using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.Core;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
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

		// NOTHING RUNS BY ITSELF HERE, AND THAT IS THE WHOLE POINT.
		//
		// The first cut of this module fired 20 s after the UI came up. That was wrong: the fetch needs
		// DelegateSupport.ConvertDelegate, the one call this mod documents as killing the PROCESS with
		// a fault .NET cannot catch, and Il2CppDelegates.Available opens as soon as FieldOffsetFix is
		// verified — so on a healthy install the gate was open and every user ran that call on every
		// world load, unasked. A crash was reported the same day. Whether or not this was its cause, a
		// probe that CAN take the game down must never be something you did not ask for.
		//
		// It is now a command: the client's WHO BLOCKED ME button, once, deliberately.
		public override void OnUiReady() { }

		public override void OnUpdate()
		{
			if (_at <= 0f || _fired) return;
			if (Time.realtimeSinceStartup < _at) return;
			_fired = true;
			try { Fetch(); }
			catch (Exception e)
			{
				Status = "blocked-by probe failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] " + Status);
			}
		}

		/// <summary>Asks once, on request. Re-arming a second time is allowed — the answer can change
		/// between sessions — but never automatically.</summary>
		public static void RequestFetch()
		{
			_fired = false;
			_at = Time.realtimeSinceStartup;   // next Update, on the main thread, where il2cpp wants it
			Status = "blocked-by: asking…";
			VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] requested by the user");
		}

		private static void Fetch()
		{
			if (!Il2CppDelegates.Available)
			{
				Status = "blocked-by: il2cpp delegate bridge is off — cannot ask the API on this build";
				VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] " + Status);
				return;
			}

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
				VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] " + Status);
				return;
			}

			VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] asking the API for player moderations…");
			ApiPlayerModeration.FetchAllAgainstMe(okAgainst, errAgainst);
			if (okMine != null && errMine != null) ApiPlayerModeration.FetchAllMine(okMine, errMine);
		}

		private static void Fail(string which, string error)
		{
			Status = "blocked-by (" + which + "): " + (error ?? "unknown error");
			VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] " + Status);
		}

		// againstMe: I am the target, so the SOURCE is the person who acted on me.
		// mine:      I am the source, so the TARGET is the person I acted on.
		private static void Consume(Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration> list, bool againstMe)
		{
			var byType = new Dictionary<string, int>(StringComparer.Ordinal);
			int total = 0, blocks = 0;
			try
			{
				foreach (ApiPlayerModeration m in Walk(list))
				{
					total++;
					string type;
					try { type = m.moderationType.ToString(); } catch { type = "?"; }
					byType.TryGetValue(type, out int n);
					byType[type] = n + 1;

					bool isBlock;
					try { isBlock = m.moderationType == ApiPlayerModeration.ModerationType.Block; } catch { continue; }
					if (!isBlock) continue;

					string id = null;
					try { id = againstMe ? m.sourceUserId : m.targetUserId; } catch { }
					if (string.IsNullOrEmpty(id)) continue;
					if (againstMe) { if (BlockedMe.Add(id)) blocks++; }
					else { if (IBlocked.Add(id)) blocks++; }
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] reading the list: " + e.Message);
			}

			// The breakdown is the point of the probe: "0 rows" and "40 rows, none of them Block" are
			// completely different answers, and only one of them means the feature is impossible.
			var parts = new List<string>();
			foreach (var kv in byType) parts.Add(kv.Key + "=" + kv.Value);
			string where = againstMe ? "against me" : "mine";
			Status = "blocked-by (" + where + "): " + total + " row(s)" + (parts.Count > 0 ? " [" + string.Join(", ", parts.ToArray()) + "]" : "")
				+ " → " + (againstMe ? BlockedMe.Count : IBlocked.Count) + " block(s)";
			VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] " + Status);
		}

		// The one enumeration that every il2cpp collection honours, per this mod's Il2CppSeq notes: a
		// generic IEnumerable<T> proxy is NOT a managed IEnumerable, so it is cast to the NATIVE
		// non-generic interface and its enumerator walked by hand.
		private static IEnumerable<ApiPlayerModeration> Walk(Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration> list)
		{
			var outp = new List<ApiPlayerModeration>();
			if (list == null) return outp;
			try
			{
				var native = list.TryCast<Il2CppSystem.Collections.IEnumerable>();
				if (native == null) return outp;
				var it = native.GetEnumerator();
				int guard = 0;
				while (it != null && it.MoveNext() && guard++ < 5000)
				{
					var cur = it.Current;
					if (cur == null) continue;
					ApiPlayerModeration m = null;
					try { m = cur.TryCast<ApiPlayerModeration>(); } catch { }
					if (m != null) outp.Add(m);
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] walk: " + e.Message); }
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
