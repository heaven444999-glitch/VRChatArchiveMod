using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Highlight ESP: glows the outline of each remote player's actual avatar mesh, using
	// VRChat's OWN highlight post-effect (HighlightsFX, the one the game uses for pickups).
	//
	// Why this beats a drawn box: the glow is the real geometry, so it follows the player's
	// rotation, pose and shape exactly instead of a screen-aligned rectangle that looks
	// wrong the moment someone turns. It is still pure visualisation of players your client
	// already renders — nothing is targeted, tracked, or sent anywhere.
	//
	// Everything is reflection-resolved and guarded: on a build where the effect is missing
	// the module simply does nothing instead of throwing.
	public class HighlightEspModule : IModule
	{
		public override string Name => "HighlightEsp";

		private const int ScanIntervalFrames = 45;
		private const float WorldIntervalSec = 6f;      // pickups and portals change on human timescales
		private float _nextWorld;                       // realtime of the next world pass
		private const int MaxRenderersPerPlayer = 24;   // avatars can have dozens; cap the cost

		private static Type _fxType;
		private static MethodInfo _addWithColor;   // (Renderer, Color, bool)
		private static MethodInfo _removeRenderer; // (Renderer, bool) or (Renderer)
		private static bool _resolved;
		private static object _fx;                 // the live HighlightsFX instance
		private static string _how = "?";          // which lookup found it, for diagnosing other machines

		// uid -> renderers we registered, so they can be cleared when the player leaves
		private readonly Dictionary<string, List<Renderer>> _applied =
			new Dictionary<string, List<Renderer>>(StringComparer.OrdinalIgnoreCase);
		private int _frame;
		private bool _wasEnabled;
		private bool _wasPlayers;   // glow was on last frame (for off->on instant-enable)
		private bool _wasWorld;     // portals/items were on last frame

		public override void OnInitialize()
		{
			if (ModConfig.EspHighlight.Value)
				VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] armed — outline glow via VRChat's own HighlightsFX.");
		}

		public override void OnUpdate()
		{
			try
			{
				// THREE INDEPENDENT USERS OF THE SAME EFFECT: the avatar glow, portals and pickups.
				// This used to return early whenever the AVATAR glow was off — and since that
				// setting had no toggle anywhere in the menu and defaults to false, the whole module
				// was dead on arrival: portals and items could be switched on and would still never
				// light up, because their pass was never reached. Each part now gates itself.
				// NO MASTER SWITCH HERE, and the second attempt at one is why this had to be undone.
				//
				// EspEnabled was folded in as a kill-all, which sounds tidy until you read what that
				// setting actually is: "Draw a box around remote players". It is the player-box
				// feature, not a master. Gating the glows behind it meant switching on "Glow on
				// pickups" — from the QuickMenu or from the desktop client — set the value, synced it
				// correctly, showed ON, and lit nothing, because a different switch nobody mentioned
				// was off. That reads as "the toggles are broken" from either surface, and no amount
				// of sync work could have fixed it, because the sync was never wrong.
				//
				// Each effect gates itself on its own setting. One switch, one thing.
				bool wantPlayers = ModConfig.EspHighlight.Value;
				bool wantWorld = ModConfig.EspPortals.Value || ModConfig.EspItems.Value;

				if (!wantPlayers && !wantWorld)
				{
					if (_wasEnabled) { ClearAll(); _wasEnabled = false; }
					_wasPlayers = false; _wasWorld = false;
					FeatureHealth.Idle("ESP/Items", "off");
					FeatureHealth.Idle("ESP/Highlight", "off");
					FeatureHealth.Idle("ESP/Portals", "off");
					return;
				}

				// THE ANSWER TO "I TURNED IT ON AND NOTHING HAPPENED".
				//
				// Without HighlightsFX every call below succeeds and draws nothing, which is exactly
				// the failure that reads as a broken toggle. Said once, in a sentence the client can
				// put under the switch, instead of leaving the user to guess.
				if (!Resolve())
				{
					string why = "on, but VRChat's highlight effect is not available yet — nothing will glow";
					if (ModConfig.EspItems.Value) FeatureHealth.Broken("ESP/Items", why);
					if (ModConfig.EspHighlight.Value) FeatureHealth.Broken("ESP/Highlight", why);
					if (ModConfig.EspPortals.Value) FeatureHealth.Broken("ESP/Portals", why);
					return;
				}
				_wasEnabled = true;

				// A DISABLE MUST NOT WAIT FOR THE SCAN TICK. Clearing was behind the 45-frame gate,
				// so switching the glow off left it on for up to a second — long enough to read as
				// "it did not turn off". The clear happens every frame; only the expensive re-scan
				// is throttled.
				if (!wantPlayers && _applied.Count > 0) ClearPlayers();
				if (!wantWorld && _worldApplied.Count > 0) ClearWorld();

				// AN ENABLE MUST ALSO BE INSTANT. Flipping a glow on used to do nothing until the next
				// 45-frame tick (and up to 6 s for the world pass), which reads as a dead switch. On an
				// off->on transition, force the relevant pass to run this frame.
				if (wantPlayers && !_wasPlayers) _frame = ScanIntervalFrames;
				if (wantWorld && !_wasWorld) { _nextWorld = 0f; _frame = ScanIntervalFrames; }
				_wasPlayers = wantPlayers; _wasWorld = wantWorld;

				if (++_frame < ScanIntervalFrames) return;
				_frame = 0;

				if (wantPlayers) Pass();

				// WORLD PASS ON A REAL CLOCK, not the player frame counter. It used to increment a
				// frame counter that ONLY advanced once per 45-frame player cycle — so "every 600"
				// meant every 600*45 = 27000 frames, about SEVEN MINUTES. Items and portals almost
				// never lit up. A realtime interval fixes that: pickups and portals genuinely do
				// change on a human timescale, so a few seconds is right and still cheap.
				float rt = Time.realtimeSinceStartup;
				if (wantWorld && rt >= _nextWorld) { _nextWorld = rt + WorldIntervalSec; WorldPass(); }
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[HighlightEsp] update threw: {e}"); }
		}

		public override void OnShutdown() => ClearAll();

		// A new world has new pickups and portals, and every renderer we were holding belongs to a
		// scene that no longer exists. Forget them without touching them, and make the next tick do
		// a world pass immediately rather than waiting out the ten-second timer in an empty world.
		public override void OnSceneLoaded(int buildIndex)
		{
			_applied.Clear();
			_worldApplied.Clear();
			_nextWorld = 0f;               // force a world pass immediately in the new world
			_frame = ScanIntervalFrames;

			// AND THE EFFECT ITSELF, which was being carried into the new world.
			//
			// _fx is a plain object reference, so it stays non-null after Unity destroys the thing
			// behind it — and Resolve() answers "already have it" on that basis. From the second
			// world onwards every glow call therefore went to a destroyed effect and did nothing,
			// silently, which is the "it works sometimes" half of this feature. Dropping it here
			// forces an honest re-resolve, and also keeps us from invoking a method on a dead proxy.
			_fx = null;
			_how = null;
			_nextFxScan = 0f;
			_firstFxTry = 0f;
			_warnedNoFx = false;
		}

		// PORTALS AND PICKUPS — the same glow, on the object itself.
		//
		// These used to be screen-space diamonds with a name and a distance. In a real world that is
		// dozens of labels stacked over each other and over the players, and it told you less than
		// nothing. Lighting the object's own geometry says "that thing, there" with no text at all,
		// and it is exactly what VRChat itself does for a grabbable ("Hold to Grab").
		private static readonly Color PortalGlow = new Color(0.66f, 0.36f, 1f);
		private static readonly Color ItemGlow = new Color(1f, 0.72f, 0.25f);
		private const int MaxRenderersPerObject = 8;

		private readonly Dictionary<int, List<Renderer>> _worldApplied = new Dictionary<int, List<Renderer>>();

		private void WorldPass()
		{
			try
			{
				bool wantPortals = ModConfig.EspPortals.Value;
				bool wantItems = ModConfig.EspItems.Value;
				if (!wantPortals && !wantItems)
				{
					if (_worldApplied.Count > 0) ClearWorld();
					return;
				}
				if (!Resolve()) return;

				var seen = new HashSet<int>();

				if (wantItems)
				{
					// A pickup IS the VRC_Pickup component — nothing has to be guessed.
					try
					{
						foreach (var p in UnityEngine.Object.FindObjectsOfType<VRC.SDKBase.VRC_Pickup>())
						{
							if (p == null) continue;
							Glow(p.transform, ItemGlow, seen);
						}
					}
					catch { }
				}

				if (wantPortals)
				{
					try
					{
						// FindObjectsOfType, NOT Resources.FindObjectsOfTypeAll. The latter returns
						// every Transform the process has loaded — inactive objects, prefabs and
						// asset-only hierarchies included, hundreds of thousands of them in a heavy
						// world — and it was the single most expensive call in the whole mod. This
						// one returns only live scene objects, which is all a portal can ever be.
						foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
						{
							if (t == null) continue;
							if ((t.name ?? "").IndexOf("Portal", StringComparison.OrdinalIgnoreCase) < 0) continue;
							try
							{
								if (!t.gameObject.activeInHierarchy) continue;
							}
							catch { continue; }
							// Roots only: a portal is a small tree and every child matches the name.
							if (t.parent != null && (t.parent.name ?? "").IndexOf("Portal", StringComparison.OrdinalIgnoreCase) >= 0) continue;
							Glow(t, PortalGlow, seen);
						}
					}
					catch { }
				}

				// Anything that left the world (a portal closed, a pickup destroyed) stops glowing.
				if (_worldApplied.Count > 0)
				{
					var gone = new List<int>();
					foreach (var kv in _worldApplied) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
					foreach (int id in gone) ClearWorldOne(id);
				}

				// THE COUNT IS THE PROOF. "on — 3 objects glowing" answers the question a toggle
				// cannot: whether it did anything. Zero in a world with no pickups is honest too,
				// and is a very different report from the effect being unavailable.
				if (wantItems)
					FeatureHealth.Ok("ESP/Items", _worldApplied.Count == 0
						? "on — no grabbable pickups in this world"
						: "on — " + _worldApplied.Count + " object(s) glowing");
				if (wantPortals)
					FeatureHealth.Ok("ESP/Portals", "on — watching for portals");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] world pass: " + e.Message); }
		}

		private void Glow(Transform t, Color col, HashSet<int> seen)
		{
			try
			{
				if (t == null) return;
				int id = t.GetInstanceID();
				seen.Add(id);
				if (_worldApplied.ContainsKey(id)) return;      // already glowing

				var list = new List<Renderer>();
				var rs = t.GetComponentsInChildren<Renderer>(false);
				if (rs == null) return;
				for (int i = 0; i < rs.Length && list.Count < MaxRenderersPerObject; i++)
				{
					var rnd = rs[i];
					if (rnd == null) continue;
					try { _addWithColor.Invoke(_fx, new object[] { rnd, col, true }); list.Add(rnd); }
					catch { }
				}
				if (list.Count > 0) _worldApplied[id] = list;
			}
			catch { }
		}

		private void ClearWorldOne(int id)
		{
			if (!_worldApplied.TryGetValue(id, out var list)) return;
			foreach (var rnd in list)
			{
				Unhighlight(rnd);
			}
			_worldApplied.Remove(id);
		}

		private void ClearWorld()
		{
			var ids = new List<int>(_worldApplied.Keys);
			foreach (int id in ids) ClearWorldOne(id);
		}

		// Shared with CapsuleEspModule, which lights an invisible capsule mesh of its own. The
		// resolution of HighlightsFX and its two obfuscated methods lives here and only here.
		private static bool _litOnce;
		private static string _lastBlame;
		private static void Blame(string why)
		{
			if (why == _lastBlame) return;
			_lastBlame = why;
			// Said once per distinct reason: this runs per renderer per frame, so anything louder
			// would bury the log. It is the difference between "the glow is off" and "the glow tried
			// and failed", which need different fixes.
			VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] glow not applied: " + why + ".");
		}

		public static bool Highlight(Renderer rnd, Color col)
		{
			try
			{
				if (rnd == null) return false;
				if (!Resolve()) { Blame("HighlightsFX did not resolve"); return false; }
				_addWithColor.Invoke(_fx, new object[] { rnd, col, true });
				if (!_litOnce) { _litOnce = true; VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] first renderer lit — the glow path works."); }
				return true;
			}
			catch (Exception e) { Blame("add-renderer threw: " + Core.Unwrap.Describe(e)); return false; }
		}

		// TURN THE GLOW OFF, RELIABLY. The dedicated remove method is resolved by signature and on
		// some builds that resolution misses — and when it did, this returned silently and the glow
		// stayed on after you disabled it. The fallback cannot miss: re-add the renderer with the
		// effect's on/off flag set to FALSE, the same call that lit it, so if lighting worked,
		// un-lighting works too.
		public static void Unhighlight(Renderer rnd)
		{
			if (rnd == null || _fx == null) return;
			try
			{
				if (_removeRenderer != null)
				{
					var ps = _removeRenderer.GetParameters();
					_removeRenderer.Invoke(_fx, ps.Length == 2 ? new object[] { rnd, false } : new object[] { rnd });
					return;
				}
			}
			catch { }
			try { if (_addWithColor != null) _addWithColor.Invoke(_fx, new object[] { rnd, Color.clear, false }); } catch { }
		}

		private void Pass()
		{
			if (!Resolve()) return;

			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (object player in FewTagsModule.EnumeratePlayers())
			{
				try
				{
					var comp = player as Component;
					if (comp == null) continue;

					// skip ourselves — highlighting your own avatar just fogs your view
					Transform localT = PlayerRef.LocalTransform();
					if (localT != null && comp.transform == localT) continue;

					string uid = FewTagsModule.UserIdOf(player) ?? comp.GetInstanceID().ToString();
					seen.Add(uid);
					// STALE CACHE: once a uid was glowing we never touched it again — so after an
					// avatar swap the old renderers are destroyed and the NEW mesh never lights up,
					// and the glow silently drops off. If any stored renderer has died, forget the uid
					// so this pass rebuilds it against the current avatar.
					if (_applied.TryGetValue(uid, out var had))
					{
						bool dead = false;
						for (int k = 0; k < had.Count; k++) { if (had[k] == null) { dead = true; break; } }
						if (dead) Remove(uid); else continue;   // still valid → already glowing
					}

					object apiUser = FewTagsModule.GetMemberByTypeName(player, "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
					Color col = TrustKit.ColorOf(apiUser);

					var list = new List<Renderer>();
					var renderers = comp.GetComponentsInChildren<Renderer>(false);
					if (renderers != null)
					{
						for (int i = 0; i < renderers.Length && list.Count < MaxRenderersPerPlayer; i++)
						{
							var rnd = renderers[i];
							if (rnd == null) continue;
							try { _addWithColor.Invoke(_fx, new object[] { rnd, col, true }); list.Add(rnd); }
							catch { }
						}
					}
					if (list.Count > 0) _applied[uid] = list;
				}
				catch { }
			}

			// players who left: drop their highlights
			List<string> gone = null;
			foreach (var kv in _applied) if (!seen.Contains(kv.Key)) (gone ??= new List<string>()).Add(kv.Key);
			if (gone != null) foreach (string uid in gone) Remove(uid);
		}

		private void Remove(string uid)
		{
			if (!_applied.TryGetValue(uid, out var list)) return;
			foreach (var rnd in list) Unhighlight(rnd);
			_applied.Remove(uid);
		}


		// Just the avatar glow, leaving portals and pickups alone — needed now that the three parts
		// switch on and off independently of each other.
		private void ClearPlayers()
		{
			var keys = new List<string>(_applied.Keys);
			foreach (string uid in keys) Remove(uid);
			_applied.Clear();
		}

		// EVERYTHING this module ever lit, players AND world. It used to clear only the players,
		// so a shutdown (or switching the module off) left every portal and pickup still glowing
		// with nothing left that knew how to un-glow them.
		private void ClearAll()
		{
			ClearPlayers();
			ClearWorld();
		}

		// HighlightsFX: static instance getter first, then the one on the main camera,
		// then a scene scan; as a last resort we attach HighlightsFXStandalone ourselves
		// (the approach other clients use).
		// Rate-limit + grace period for the instance hunt above.
		private static float _nextFxScan, _firstFxTry;
		private static bool _warnedNoFx;
		private const float GiveUpAfter = 30f;

		private static bool Resolve()
		{
			// A CACHED EFFECT IS ONLY GOOD WHILE IT IS ALIVE. Unity destroying it does not null this
			// reference, so the liveness is asked of the object rather than of the pointer.
			if (_fx != null && _addWithColor != null)
			{
				try
				{
					var live = (_fx as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Behaviour>();
					if (live != null) return true;
				}
				catch { }
				_fx = null;   // destroyed or no longer castable: hunt for it again
			}
			if (!_resolved)
			{
				_resolved = true;
				try
				{
					var asm = Assembly.Load("Assembly-CSharp");
					_fxType = asm.GetType("HighlightsFX");
					if (_fxType == null) { VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] HighlightsFX type not found on this build."); return false; }

					foreach (var m in _fxType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
					{
						var ps = m.GetParameters();
						if (_addWithColor == null && ps.Length == 3
							&& ps[0].ParameterType.Name == "Renderer" && ps[1].ParameterType.Name == "Color" && ps[2].ParameterType == typeof(bool))
							_addWithColor = m;
						if (_removeRenderer == null && ps.Length == 2
							&& ps[0].ParameterType.Name == "Renderer" && ps[1].ParameterType == typeof(bool))
							_removeRenderer = m;
					}
					if (_removeRenderer == null)
						foreach (var m in _fxType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
						{
							var ps = m.GetParameters();
							if (ps.Length == 1 && ps[0].ParameterType.Name == "Renderer" && m.ReturnType == typeof(void)) { _removeRenderer = m; break; }
						}
					if (_addWithColor == null)
						VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] no (Renderer, Color, bool) highlight method — outline ESP unavailable.");
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] resolve failed: {e.Message}"); }
			}
			if (_fxType == null || _addWithColor == null) return false;

			// NOT YET IS NOT THE SAME AS NEVER, and this used to report them as the same thing.
			//
			// Resolve() is called from three places, every frame, and while the effect has not been
			// found it ran the whole hunt again each time — a static-getter probe, a GetComponent on
			// the camera, and a FindObjectOfType scan of the scene — then logged
			// "HighlightsFX could NOT be resolved ... capsule and item glow will do nothing".
			//
			// Both halves of that were wrong. HighlightsFX simply does not exist during loading, so
			// the early misses are normal and the warning was a false alarm that sent people looking
			// for a broken feature; the log then said "resolved via static getter" a moment later
			// and contradicted itself. And the scan is not free: the profiler put this module at
			// 47 ms per second, spent almost entirely on failing to find something not there yet.
			//
			// So: the hunt is rate-limited to once a second while it fails, and the warning waits
			// until it has genuinely been failing for a while before saying anything at all.
			float now = Time.realtimeSinceStartup;
			if (_firstFxTry <= 0f) _firstFxTry = now;
			if (now < _nextFxScan) return false;
			_nextFxScan = now + 1f;

			try
			{
				// 1) static instance getter
				foreach (var p in _fxType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (p.GetParameters().Length != 0 || p.ReturnType.Name != "HighlightsFX") continue;
					try { _fx = p.Invoke(null, null); } catch { }
					if (_fx != null) { _how = "static getter"; break; }
				}
				// 2) on the main camera / anywhere in the scene
				if (_fx == null)
				{
					var il2 = Il2CppType.From(_fxType);
					var cam = Camera.main;
					if (cam != null) { _fx = cam.GetComponent(il2) ?? cam.GetComponentInChildren(il2, true); if (_fx != null) _how = "main camera"; }
					if (_fx == null) { _fx = UnityEngine.Object.FindObjectOfType(il2); if (_fx != null) _how = "scene scan"; }
					// 3) attach one ourselves
					if (_fx == null && cam != null)
					{
						var standalone = Assembly.Load("Assembly-CSharp").GetType("HighlightsFXStandalone");
						if (standalone != null) { _fx = cam.gameObject.AddComponent(Il2CppType.From(standalone)); if (_fx != null) _how = "standalone attached by us"; }
					}
				}
				if (_fx != null)
				{
					// FOUND IS NOT THE SAME AS RUNNING. The glow works on one machine and not another
					// because HighlightsFX can be present but DISABLED — VRChat switches it off with
					// certain graphics settings, and a disabled post-effect draws nothing while every
					// call we make into it still succeeds silently. So it is switched on explicitly,
					// and the resolution path is logged: when someone reports no glow, their log now
					// says whether the effect was found at all and by which route.
					try
					{
						var beh = (_fx as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Behaviour>();
						if (beh != null && !beh.enabled)
						{
							beh.enabled = true;
							VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] the effect was disabled — switched on.");
						}
						var go2 = beh != null ? beh.gameObject : null;
						if (go2 != null && !go2.activeInHierarchy)
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[HighlightEsp] the effect's object is inactive — the glow will not draw.");
					}
					catch { }

					VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] highlight effect resolved via " + _how + ".");
				}
				else if (!_warnedNoFx && now - _firstFxTry >= GiveUpAfter)
				{
					// Once, and only once it has really been missing for half a minute — by which
					// point a world is long since loaded and "not yet" is no longer an explanation.
					_warnedNoFx = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[HighlightEsp] HighlightsFX still not found after " + (int)GiveUpAfter + "s — capsule and "
						+ "item glow will do nothing on this machine. Type found: " + (_fxType != null)
						+ ", add method: " + (_addWithColor != null) + ". Still retrying once a second.");
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] instance lookup failed: {e.Message}"); }

			return _fx != null;
		}
	}
}
