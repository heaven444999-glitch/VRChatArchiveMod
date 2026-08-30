using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PUT A URL IN THE WORLD'S VIDEO PLAYER.
	//
	// This drives the same mechanism the world's own URL box drives: a video player is an
	// UdonBehaviour holding a VRCUrl variable, and playing something means writing that variable
	// and firing the player's "load this" event. Nothing here is a hole in VRChat — the field is
	// meant to be written, and Udon's own whitelist (which stops a SCRIPT from fabricating a
	// VRCUrl) does not apply to us because we construct it natively.
	//
	// IT IS SYNCED, SO IT IS EVERYONE'S BUSINESS. Ownership is taken and the play event is sent
	// over the network, exactly as pressing the world's button does — so the whole instance hears
	// it. That is what the feature is for, but it also means many worlds gate it to the instance
	// owner and many communities read it as trolling. The mod does not soften that; it just does
	// what it was asked, and says who it reached.
	public class VideoUrlModule : IModule
	{
		public override string Name => "VideoUrl";

		public static string LastStatus = "";
		public static string LastUrl = "";

		// Every video-player system names its "start playing" entry point differently, and a world
		// can ship any of them. They are tried in order and the ones that do not exist are simply
		// ignored — an UdonBehaviour rejects an event it has no entry point for.
		private static readonly string[] PlayEvents =
		{
			"_ChangeMedia",        // ProTV
			"OnURLChanged",        // USharpVideo
			"_TriggerPlay",        // VideoTXL / USharpVideo
			"_UrlChanged",
			"PlayVideo", "_Play", "Play", "_PlayVideo",
			"OnURLInput", "_OnURLInput",
		};

		private static Type _udonType;
		private static Type _urlType;
		private static ConstructorInfo _urlCtor;

		public override void OnInitialize()
		{
			VRChatArchiveModPlugin.Logger.LogInfo("[VideoUrl] armed — URL injection into the world's video player.");
		}

		private static Type FindType(string full)
		{
			try
			{
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					Type t;
					try { t = asm.GetType(full, false); }
					catch { continue; }
					if (t != null) return t;
				}
			}
			catch { }
			return null;
		}

		private static bool Resolve()
		{
			if (_udonType == null)
				_udonType = Type.GetType("VRC.Udon.UdonBehaviour, VRC.Udon", false)
					?? FindType("VRC.Udon.UdonBehaviour");

			if (_urlType == null)
				_urlType = FindType("VRC.SDKBase.VRCUrl") ?? FindType("VRCUrl");

			if (_urlCtor == null && _urlType != null)
			{
				try { _urlCtor = _urlType.GetConstructor(new[] { typeof(string) }); }
				catch { }
			}
			return _udonType != null && _urlType != null && _urlCtor != null;
		}

		/// <summary>
		/// Writes <paramref name="url"/> into every video player found and asks them to play it.
		/// Returns the number of players reached.
		/// </summary>
		public static int Inject(string url)
		{
			LastUrl = url ?? "";
			if (string.IsNullOrWhiteSpace(url))
			{
				LastStatus = "no URL to send";
				return 0;
			}
			// A bare word would be written into the player and fail there instead of here, with no
			// explanation anywhere the user can see.
			if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				&& !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
				&& !url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
			{
				LastStatus = "that is not a URL — it has to start with http:// or https://";
				return 0;
			}

			if (!Resolve())
			{
				LastStatus = "this build exposes no Udon/VRCUrl types — cannot reach the player";
				return 0;
			}

			object vrcUrl;
			try { vrcUrl = _urlCtor.Invoke(new object[] { url }); }
			catch (Exception e)
			{
				LastStatus = "could not build a VRCUrl: " + Short(e.Message);
				return 0;
			}

			int reached = 0;

			// PRIMARY PATH: DRIVE THE ACTUAL VIDEO COMPONENT.
			//
			// Every world player — USharpVideo, ProTV, VideoTXL, and any custom one — ultimately
			// hands its URL to a BaseVRCVideoPlayer (the SDK's VRCUnityVideoPlayer / AVPro player),
			// and that base type has a real method: LoadURL(VRCUrl). Calling it plays the video on
			// that player directly, on any world, with no dependency on the Udon script's variable
			// names. This is why "no VRCUrl field" was a dead end — the URL lives on the COMPONENT,
			// not necessarily as a readable Udon public variable.
			reached += DriveVideoComponents(url);

			// SECONDARY PATH: the Udon variable route, for players that also expose a settable URL
			// symbol and a play event (this is what carries the SYNC to other players).
			int syncedReached = 0;
			try
			{
				var il2 = Il2CppType.From(_udonType);
				var all = UnityEngine.Object.FindObjectsOfType(il2);
				if (all != null)
					for (int i = 0; i < all.Length; i++)
					{
						var ub = all[i];
						if (ub == null || !NativeGuard.Alive(ub)) continue;
						string symbol = FindUrlSymbol(ub);
						if (symbol == null) continue;
						if (TrySet(ub, symbol, vrcUrl)) syncedReached++;
					}
			}
			catch { }

			if (reached == 0 && syncedReached == 0)
			{
				DumpVideoTargetsOnce();
				LastStatus = "no video player found in this world (subtree dumped for tuning)";
			}
			else
			{
				LastStatus = reached + " player(s) started"
					+ (syncedReached > 0 ? ", " + syncedReached + " synced to everyone" : " (local — the world's sync may not follow)");
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[VideoUrl] " + LastStatus + " :: " + url);
			return reached + syncedReached;
		}

		private static Type _basePlayerType;
		private static System.Reflection.MethodInfo _loadUrl;
		private static System.Reflection.ConstructorInfo _playerCtor;
		private static System.Reflection.ConstructorInfo _urlParamCtor;
		private static bool _videoDumped;

		// Finds every BaseVRCVideoPlayer in the world and calls LoadURL(vrcUrl) on it.
		private static int DriveVideoComponents(object urlString)
		{
			int n = 0;
			try
			{
				if (_basePlayerType == null)
					_basePlayerType = FindType("VRC.SDK3.Video.Components.Base.BaseVRCVideoPlayer")
						?? FindType("BaseVRCVideoPlayer");
				if (_basePlayerType == null) return 0;

				if (_loadUrl == null)
				{
					foreach (var m in _basePlayerType.GetMethods())
					{
						if (m.Name != "LoadURL") continue;
						if (m.GetParameters().Length == 1) { _loadUrl = m; break; }
					}
				}
				if (_loadUrl == null) return 0;

				// BUILD THE VRCUrl FROM LoadURL'S OWN PARAMETER TYPE.
				//
				// "Object does not match target type" was two mismatches at once: the argument was a
				// VRCUrl of a type resolved elsewhere, not necessarily the exact type this method's
				// signature names. Constructing it from LoadURL's own parameter type guarantees the
				// argument matches.
				if (_urlParamCtor == null)
				{
					Type pt = _loadUrl.GetParameters()[0].ParameterType;
					_urlParamCtor = pt.GetConstructor(new[] { typeof(string) });
				}
				if (_urlParamCtor == null) return 0;
				object vrcUrl;
				try { vrcUrl = _urlParamCtor.Invoke(new object[] { (string)urlString }); }
				catch { return 0; }

				if (_playerCtor == null)
					_playerCtor = _basePlayerType.GetConstructor(new[] { typeof(IntPtr) });
				if (_playerCtor == null) return 0;

				var il2 = Il2CppType.From(_basePlayerType);
				var players = UnityEngine.Object.FindObjectsOfType(il2);
				if (players == null) return 0;

				for (int i = 0; i < players.Length; i++)
				{
					var p = players[i];
					if (p == null || !NativeGuard.Alive(p)) continue;
					try
					{
						// RE-WRAP AT THE EXACT TYPE. FindObjectsOfType hands back UnityEngine.Object
						// wrappers; a reflected Invoke needs the instance to be the method's declaring
						// type. Reconstruct the proxy of _basePlayerType over the same il2cpp pointer,
						// exactly the fix used elsewhere in the mod for the same trap.
						IntPtr ptr = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)p).Pointer;
						if (ptr == IntPtr.Zero) continue;
						object typed = _playerCtor.Invoke(new object[] { ptr });
						_loadUrl.Invoke(typed, new object[] { vrcUrl });
						n++;
					}
					catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] LoadURL threw: " + Short(e.Message)); }
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] component path threw: " + Short(e.Message)); }
			return n;
		}

		// One-time diagnostic when nothing matched: name every BaseVRCVideoPlayer and every
		// UdonBehaviour with its variable symbols, so the real target can be read off the log.
		private static void DumpVideoTargetsOnce()
		{
			if (_videoDumped) return;
			_videoDumped = true;
			try
			{
				if (_basePlayerType != null)
				{
					var players = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_basePlayerType));
					VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] BaseVRCVideoPlayer count: " + (players?.Length ?? 0));
				}
				else VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] BaseVRCVideoPlayer TYPE not found in interop.");

				var uAll = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_udonType));
				VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] UdonBehaviour count: " + (uAll?.Length ?? 0));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] dump threw: " + Short(e.Message)); }
		}

		// THE FIELD IS FOUND BY TYPE, NOT BY NAME. USharpVideo, ProTV and VideoTXL each call their
		// URL variable something different, and a world can ship a custom player that calls it
		// anything at all. What they cannot vary is the TYPE: a video URL is a VRCUrl. So every
		// public variable is read and the first VRCUrl-typed one wins.
		private static string FindUrlSymbol(Il2CppObjectBase ub)
		{
			try
			{
				var vars = _udonType.GetProperty("publicVariables")?.GetValue(ub);
				if (vars == null) return null;

				var symbolsProp = vars.GetType().GetProperty("VariableSymbols");
				var symbols = symbolsProp?.GetValue(vars) as System.Collections.IEnumerable;
				if (symbols == null) return null;

				// Match by the DECLARED TYPE, never by reading the live value. The old path called
				// GetProgramVariable(sym) then Il2CppNameOf(value) on EVERY symbol of EVERY
				// UdonBehaviour in the world -- and reading the type of a variable whose value wraps a
				// dead/uninitialised il2cpp object is an access violation no try/catch survives. That
				// is the crash this module caused. GetProgramVariableType returns type metadata only
				// (never a live object), so it is safe on every symbol.
				var getType = _udonType.GetMethod("GetProgramVariableType", new[] { typeof(string) });
				if (getType == null) return null;

				foreach (object s in symbols)
				{
					string sym = s as string ?? s?.ToString();
					if (string.IsNullOrEmpty(sym)) continue;
					object t;
					try { t = getType.Invoke(ub, new object[] { sym }); }
					catch { continue; }
					if (string.Equals(TypeNameOf(t), "VRCUrl", StringComparison.Ordinal)) return sym;
				}
			}
			catch { }
			return null;
		}

		// GetProgramVariableType hands back a managed Type on some builds and an il2cpp Type proxy on
		// others; both answer to Name. Reading Name is metadata only -- it never touches a live value.
		private static string TypeNameOf(object t)
		{
			if (t == null) return "?";
			if (t is Type mt) return mt.Name ?? "?";
			try
			{
				string s = t.GetType().GetProperty("Name")?.GetValue(t) as string;
				if (!string.IsNullOrEmpty(s)) return s;
			}
			catch { }
			return "?";
		}

		private static bool TrySet(Il2CppObjectBase ub, string symbol, object vrcUrl)
		{
			try
			{
				GameObject go = null;
				try { go = (_udonType.GetProperty("gameObject")?.GetValue(ub)) as GameObject; }
				catch { }

				// SYNCED: take the object first, or the write is overwritten by whoever owns it on
				// their next sync tick and nobody else ever hears it. Gated so we NEVER SetOwner on
				// an object with no network state (that takes the game down): a VRCObjectSync/Pickup,
				// OR — crucially for USharpVideo/ProTV — an UdonBehaviour that actually SYNCS (a
				// Manual/Continuous SyncMethod). Those players carry their URL on their OWN synced
				// UdonBehaviour, no ObjectSync, so without this the set never reaches other clients
				// and the video "won't load".
				if (go != null && (IsNetworked(go) || UdonHasSync(ub))) TakeOwnership(go);

				var setVar = _udonType.GetMethod("SetProgramVariable", new[] { typeof(string), typeof(object) })
					?? _udonType.GetMethod("SetProgramVariable");
				if (setVar == null) return false;
				setVar.Invoke(ub, new object[] { symbol, vrcUrl });

				// Then tell it to load what it now holds. Sent over the network so the instance
				// follows; the local call is the fallback for players that expose no network entry.
				var sendNet = _udonType.GetMethod("SendCustomNetworkEvent");
				var send = _udonType.GetMethod("SendCustomEvent", new[] { typeof(string) });

				bool any = false;
				foreach (string ev in PlayEvents)
				{
					// VRChat refuses to network any '_'-prefixed entry point; only broadcast eligible
					// ones, and run the rest locally.
					if (sendNet != null && ev.Length > 0 && ev[0] != '_' && TryNetworkEvent(sendNet, ub, ev)) any = true;
					if (send != null)
					{
						try { send.Invoke(ub, new object[] { ev }); any = true; }
						catch { }
					}
				}
				return any;
			}
			catch { return false; }
		}

		// SendCustomNetworkEvent's first parameter is an enum (NetworkEventTarget); its value for
		// "All" is 0 on every build so far, but it is read off the enum rather than assumed.
		private static bool TryNetworkEvent(MethodInfo m, Il2CppObjectBase ub, string ev)
		{
			try
			{
				var ps = m.GetParameters();
				if (ps.Length != 2) return false;
				object target;
				try { target = Enum.Parse(ps[0].ParameterType, "All", true); }
				catch { target = Activator.CreateInstance(ps[0].ParameterType); }
				m.Invoke(ub, new[] { target, (object)ev });
				return true;
			}
			catch { return false; }
		}

		// True only when this UdonBehaviour actually synchronises (Manual/Continuous SyncMethod),
		// which is what gives it the network state SetOwner needs. Anything unclear -> false, so we
		// never SetOwner on a non-networked behaviour (the crash). USharpVideo/ProTV sync this way.
		private static bool UdonHasSync(Il2CppObjectBase ub)
		{
			try
			{
				object sm = _udonType.GetProperty("SyncMethod")?.GetValue(ub);
				if (sm == null) return false;
				string n = sm.ToString();
				return n == "Manual" || n == "Continuous";
			}
			catch { return false; }
		}

		private static bool IsNetworked(GameObject go)
		{
			try
			{
				var comps = go.GetComponents<Component>();
				if (comps == null) return false;
				foreach (var c in comps)
				{
					if (c == null) continue;
					string n;
					try { n = MenuCard.Il2CppNameOf(c); }
					catch { continue; }
					if (string.IsNullOrEmpty(n)) continue;
					// ONLY a VRCObjectSync or a VRC Pickup gives the network state Networking.SetOwner
					// needs. An UdonBehaviour alone does NOT -- SetOwner on a non-networked object makes
					// VRChat dereference network state that was never allocated and the process dies
					// (this module's crash). Same gate ObjectOrbitModule uses.
					if (n.IndexOf("Pickup", StringComparison.OrdinalIgnoreCase) >= 0
						|| n.IndexOf("ObjectSync", StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
			}
			catch { }
			return false;
		}

		private static void TakeOwnership(GameObject go)
		{
			try
			{
				if (go == null || !NativeGuard.Alive(go)) return;
				var me = VRC.SDKBase.Networking.LocalPlayer;
				if (me == null || !NativeGuard.Alive(me)) return;
				if (VRC.SDKBase.Networking.IsOwner(me, go)) return;
				VRC.SDKBase.Networking.SetOwner(me, go);
			}
			catch { }
		}

		private static string Short(string s)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length > 90 ? s.Substring(0, 90) + "…" : s);
	}
}
