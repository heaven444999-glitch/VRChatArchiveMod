using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// FAST SYNC — ask VRChat to serialise YOU at its fast rate.
	//
	// WHAT IT IS, AND WHAT IT IS NOT. This is not a hand-rolled increase of the Photon send rate,
	// which is the shape I warned the owner about: NetSendModule exists because two ServerTimeouts on
	// 2026-09-02 came from a STARVED CONNECTION, not a busy CPU, and MarkModule carries an event
	// budget for the same reason. This flips VRChat's OWN flag on VRChat's OWN serialiser —
	// FlatBufferNetworkSerializer.RequireFastRate — so the game keeps its own throttling, its own
	// batching and its own idea of what the network can take. It is the difference between opening a
	// tap the game controls and bypassing the tap.
	//
	// WHAT IT BUYS. Your position, rotation and pose reach the other clients more often, so you look
	// smoother to everyone else and the things you carry track your hands more closely. It costs
	// outbound bandwidth, which is why the game does not do it for everybody all the time.
	//
	// VERIFIED BEFORE IT WAS WRITTEN, not after: FlatBufferNetworkSerializer and get_/set_
	// RequireFastRate are both present in libs/interop's Assembly-CSharp. The lesson of the custom
	// username, which shipped on a grep of a name and crashed the game.
	//
	// RE-ASSERTED, because the component goes with the player. Joining a world rebuilds the local
	// player object and the fresh serialiser comes back at the default rate, so a one-shot write
	// would quietly stop applying after the first instance change.
	public class FastSyncModule : IModule
	{
		public override string Name => "FastSync";

		public static string Status = "";
		/// <summary>True once the flag has actually been written and read back as set.</summary>
		public static bool Applied { get; private set; }

		private static Type _serType;
		private static Il2CppSystem.Type _serIl2;
		private static PropertyInfo _fastRateProp;
		private static bool _resolveLogged;
		private static float _nextCheck;
		private static bool _lastWanted;

		public override void OnUpdate()
		{
			try
			{
				float now = Time.realtimeSinceStartup;
				if (now < _nextCheck) return;
				// Twice a second. One component lookup and one bool read when nothing has changed.
				_nextCheck = now + 0.5f;

				bool want = false;
				try { want = ModConfig.FastSync != null && ModConfig.FastSync.Value; } catch { }

				var ser = Resolve();
				if (ser == null)
				{
					Applied = false;
					if (want) Status = "fast sync: waiting for the local player";
					return;
				}

				bool current;
				try { current = (bool)(_fastRateProp.GetValue(ser) ?? false); }
				catch { Status = "fast sync: could not read RequireFastRate"; return; }

				if (current == want)
				{
					Applied = want;
					if (want) Status = "fast sync ON — VRChat is serialising you at its fast rate";
					else Status = "fast sync off";
					_lastWanted = want;
					return;
				}

				try { _fastRateProp.SetValue(ser, want); }
				catch (Exception e) { Status = "fast sync: " + e.Message; return; }

				// Read back rather than assume, the way the custom username does — "set" and "took"
				// are different claims.
				bool after;
				try { after = (bool)(_fastRateProp.GetValue(ser) ?? false); }
				catch { after = false; }
				Applied = after == want && want;

				if (want != _lastWanted || after != want)
				{
					_lastWanted = want;
					if (after == want)
						Killiorim.Logger.LogInfo(
							"[FastSync] RequireFastRate = " + want + " — verified by read-back. VRChat's own "
							+ "serialiser rate, so its own throttling still applies.");
					else
						Killiorim.Logger.LogWarning(
							"[FastSync] wrote RequireFastRate = " + want + " but read back " + after
							+ " — the game refused it. Not retrying in a loop.");
				}
				Status = Applied ? "fast sync ON" : "fast sync off";
			}
			catch (Exception e) { Status = "fast sync: " + e.Message; }
		}

		// The serialiser lives on the LOCAL PLAYER's GameObject. Resolved fresh each pass rather than
		// cached: the object is rebuilt on every world change, and a cached proxy would be dead — the
		// lookup is one call and this runs twice a second.
		private static object Resolve()
		{
			try
			{
				if (_serType == null)
				{
					foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
					{
						try { _serType = asm.GetType("VRC.Networking.FlatBufferNetworkSerializer", false) ?? asm.GetType("FlatBufferNetworkSerializer", false); }
						catch { continue; }
						if (_serType != null) break;
					}
					if (_serType == null)
					{
						if (!_resolveLogged) { _resolveLogged = true; Killiorim.Logger.LogWarning("[FastSync] FlatBufferNetworkSerializer is not present on this build — fast sync unavailable."); }
						return null;
					}
					try { _serIl2 = Il2CppType.From(_serType); } catch { }
					_fastRateProp = _serType.GetProperty("RequireFastRate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					if (_serIl2 == null || _fastRateProp == null)
					{
						if (!_resolveLogged) { _resolveLogged = true; Killiorim.Logger.LogWarning("[FastSync] RequireFastRate not found on FlatBufferNetworkSerializer — fast sync unavailable."); }
						_serType = null;
						return null;
					}
					if (!_resolveLogged) { _resolveLogged = true; Killiorim.Logger.LogInfo("[FastSync] armed — FlatBufferNetworkSerializer.RequireFastRate resolved."); }
				}

				// PlayerRef, not prop_Player_0 directly: that property answers from the first frame
				// with a proxy that has no live object behind it, and reading a component off THAT is
				// the fatal access violation NativeGuard exists to prevent.
				var lt = PlayerRef.LocalTransform();
				if (lt == null || !NativeGuard.Alive(lt)) return null;

				Component c = null;
				try { c = lt.GetComponent(_serIl2); } catch { }
				if (c == null) { try { c = lt.GetComponentInChildren(_serIl2, true); } catch { } }
				if (c == null || !NativeGuard.Alive(c)) return null;
				return c;
			}
			catch { return null; }
		}

		// A new world means a new player object; force the next pass to write the flag again.
		public override void OnSceneLoaded(int buildIndex) { Applied = false; _lastWanted = false; _nextCheck = 0f; }
	}
}
