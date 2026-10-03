using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.Core;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// WHO BLOCKED ME — the data half. Draws nothing; it fills two sets the player lists read.
	//
	// VRChat never tells you outright that someone blocked you, but its own API model does carry both
	// directions: VRC.Core.ApiPlayerModeration has FetchAllMine (what I did to others) and
	// FetchAllAgainstMe (what others did to me), each entry holding a moderationType, a targetUserId
	// and a sourceUserId. For an "against me" entry of type Block, the SOURCE is the person who
	// blocked me — that is the whole trick, and it needs no guessing from how an avatar is rendered.
	//
	// THIS IS A PROBE FIRST, A FEATURE SECOND. Three things are unknown and each one can make the
	// answer empty, so it logs a breakdown by type instead of quietly showing nothing:
	//   1. whether VRChat's server still returns Block rows on the against-me endpoint at all —
	//      it has been restricted over the years, and the client method existing proves nothing;
	//   2. whether the il2cpp delegate bridge works on this build (see below);
	//   3. whether the game has already fetched this for its own BlockedByUsers list, in which case
	//      a local read would be cheaper than any call.
	//
	// THE DELEGATE BRIDGE IS THE DANGEROUS PART. FetchAllAgainstMe takes two Action callbacks, so it
	// needs DelegateSupport.ConvertDelegate — the call this mod documents as dying inside
	// Il2CppSystem.Delegate.set_method_ptr with an access violation no try/catch can survive. Every
	// use goes through Il2CppDelegates.TryConvert, which refuses when the bridge is closed rather
	// than attempting it: probing that is the crash. Bridge closed = this module logs why and stops,
	// and nothing else in the mod notices.
	//
	// ONE CALL PER SESSION. These are authenticated API requests made as the user; ApiPlayerModeration
	// even carries its own ListCacheTime. Fired once, a little after the UI is up, and never on a
	// timer — a moderation list that changes mid-session is not worth a rate limit.
	public class BlockedByProbeModule : IModule
	{
		public override string Name => "BlockedByProbe";

		/// <summary>User ids of people who have blocked ME. Read by the player lists.</summary>
		public static readonly HashSet<string> BlockedMe = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>User ids I have blocked myself — the other direction, so the two can be told apart.</summary>
		public static readonly HashSet<string> IBlocked = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>"" until an answer arrived; otherwise a short sentence for the diagnostics panel.</summary>
		public static string Status = "";

		private static bool _fired;
		private static float _at;
		private static bool _autoArmed;
		private const string TrailName = "blockedby";

		// NOTHING RUNS BY ITSELF HERE, AND THAT IS THE WHOLE POINT.
		//
		// The first cut of this module fired 20 s after the UI came up. That was wrong: the fetch needs
		// DelegateSupport.ConvertDelegate, the one call this mod documents as killing the PROCESS with
		// a fault .NET cannot catch, and Il2CppDelegates.Available opens as soon as FieldOffsetFix is
		// verified — so on a healthy install the gate was open and every user ran that call on every
		// world load, unasked. A crash was reported the same day. Whether or not this was its cause, a
		// probe that CAN take the game down must never be something you did not ask for.
		//
		// AND YET IT NOW RUNS BY ITSELF AGAIN (2026-09-08), because a badge nobody can reach is not a
		// feature: ModLink.WhoBlockedMe() existed in the client and NOTHING EVER CALLED IT, so the
		// command this comment sends you to was unreachable and the BLOCKED pill could never appear.
		// The owner asked for it on by default, and not behind a toggle.
		//
		// What makes that defensible now, where it was not before:
		//   * IT IS GATED. Il2CppDelegates.Available is false unless FieldOffsetFix verified the
		//     offset slot, and the bridge REFUSES rather than guessing. On a build where the repair
		//     failed, nothing is asked at all.
		//   * IT LEAVES A TRAIL. Core/CrashTrail writes each step to disk and flushes BEFORE the call
		//     it describes, so if this ever does take the process down, the next launch says exactly
		//     which line did it. The old "a crash was reported the same day, whether or not this was
		//     its cause" is precisely the uncertainty that tooling removes.
		//   * ONCE PER SESSION, and late. Not on every world load, and not while the game is still
		//     bringing the instance up.
		public override void OnUiReady() { }

		// WHAT THE PREVIOUS SESSION DIED ON, read exactly once. CrashTrail.Check DELETES the file it
		// reads, so calling it from two places would make whichever ran second see nothing — the
		// answer is taken here, at startup, and everyone else reads this.
		private static string _lastCrash = "";

		public override void OnInitialize()
		{
			try { _lastCrash = Core.CrashTrail.Check(TrailName) ?? ""; }
			catch { _lastCrash = ""; }
			if (_lastCrash.Length > 0)
			{
				// REPORTED, NOT ACTED ON. An earlier draft made this disable the probe for a session,
				// and that was the wrong instinct: a feature that switches itself off is a feature the
				// user has lost, and the answer to "people are crashing" is to stop the crash, not to
				// stop the feature. The trail is here so a crash names its own line on the next launch.
				Killiorim.Logger.LogWarning(
					"[BlockedBy] the previous session died inside this probe at \"" + _lastCrash + "\". "
					+ "Running again with the pointer checks below; if this line comes back, it names the step.");
			}
		}

		public override void OnUpdate()
		{
			// Arm the automatic pass once, a while after the game has settled.
			if (!_autoArmed && !_fired && _at <= 0f)
			{
				float now0 = Time.realtimeSinceStartup;
				if (now0 < 45f) return;             // let the session finish coming up first
				// An escape hatch, ON by default: nothing is disabled, but a user who wants this off
				// should not have to delete the mod to get it.
				try { if (ModConfig.BlockedByProbe != null && !ModConfig.BlockedByProbe.Value) { _autoArmed = true; Status = "blocked-by: switched off in the config"; return; } }
				catch { }
				// AND WAIT TO ACTUALLY BE IN A WORLD. 45 s after launch can still be the loading
				// screen on a cold start, and an authenticated API call made before the account is
				// live comes back empty — which would look exactly like "nobody blocked you" and
				// then never be retried, because this fires once.
				try { if (PlayerRef.LocalApi() == null) return; }
				catch { return; }
				_autoArmed = true;
				if (!Il2CppDelegates.Available)
				{
					Status = "blocked-by: il2cpp delegate bridge is off — not asking on this build";
					Killiorim.Logger.LogInfo("[BlockedBy] " + Status);
					return;
				}
				_at = now0;
				Killiorim.Logger.LogInfo("[BlockedBy] asking automatically (once this session).");
			}

			if (_at <= 0f || _fired) return;
			if (Time.realtimeSinceStartup < _at) return;
			_fired = true;

			// THE TRAIL IS OPEN ACROSS THE WHOLE CALL. FetchAllAgainstMe goes through
			// DelegateSupport.ConvertDelegate, which this mod documents as able to kill the process
			// with a fault no catch can see — so every step is on disk before it runs.
			Core.CrashTrail.Begin(TrailName, "automatic blocked-by probe\r\nmod=" + PluginInfo.Version);
			try { Fetch(); }
			catch (Exception e)
			{
				Status = "blocked-by probe failed: " + e.Message;
				Killiorim.Logger.LogWarning("[BlockedBy] " + Status);
			}
			finally { Core.CrashTrail.End(); }
		}

		/// <summary>What the PREVIOUS session's probe died on, or "" if it completed. Reading consumes
		/// it, so a crash is reported once. Called by the diagnostics report.</summary>
		public static string CrashedAt() => _lastCrash;

		/// <summary>Asks once, on request. Re-arming a second time is allowed — the answer can change
		/// between sessions — but never automatically.</summary>
		public static void RequestFetch()
		{
			_fired = false;
			_at = Time.realtimeSinceStartup;   // next Update, on the main thread, where il2cpp wants it
			Status = "blocked-by: asking…";
			Killiorim.Logger.LogInfo("[BlockedBy] requested by the user");
		}

		private static void Fetch()
		{
			if (!Il2CppDelegates.Available)
			{
				Status = "blocked-by: il2cpp delegate bridge is off — cannot ask the API on this build";
				Killiorim.Logger.LogInfo("[BlockedBy] " + Status);
				return;
			}

			Core.CrashTrail.Step("converting the four delegates");
			var okAgainst = Il2CppDelegates.TryConvert<Il2CppSystem.Action<Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration>>>(
				new Action<Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration>>(list => Consume(list, true)), "BlockedBy/againstMe");
			var okMine = Il2CppDelegates.TryConvert<Il2CppSystem.Action<Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration>>>(
				new Action<Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration>>(list => Consume(list, false)), "BlockedBy/mine");
			var errAgainst = Il2CppDelegates.TryConvert<Il2CppSystem.Action<string>>(
				new Action<string>(e => Fail("against me", e)), "BlockedBy/againstMe-err");
			var errMine = Il2CppDelegates.TryConvert<Il2CppSystem.Action<string>>(
				new Action<string>(e => Fail("mine", e)), "BlockedBy/mine-err");

			if (okAgainst == null || errAgainst == null)
			{
				Status = "blocked-by: the delegate bridge refused — nothing was asked";
				Killiorim.Logger.LogInfo("[BlockedBy] " + Status);
				return;
			}

			Killiorim.Logger.LogInfo("[BlockedBy] asking the API for player moderations…");
			Core.CrashTrail.Step("FetchAllAgainstMe — the call that needs ConvertDelegate");
			ApiPlayerModeration.FetchAllAgainstMe(okAgainst, errAgainst);
			Core.CrashTrail.Step("FetchAllAgainstMe returned");
			if (okMine != null && errMine != null)
			{
				Core.CrashTrail.Step("FetchAllMine");
				ApiPlayerModeration.FetchAllMine(okMine, errMine);
				Core.CrashTrail.Step("FetchAllMine returned");
			}
		}

		private static void Fail(string which, string error)
		{
			Status = "blocked-by (" + which + "): " + (error ?? "unknown error");
			Killiorim.Logger.LogWarning("[BlockedBy] " + Status);
		}

		// againstMe: I am the target, so the SOURCE is the person who acted on me.
		// mine:      I am the source, so the TARGET is the person I acted on.
		private static void Consume(Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration> list, bool againstMe)
		{
			var byType = new Dictionary<string, int>(StringComparer.Ordinal);
			int total = 0, blocks = 0;
			try
			{
				// THE SECOND HALF OF THE SAME CRASH. Walk stops a dead pointer reaching TryCast; these
				// lines then read FIELDS off the rows it returned, and that is the other uncatchable
				// fault this mod knows about — moderationType is an il2cpp field (Core/FieldOffsetFix:
				// unrepaired, the offset is a metadata token and the read lands outside the object),
				// and sourceUserId/targetUserId are il2cpp STRINGS, whose generated getter hands the
				// pointer straight to Il2CppStringToManaged and memmoves from it. Both end the process
				// from inside, past every catch here.
				//
				// So the rows are re-validated before they are read — a row can die between the walk
				// and here, the callback is not instantaneous — and the strings go through
				// Core/Il2CppStr, which proves the header and length before converting.
				bool fieldsSafe = false;
				try { fieldsSafe = Core.FieldOffsetFix.Verified; } catch { }
				if (!fieldsSafe)
				{
					Status = "blocked-by: field offsets unrepaired on this build — not reading the rows";
					Killiorim.Logger.LogWarning("[BlockedBy] " + Status);
					return;
				}

				foreach (ApiPlayerModeration m in Walk(list))
				{
					if (m == null || !Core.NativeGuard.Alive(m)) continue;
					total++;
					string type;
					try { type = m.moderationType.ToString(); } catch { type = "?"; }
					byType.TryGetValue(type, out int n);
					byType[type] = n + 1;

					bool isBlock;
					try { isBlock = m.moderationType == ApiPlayerModeration.ModerationType.Block; } catch { continue; }
					if (!isBlock) continue;

					string id = ReadId(m, againstMe);
					if (string.IsNullOrEmpty(id)) continue;
					if (againstMe) { if (BlockedMe.Add(id)) blocks++; }
					else { if (IBlocked.Add(id)) blocks++; }
				}
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogWarning("[BlockedBy] reading the list: " + e.Message);
			}

			// The breakdown is the point of the probe: "0 rows" and "40 rows, none of them Block" are
			// completely different answers, and only one of them means the feature is impossible.
			var parts = new List<string>();
			foreach (var kv in byType) parts.Add(kv.Key + "=" + kv.Value);
			string where = againstMe ? "against me" : "mine";
			Status = "blocked-by (" + where + "): " + total + " row(s)" + (parts.Count > 0 ? " [" + string.Join(", ", parts.ToArray()) + "]" : "")
				+ " → " + (againstMe ? BlockedMe.Count : IBlocked.Count) + " block(s)";
			Killiorim.Logger.LogInfo("[BlockedBy] " + Status);
		}

		// The one enumeration that every il2cpp collection honours, per this mod's Il2CppSeq notes: a
		// generic IEnumerable<T> proxy is NOT a managed IEnumerable, so it is cast to the NATIVE
		// non-generic interface and its enumerator walked by hand.
		// THE CRASH THIS IS WRITTEN AGAINST (2026-09-08, a user's ErrorLog.log):
		//
		//     Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
		//        at Il2CppInterop.Runtime.IL2CPP.il2cpp_class_is_assignable_from(IntPtr, IntPtr)
		//        at Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase.TryCast[[System.__Canon]]()
		//        at Killiorim.Modules.BlockedByProbeModule.Walk(...)
		//
		// AND THE `catch` THAT USED TO WRAP TryCast NEVER RAN. On .NET 6 an AccessViolationException is
		// a corrupted-state exception: it is not delivered to managed catch blocks at all, the process
		// simply ends. So `try { m = cur.TryCast<...>(); } catch { }` read like a guard and was
		// decorative — the same lesson as the displayName crash in SpoofModule the same day. The only
		// thing that works is not making the call when it would fault.
		//
		// WHAT TryCast<T> ACTUALLY DOES: it takes the target class pointer from
		// Il2CppClassPointerStore<T>, the object's own class from il2cpp_object_get_class(Pointer),
		// and hands BOTH to il2cpp_class_is_assignable_from, which dereferences them. Two pointers,
		// either of which can be rotten:
		//   * the OBJECT's — an element whose native object is gone, or an enumerator handing back a
		//     stale pointer. il2cpp_object_get_class then reads freed memory and returns garbage.
		//   * the TARGET CLASS's — resolved lazily per T, and this build re-obfuscates every VRChat
		//     type on each update, so a lookup that half-succeeds leaves a non-zero, invalid pointer.
		// The stack cannot say which, and it does not matter: both are refused below, so the walk is
		// safe under either.
		// The user id on one row, read the safe way: the raw field pointer, then Core/Il2CppStr, which
		// refuses a string whose header or length does not stand up instead of memmoving from it.
		// Field handles are resolved once per session off the first row we see.
		private static IntPtr _fSource, _fTarget;
		private static bool _idFieldsTried;

		private static string ReadId(ApiPlayerModeration m, bool againstMe)
		{
			try
			{
				IntPtr obj;
				try { obj = m.Pointer; } catch { return ""; }
				if (!Core.NativeGuard.IsLiveObject(obj)) return "";

				if (!_idFieldsTried)
				{
					_idFieldsTried = true;
					_fSource = Core.Il2CppStr.FindField(obj, "sourceUserId");
					_fTarget = Core.Il2CppStr.FindField(obj, "targetUserId");
					if (_fSource == IntPtr.Zero || _fTarget == IntPtr.Zero)
						Killiorim.Logger.LogWarning(
							"[BlockedBy] ApiPlayerModeration has no sourceUserId/targetUserId field on this build "
							+ "— no ids will be read, and nothing is guessed.");
				}

				IntPtr fi = againstMe ? _fSource : _fTarget;
				if (fi == IntPtr.Zero) return "";
				IntPtr sp;
				if (!Core.Il2CppStr.TryFieldPtr(obj, fi, out sp)) return "";
				string s;
				return Core.Il2CppStr.TryRead(sp, out s) ? (s ?? "") : "";
			}
			catch { return ""; }
		}

		private static bool ClassOk<T>() where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
		{
			try
			{
				IntPtr k = Il2CppInterop.Runtime.Il2CppClassPointerStore<T>.NativeClassPtr;
				return k != IntPtr.Zero && Core.NativeGuard.IsReadable(k, 16);
			}
			catch { return false; }   // a type initializer that threw is a managed failure, and catchable
		}

		private static IEnumerable<ApiPlayerModeration> Walk(Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration> list)
		{
			var outp = new List<ApiPlayerModeration>();
			if (list == null) return outp;

			// Both class pointers, once, before anything is cast. If either is not a readable class,
			// no cast in this method can be made safely and the honest answer is no rows.
			if (!ClassOk<Il2CppSystem.Collections.IEnumerable>() || !ClassOk<ApiPlayerModeration>())
			{
				Killiorim.Logger.LogWarning(
					"[BlockedBy] the il2cpp class pointers for IEnumerable/ApiPlayerModeration did not "
					+ "validate on this build — not walking the list. VRChat has probably moved the type.");
				return outp;
			}
			if (!Core.NativeGuard.Alive(list)) { Killiorim.Logger.LogWarning("[BlockedBy] the list VRChat handed back is not a live object — nothing read."); return outp; }

			// THE REAL REPAIR: DO NOT CAST IN THE LOOP AT ALL.
			//
			// Guarding TryCast per element makes the walk survivable. Not calling it is better, and it
			// is available: VRChat hands back a List<ApiPlayerModeration>, and Il2CppInterop's List<T>
			// exposes Count and an indexer whose result is ALREADY typed — get_Item goes through
			// Il2CppObjectPool.Get<T>, which never touches il2cpp_class_is_assignable_from. So the
			// function that faulted is not on this path even once per row.
			//
			// One cast remains, on the LIST itself, and it is the one call we can afford to check
			// properly: both class pointers validated above, the object validated as live. One guarded
			// call instead of N unguarded ones.
			try
			{
				if (ClassOk<Il2CppSystem.Collections.Generic.List<ApiPlayerModeration>>())
				{
					Il2CppSystem.Collections.Generic.List<ApiPlayerModeration> typed = null;
					try { typed = list.TryCast<Il2CppSystem.Collections.Generic.List<ApiPlayerModeration>>(); } catch { }
					if (typed != null && Core.NativeGuard.Alive(typed))
					{
						int n = -1;
						try { n = typed.Count; } catch { n = -1; }
						// A Count read off a rebuilt object could be anything; a moderation list of a
						// hundred thousand rows is not a real answer, it is a bad read.
						if (n >= 0 && n <= 50000)
						{
							int dead = 0;
							for (int i = 0; i < n; i++)
							{
								ApiPlayerModeration m = null;
								try { m = typed[i]; } catch { continue; }
								if (m == null) continue;
								if (!Core.NativeGuard.Alive(m)) { dead++; continue; }
								outp.Add(m);
							}
							Killiorim.Logger.LogInfo(
								"[BlockedBy] read " + outp.Count + " row(s) by indexer"
								+ (dead > 0 ? " (" + dead + " skipped: object gone)" : "") + " — no per-row cast.");
							return outp;
						}
						Killiorim.Logger.LogWarning("[BlockedBy] List.Count read back " + n + " — refusing it, falling back to the enumerator.");
					}
				}
			}
			catch (Exception e) { Killiorim.Logger.LogWarning("[BlockedBy] indexer path: " + e.Message); }

			// FALLBACK — an enumerator walk, for a build where the callback hands back something that
			// is not a List<T>. Same guards, per element, because here the cast is unavoidable.
			try
			{
				var native = list.TryCast<Il2CppSystem.Collections.IEnumerable>();
				if (native == null || !Core.NativeGuard.Alive(native)) return outp;
				var it = native.GetEnumerator();
				if (it == null || !Core.NativeGuard.Alive(it)) return outp;

				int guard = 0, skipped = 0;
				while (guard++ < 5000)
				{
					// The enumerator is re-checked EVERY step: MoveNext runs the world's own code and
					// the collection can be rebuilt underneath us between two elements.
					if (!Core.NativeGuard.Alive(it)) break;
					bool more;
					try { more = it.MoveNext(); } catch { break; }
					if (!more) break;

					var cur = it.Current;
					if (cur == null) continue;

					// THE LINE THE CRASH WAS ON. IsLiveObject proves exactly what
					// il2cpp_class_is_assignable_from is about to assume: the pointer is mapped,
					// 8-aligned, and the class pointer in its first eight bytes is mapped too.
					IntPtr p;
					try { p = cur.Pointer; } catch { skipped++; continue; }
					if (!Core.NativeGuard.IsLiveObject(p)) { skipped++; continue; }

					ApiPlayerModeration m = null;
					try { m = cur.TryCast<ApiPlayerModeration>(); } catch { }
					if (m != null) outp.Add(m);
				}
				if (skipped > 0)
					Killiorim.Logger.LogWarning(
						"[BlockedBy] skipped " + skipped + " row(s) whose object was not there any more. "
						+ "This is the guard doing its job — that cast used to end the process.");
			}
			catch (Exception e) { Killiorim.Logger.LogWarning("[BlockedBy] walk: " + e.Message); }
			return outp;
		}

		/// <summary>BLOCKED / BLOCKED BY YOU / "" for a user id — what the lists print.</summary>
		public static string Tag(string userId)
		{
			if (string.IsNullOrEmpty(userId)) return "";
			if (BlockedMe.Contains(userId)) return "BLOCKED";
			if (IBlocked.Contains(userId)) return "BLOCKED BY YOU";
			return "";
		}

		// The lists belong to the account, not the world: a scene change must not clear them, and must
		// not re-ask either.
		public override void OnShutdown() { BlockedMe.Clear(); IBlocked.Clear(); _fired = false; }
	}
}
