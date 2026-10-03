using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase;

namespace Killiorim.Core
{
	// Shared, cached VRCPlayerApi -> APIUser resolver.
	//
	// Reaching a player's APIUser means: build an Il2CppType, search the hierarchy for the
	// VRC.Player component, TryCast it, then read a property by reflection. ESP, the radar and
	// the watchlist each did that FOR EVERY PLAYER, ON EVERY REPAINT — and IMGUI repaints more
	// than once per frame. With a full instance that was hundreds of hierarchy searches and
	// reflection invokes per frame, three times over: a measurable, constant frame cost.
	//
	// Here the reflection handles are resolved once, the Il2CppType is built once, and the answer
	// is cached per player for a second (trust rank does not change mid-second). Dead entries are
	// dropped when the cache is swept, and everything is thrown away on a world change.
	public static class ApiUsers
	{
		private const float TtlSeconds = 1f;

		private static bool _resolved;
		private static Type _playerType;
		private static PropertyInfo _mApiUser;
		private static MethodInfo _tryCastPlayer;
		private static Il2CppSystem.Type _playerIl2Type;   // built once, not per call

		private sealed class Entry { public APIUser User; public float At; }
		private static readonly Dictionary<int, Entry> Cache = new Dictionary<int, Entry>();
		private static float _nextSweep;

		public static APIUser Get(VRCPlayerApi api)
		{
			try
			{
				if (api == null) return null;
				Resolve();
				if (_mApiUser == null || _playerIl2Type == null) return null;

				GameObject go = api.gameObject;
				if (go == null) return null;
				int id = go.GetInstanceID();
				float now = Time.realtimeSinceStartup;

				if (Cache.TryGetValue(id, out Entry e) && now - e.At < TtlSeconds) return e.User;

				var comp = go.GetComponent(_playerIl2Type)
				        ?? go.GetComponentInParent(_playerIl2Type)
				        ?? go.GetComponentInChildren(_playerIl2Type);

				APIUser user = null;
				if (comp != null)
				{
					object player = _tryCastPlayer != null ? _tryCastPlayer.Invoke(comp, null) : comp;
					if (player != null) user = _mApiUser.GetValue(player) as APIUser;
				}

				if (e == null) Cache[id] = new Entry { User = user, At = now };
				else { e.User = user; e.At = now; }

				if (now >= _nextSweep) { _nextSweep = now + 30f; Sweep(now); }
				return user;
			}
			catch { return null; }
		}

		// Players leave; without this the dictionary would only ever grow across a long session.
		private static void Sweep(float now)
		{
			try
			{
				List<int> dead = null;
				foreach (var kv in Cache)
					if (now - kv.Value.At > 60f) (dead ??= new List<int>()).Add(kv.Key);
				if (dead != null) foreach (int k in dead) Cache.Remove(k);
			}
			catch { }
		}

		public static void Clear() { try { Cache.Clear(); } catch { } }

		private static void Resolve()
		{
			if (_resolved) return;
			_resolved = true;
			try
			{
				_playerType = Assembly.Load("Assembly-CSharp").GetType("VRC.Player");
				if (_playerType == null) return;
				_mApiUser = _playerType.GetProperties().FirstOrDefault(p => p.PropertyType == typeof(APIUser));
				// TryCast<VRC.Player> re-wraps a plain Component as the concrete type so the APIUser
				// property can actually be read (otherwise GetValue throws -> null -> everything falls
				// back to the Visitor colour).
				_tryCastPlayer = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
					.GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)
					?.MakeGenericMethod(_playerType);
				_playerIl2Type = Il2CppInterop.Runtime.Il2CppType.From(_playerType);
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogWarning($"[ApiUsers] trust reflection unavailable: {e.Message}");
			}
		}

		// ---- per-player badges (VRC+ / 18+ / platform) ---------------------------------
		// All three come off the same APIUser we already cache, so they cost nothing extra:
		//   isSupporter    -> VRC+
		//   ageVerified    -> 18+
		//   last_platform  -> "standalonewindows" (PC) / "android" (Quest)
		// Read by reflection with cached PropertyInfo: the interop exposes these by their real
		// names, but a VRChat build can rename or drop one, and a missing badge must never throw
		// in the middle of drawing the roster.
		public struct Badges
		{
			public bool Plus;        // VRC+
			public bool Adult;       // 18+ age verified
			public string Platform;  // "PC" | "Quest" | ""
		}

		private static bool _badgeResolved;
		private static PropertyInfo _pSupporter, _pAgeVerified, _pAgeStatus, _pPlatform;

		public static Badges BadgesOf(VRCPlayerApi api) => BadgesFrom(Get(api));

		// Same read, but from an APIUser the caller already has (the PLAYERS roster resolves one
		// per player when it refreshes, so it never needs a second lookup).
		public static Badges BadgesFrom(object user)
		{
			var b = new Badges { Platform = "" };
			try
			{
				var u = user;
				if (u == null) return b;
				ResolveBadges(u);

				if (_pSupporter != null) { try { b.Plus = (bool)_pSupporter.GetValue(u); } catch { } }

				if (_pAgeVerified != null) { try { b.Adult = (bool)_pAgeVerified.GetValue(u); } catch { } }
				if (!b.Adult && _pAgeStatus != null)
				{
					try
					{
						string st = _pAgeStatus.GetValue(u)?.ToString();
						if (!string.IsNullOrEmpty(st) && st.IndexOf("verified", StringComparison.OrdinalIgnoreCase) >= 0
							&& st.IndexOf("un", StringComparison.OrdinalIgnoreCase) != 0)
							b.Adult = true;
					}
					catch { }
				}

				if (_pPlatform != null)
				{
					try
					{
						string p = _pPlatform.GetValue(u)?.ToString() ?? "";
						if (p.IndexOf("android", StringComparison.OrdinalIgnoreCase) >= 0) b.Platform = "Quest";
						else if (p.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0
						      || p.IndexOf("standalone", StringComparison.OrdinalIgnoreCase) >= 0) b.Platform = "PC";
						else if (p.Length > 0) b.Platform = p;
					}
					catch { }
				}
			}
			catch { }
			return b;
		}

		private static void ResolveBadges(object sample)
		{
			if (_badgeResolved) return;
			_badgeResolved = true;
			try
			{
				Type t = sample.GetType();
				_pSupporter   = Find(t, "isSupporter", "hasVRCPlus");
				_pAgeVerified = Find(t, "ageVerified", "isAgeVerified");
				_pAgeStatus   = Find(t, "ageVerificationStatus");
				_pPlatform    = Find(t, "last_platform", "platform");
				Killiorim.Logger.LogInfo(
					$"[ApiUsers] badges: vrc+={_pSupporter != null} 18+={_pAgeVerified != null}/{_pAgeStatus != null} platform={_pPlatform != null}");
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[ApiUsers] badge reflection failed: {e.Message}"); }
		}

		private static PropertyInfo Find(Type t, params string[] names)
		{
			foreach (string n in names)
			{
				try { var p = t.GetProperty(n); if (p != null) return p; } catch { }
			}
			return null;
		}
	}
}
