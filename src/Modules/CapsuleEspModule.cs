using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// CAPSULE ESP — a real 3D capsule around each player, lit by VRChat's own glow.
	//
	// Not drawn. Every screen-space attempt at this fails for the same reason: a shape computed from
	// projected points is a shape on your screen, so it does not turn with the player, does not sit
	// in the depth buffer, and turns into a circle the moment the maths degenerates.
	//
	// The way this is actually done — read out of TevvezClientQM.dll, which does it correctly:
	//
	//   1. Every VRChat player carries a "SelectRegion" child — the capsule the game itself uses to
	//      decide you clicked on someone. It is already the right shape, the right size and in the
	//      right place, and it follows the avatar. Nothing has to be measured or guessed.
	//   2. Clone its MESH into a GameObject of our own (falling back to Unity's primitive capsule if
	//      the region has no MeshFilter), with a fully transparent material, so nothing is added to
	//      the picture on its own.
	//   3. Hand that renderer to HighlightsFX — the same post-effect VRChat uses for "Hold to Grab".
	//      The effect draws the OUTLINE of the geometry it is given, which is where the neon comes
	//      from. An invisible mesh plus an outline effect equals a glowing capsule.
	//
	// So the glow is real geometry: it turns, scales and occludes like the avatar, because it IS in
	// the scene rather than painted over it.
	public class CapsuleEspModule : IModule
	{
		public override string Name => "CapsuleEsp";

		private const string CapsuleName = "VA_CapsuleESP";
		private const int RescanFrames = 60;

		private sealed class Cap
		{
			public bool Rainbow;
			public bool Improvised;   // built without SelectRegion; upgrade when it appears
			public Transform Region;      // the player's own SelectRegion
			public GameObject Go;         // our clone
			public MeshRenderer Rend;
			public Color Col;
		}

		private readonly Dictionary<int, Cap> _caps = new Dictionary<int, Cap>();
		private int _frame;
		private bool _wasOn;

		public override void OnUpdate()
		{
			try
			{
				// SELF-GATED, like the glows. EspEnabled is the player-box feature ("Draw a box around
				// remote players"), not a master, so requiring it here made the capsule switch do
				// nothing until an unrelated one was also on.
				bool on = ModConfig.EspCapsule.Value;
				if (!on)
				{
					if (_wasOn) { ClearAll(); _wasOn = false; }
					return;
				}
				_wasOn = true;

				// Position every frame — the capsule has to sit on the player, not near them.
				foreach (var c in _caps.Values) Track(c);

				// A GRADIENT HAS TO MOVE. The colour is otherwise only set on the rescan tick, once a
				// second, which reads as a capsule that abruptly changes colour rather than one that
				// sweeps through the spectrum. Only rainbow capsules pay for this.
				Color now = TrustKit.Spectrum();
				foreach (var c in _caps.Values)
				{
					if (c == null || !c.Rainbow || c.Rend == null) continue;
					c.Col = now;
					Light(c, true);
				}

				if (++_frame < RescanFrames) return;
				_frame = 0;
				Rescan();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[CapsuleEsp] " + e.Message); }
		}

		public override void OnSceneLoaded(int buildIndex) => ClearAll();
		public override void OnShutdown() => ClearAll();

		private static bool _reported;

		private void Rescan()
		{
			var players = VRC.SDKBase.VRCPlayerApi.AllPlayers;
			if (players == null) return;

			var seen = new HashSet<int>();
			float maxDist = ModConfig.EspMaxDistance.Value;
			Vector3 me = Vector3.zero;
			try { var t = PlayerRef.LocalTransform(); if (t != null) me = t.position; } catch { }

			for (int i = 0; i < players.Count; i++)
			{
				try
				{
					var api = players[i];
					// Never yourself: your own capsule would sit in the middle of your view and tell
					// you nothing. A rainbow id is about how OTHER people's clients draw YOU.
					if (api == null || api.isLocal) continue;
					// 0 (or less) = UNLIMITED distance, same rule as every other ESP type.
					if (maxDist > 0f && Vector3.Distance(me, api.GetPosition()) > maxDist) continue;

					int id = api.playerId;
					seen.Add(id);

					// An improvised capsule is replaced as soon as VRChat's own region exists, so a
					// player who joined mid-load ends up with the exact shape like everyone else.
					if (_caps.TryGetValue(id, out var existing) && existing != null && existing.Improvised)
					{
						try
						{
							var root2 = api.gameObject != null ? api.gameObject.transform : null;
							if (root2 != null && root2.Find("SelectRegion") != null) { Remove(id); }
						}
						catch { }
					}

					if (!_caps.TryGetValue(id, out var cap) || cap == null || cap.Go == null)
					{
						cap = Build(api);
						if (cap == null) continue;
						_caps[id] = cap;
					}

					// Trust colour, re-applied on each rescan: a rank can resolve after the player
					// has already spawned, and a capsule stuck on the Visitor grey is misleading.
					// A rainbow capsule is re-lit every pass rather than only when the colour
					// CHANGES — the whole point is that it is always changing.
					if (TrustKit.IsRainbow(Core.ApiUsers.Get(api)))
					{
						cap.Rainbow = true;
						cap.Col = TrustKit.Spectrum();
						Light(cap, true);
					}
					else
					{
						cap.Rainbow = false;
						Color col = TrustKit.ColorOf(Core.ApiUsers.Get(api));
						if (col != cap.Col) { cap.Col = col; Light(cap, true); }
					}
				}
				catch { }
			}

			// Anyone who left or went out of range: take the glow off and destroy our object.
			if (_caps.Count > 0)
			{
				var gone = new List<int>();
				foreach (var kv in _caps) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
				foreach (int id in gone) Remove(id);
			}

			// One-time headline: players in range vs capsules held. "seen 6, capsules 0" points at
			// Build; "seen 0" at the roster; "capsules 6" with a blank screen at the glow, which
			// HighlightEsp now reports on separately.
			if (!_reported && seen.Count > 0)
			{
				_reported = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[CapsuleEsp] seen " + seen.Count + " player(s) in range, " + _caps.Count + " capsule(s) built.");
			}
		}

		private Cap Build(VRC.SDKBase.VRCPlayerApi api)
		{
			try
			{
				Transform root = api.gameObject != null ? api.gameObject.transform : null;
				if (root == null) return null;

				// SelectRegion is VRChat's own player capsule — already the right shape and size.
				Transform region = root.Find("SelectRegion");
				if (region == null)
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (t == null) continue;
						if (!string.Equals(t.name, "SelectRegion", StringComparison.OrdinalIgnoreCase)) continue;
						region = t; break;
					}
				}
				// NO SelectRegion, STILL A CAPSULE. Returning null here is why the capsule appeared
				// on some players and not others: that object is VRChat's own click target and it is
				// not always there — not before the avatar finishes loading, and not on every rig.
				// Giving up meant those players silently had no ESP at all, which is worse than a
				// capsule of approximate size.
				//
				// The fallback hangs off the player root instead, at a body's height and width. It
				// is replaced by the real thing on a later rescan, because a capsule built this way
				// is rebuilt whenever SelectRegion turns up.
				bool improvised = region == null;
				if (improvised) region = root;

				// Parented to SelectRegion ITSELF with an identity local transform, so our capsule is
				// that region exactly — same place, same rotation, same size, for free and forever.
				//
				// The first version parented to the player root and copied SelectRegion.lossyScale
				// into localScale. lossyScale is already the WORLD scale, so any scale on the root
				// multiplied it a second time and the capsule swallowed the screen.
				var go = new GameObject(CapsuleName);
				go.transform.SetParent(region, false);
				go.transform.localPosition = Vector3.zero;
				go.transform.localRotation = Quaternion.identity;
				go.transform.localScale = Vector3.one;

				var mf = go.AddComponent<MeshFilter>();
				var src = region.GetComponent<MeshFilter>();
				if (src != null && src.sharedMesh != null)
				{
					mf.sharedMesh = src.sharedMesh;
				}
				else
				{
					// SelectRegion is often a bare CapsuleCollider with no mesh at all. Unity's
					// primitive capsule is a FIXED radius 0.5 / height 2 — dropping that in at
					// identity scale gives a capsule that has nothing to do with the player it is
					// supposed to be around. So when there is no mesh, take the collider's own
					// radius, height and axis and scale the primitive to match it exactly.
					var prim = GameObject.CreatePrimitive(PrimitiveType.Capsule);
					mf.sharedMesh = prim.GetComponent<MeshFilter>().sharedMesh;
					UnityEngine.Object.Destroy(prim);

					var col = improvised ? null : region.GetComponent<CapsuleCollider>();
					if (col != null)
					{
						// Primitive: radius 0.5, total height 2, along Y.
						float r = Mathf.Max(0.01f, col.radius);
						float h = Mathf.Max(2f * r, col.height);

						// NOT CLAMPED. A capsule that reaches the ceiling looks like a bug and is
						// not one — some avatars really are that tall, and the capsule is meant to be
						// the player's actual size. Capping it "sensibly" would shrink a legitimate
						// avatar to hide a problem that lives somewhere else entirely: what reads as
						// wrong in a crowd is the colours blending, not the height.
						var s = new Vector3(r / 0.5f, h / 2f, r / 0.5f);
						// direction: 0 = X, 1 = Y, 2 = Z. Rotate the primitive if the collider is
						// not upright, rather than producing a capsule lying across the player.
						if (col.direction == 0) { go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f); }
						else if (col.direction == 2) { go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); }
						go.transform.localScale = s;
						go.transform.localPosition = col.center;
					}
					else
					{
						// Neither a mesh nor a capsule collider: fall back to a body-sized capsule
						// rather than Unity's 2m default, which reads as a giant bubble.
						go.transform.localScale = new Vector3(0.55f, 0.9f, 0.55f);
						// On the player root the origin is at the FEET, so an unshifted capsule sits
						// half-buried in the floor.
						if (improvised) go.transform.localPosition = new Vector3(0f, 0.9f, 0f);
					}
				}

				var rend = go.AddComponent<MeshRenderer>();
				// Fully transparent: we contribute NO pixels of our own. Everything you see is the
				// outline the highlight effect draws around this geometry.
				var mat = new Material(Shader.Find("GUI/Text Shader")) { color = new Color(0f, 0f, 0f, 0f) };

				// DRAWN THROUGH WALLS. An ESP that a wall can hide is an ESP that fails exactly when
				// it is wanted: the whole point is the player you cannot see. The depth test is
				// switched off so this geometry is never rejected for being behind something, and it
				// is pushed past the opaque queue so it is submitted after the world is drawn.
				try
				{
					// RENDER LAST, AND STACK PROPERLY.
					//
					// Two separate things were being confused. Ignoring the world's depth is what
					// lets a capsule show through a wall — that part was right. But ZWrite was off,
					// so the capsules did not write depth EITHER, and every one of them composited
					// on top of every other: five players in a line gave one smear of mixed colour
					// instead of five capsules.
					//
					// Writing depth while still ignoring the world's fixes exactly that. At queue
					// 9999 Unity sorts these back-to-front like transparent geometry, so the nearer
					// capsule draws last and COVERS the farther one instead of blending with it —
					// they superimpose, each keeping its own colour, and all of them still show
					// through the level.
					bool through = true;
					try { through = ModConfig.EspThroughWalls.Value; } catch { }

					mat.SetInt("_ZTest", (int)(through
						? UnityEngine.Rendering.CompareFunction.Always
						: UnityEngine.Rendering.CompareFunction.LessEqual));
					mat.SetInt("_ZWrite", 1);                 // always: this is what stops the smearing
					mat.renderQueue = through ? 9999 : 2500;  // last of everything, or with the world
				}
				catch { }

				rend.material = mat;
				rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				rend.receiveShadows = false;

				var cap = new Cap { Region = region, Go = go, Rend = rend, Col = Color.white, Improvised = improvised };
				Track(cap);
				Light(cap, true);
				return cap;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[CapsuleEsp] build: " + e.Message);
				return null;
			}
		}

		// Nothing to do per frame any more: being a child of SelectRegion with an identity local
		// transform means it follows on its own. Kept only to put the identity back if something in
		// the world ever moves our object.
		private static void Track(Cap c)
		{
			try
			{
				if (c?.Region == null || c.Go == null) return;
				// Nothing to re-assert when the mesh came from SelectRegion itself: identity local
				// TRS on a child of the region is already exact. The collider-derived case owns its
				// own scale and offset, so leave those alone too.
				if (c.Go.transform.parent != c.Region) c.Go.transform.SetParent(c.Region, false);
			}
			catch { }
		}

		// HighlightsFX is reached through HighlightEspModule, which already resolves it by
		// reflection and copes with the builds where it is missing.
		private static void Light(Cap c, bool on)
		{
			try
			{
				if (c?.Rend == null) return;
				if (on) HighlightEspModule.Highlight(c.Rend, c.Col);
				else HighlightEspModule.Unhighlight(c.Rend);
			}
			catch { }
		}

		private void Remove(int id)
		{
			if (!_caps.TryGetValue(id, out var c)) return;
			try { Light(c, false); } catch { }
			try { if (c?.Go != null) UnityEngine.Object.Destroy(c.Go); } catch { }
			_caps.Remove(id);
		}

		private void ClearAll()
		{
			var ids = new List<int>(_caps.Keys);
			foreach (int id in ids) Remove(id);
		}
	}
}
