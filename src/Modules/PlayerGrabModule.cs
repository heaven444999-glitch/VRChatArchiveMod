using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PLAYER GRAB — mod users grab and throw EACH OTHER.
	//
	// VRChat lets no client move another player's avatar, so this is COOPERATIVE: the player being
	// held moves THEMSELVES (their own client, VRChat's own SetVelocity) to follow the grabber's hand.
	// Nothing is streamed: the held client reads the grabber's hand BONE locally — VRChat already
	// syncs everyone's avatar bones. Only a handful of events travel, through the Archive's relay
	// (/api/grab/send + long-poll /api/grab/poll): grab, hold (1 s heartbeat), release, ack/nack,
	// escaped. Mutual consent: it only works between two people who BOTH run the mod with this
	// toggle ON; a grab sent to anyone else simply gets no answer (and the grabber is told so).
	//
	// Mechanics follow the well-known world-script approach (Reimajo's Player Lift Up) reimplemented
	// on the mod side: velocity-follow toward startPos + (handNow - handStart); throw = the held
	// player's velocity over the last two frames x a force multiplier; WASD/stick to escape.
	public class PlayerGrabModule : IModule
	{
		public override string Name => "PlayerGrab";

		public static bool Active { get; private set; }
		public static string Status = "";
		/// <summary>Short live state for the client button: "", "holding X", "held by X".</summary>
		public static string StateText = "";

		// ---- grabber side
		private static string _pendingUid, _pendingName; private static float _pendingAt;
		private static string _holdingUid, _holdingName; private static float _nextHold;
		private static string _holdingHand = "R";
		private static bool _gripL, _gripR;          // previous XR grip state (edge detection)
		private static bool _xrBroken;               // XR input threw once -> stop asking

		// ---- held side
		private static string _heldByUid, _heldByName, _heldHand;
		private static VRC.SDKBase.VRCPlayerApi _holderApi;
		private static Vector3 _startHand, _startMe, _p1, _p2; private static float _dt1 = 0.016f;
		private static float _lastHoldAt, _handMissingSince;
		private static Vector3 _inertiaVel; private static float _inertiaUntil, _inertiaTotal;

		// ---- relay
		private static bool _polling;
		private static readonly ConcurrentQueue<Action> Main = new ConcurrentQueue<Action>();

		private static readonly HumanBodyBones[] ReachBones =
		{
			HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips,
			HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
			HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
			HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
		};

		// ---------------------------------------------------------------- config (live)
		private static float Follow => Cfg(() => ModConfig.PlayerGrabFollow.Value, 1f, 0.2f, 3f);
		private static float ThrowForce => Cfg(() => ModConfig.PlayerGrabThrowForce.Value, 3f, 0f, 8f);
		private static float Inertia => Cfg(() => ModConfig.PlayerGrabInertia.Value, 0.8f, 0f, 4f);
		private static float Reach => Cfg(() => ModConfig.PlayerGrabReach.Value, 0.35f, 0.1f, 1.5f);
		private static float AimRange => Cfg(() => ModConfig.PlayerGrabAimRange.Value, 12f, 2f, 40f);
		private static bool AllowEscape { get { try { return ModConfig.PlayerGrabAllowEscape.Value; } catch { return true; } } }
		private static float Cfg(Func<float> get, float dflt, float lo, float hi)
		{
			try { return Mathf.Clamp(get(), lo, hi); } catch { return dflt; }
		}

		// ---------------------------------------------------------------- toggle

		public static void Toggle()
		{
			if (Active) TurnOff("player grab off"); else TurnOn();
		}

		private static void TurnOn()
		{
			string me = VaTagsModule.LocalUserId();
			if (string.IsNullOrEmpty(me)) { Status = "your user id isn't readable yet (join a world)"; return; }
			Active = true;
			Status = "player grab ON — grip near a mod user to lift them (PC: GRAB PLAYER YOU AIM AT / RightShift+U)";
			StateText = "";
			EnsurePolling();
			VRChatArchiveModPlugin.Logger.LogInfo("[PlayerGrab] ON.");
		}

		private static void TurnOff(string why)
		{
			try { if (!string.IsNullOrEmpty(_holdingUid)) Send(_holdingUid, "release", _holdingHand); } catch { }
			try { if (!string.IsNullOrEmpty(_heldByUid)) { Send(_heldByUid, "escaped", ""); ReleaseSelf(false); } } catch { }
			_holdingUid = _holdingName = _pendingUid = _pendingName = null;
			Active = false;
			StateText = "";
			Status = why;
			VRChatArchiveModPlugin.Logger.LogInfo("[PlayerGrab] OFF.");
		}

		// ---------------------------------------------------------------- update

		public override void OnUpdate()
		{
			try
			{
				while (Main.TryDequeue(out var act)) { try { act(); } catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[PlayerGrab] event: " + e.Message); } }

				if (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.H)) Toggle();
				// The relay is the mod's event bus — grabs AND the Mark's art broadcasts — so it listens as
				// soon as we know who we are, not only while player grab is on.
				if (!string.IsNullOrEmpty(VaTagsModule.LocalUserId())) EnsurePolling();
				// Belt and braces for the outbox: if a send ever lands in the queue while the drain is
				// finishing, this picks it up on the next frame instead of leaving it there.
				if (!_outbox.IsEmpty) PumpOutbox();
				if (!Active) return;
				float now = Time.realtimeSinceStartup;

				// ---- grabber: inputs
				if (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.U)) GrabOrReleaseAimed();
				PollVrGrips();

				// ---- grabber: pending answer timeout
				if (!string.IsNullOrEmpty(_pendingUid) && now - _pendingAt > 1.6f)
				{
					Status = "no answer from " + _pendingName + " — no mod, or their player grab is off";
					_pendingUid = _pendingName = null;
				}
				// ---- grabber: heartbeat while holding
				if (!string.IsNullOrEmpty(_holdingUid) && now >= _nextHold)
				{
					_nextHold = now + 1f;
					if (FindEntry(_holdingUid) == null) { _holdingUid = _holdingName = null; StateText = ""; Status = "they left"; }
					else Send(_holdingUid, "hold", _holdingHand);
				}

				// ---- held side
				if (!string.IsNullOrEmpty(_heldByUid)) HeldUpdate(now);
				else if (now < _inertiaUntil) InertiaUpdate(now);
			}
			catch (Exception e) { Status = "failed: " + e.Message; }
		}

		// VR: a closed grip with the hand inside another mod user's body = grab; opening it = release.
		private static void PollVrGrips()
		{
			if (_xrBroken) return;
			try
			{
				bool l = GripDown(UnityEngine.XR.XRNode.LeftHand);
				bool r = GripDown(UnityEngine.XR.XRNode.RightHand);
				if (r && !_gripR) OnGripPressed("R");
				if (l && !_gripL) OnGripPressed("L");
				if (!r && _gripR && _holdingHand == "R" && !string.IsNullOrEmpty(_holdingUid)) ReleaseHeld();
				if (!l && _gripL && _holdingHand == "L" && !string.IsNullOrEmpty(_holdingUid)) ReleaseHeld();
				_gripL = l; _gripR = r;
			}
			catch { _xrBroken = true; }   // no XR (desktop) or an interop gap: the aim path still works
		}

		private static bool GripDown(UnityEngine.XR.XRNode node)
		{
			var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
			if (!dev.isValid) return false;
			bool v;
			return dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out v) && v;
		}

		private static void OnGripPressed(string hand)
		{
			if (!string.IsNullOrEmpty(_holdingUid) || !string.IsNullOrEmpty(_pendingUid)) return;
			var me = PlayerRef.LocalApi(); if (me == null) return;
			Vector3 handPos;
			try { handPos = me.GetBonePosition(hand == "L" ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand); } catch { return; }
			if (handPos == Vector3.zero) return;
			var target = NearestByBones(handPos, Reach);
			if (target != null) BeginGrab(target, hand);
		}

		/// <summary>Desktop: grab the mod user under the crosshair, or release if already holding.</summary>
		public static void GrabOrReleaseAimed()
		{
			if (!Active) { Status = "turn player grab on first"; return; }
			if (!string.IsNullOrEmpty(_holdingUid)) { ReleaseHeld(); return; }
			var target = NearestToCrosshair(AimRange);
			if (target == null) { Status = "no player under your crosshair (within " + AimRange.ToString("0") + "m)"; VRChatArchiveModPlugin.Logger.LogInfo($"[PlayerGrab] aim: no target (roster={VaTagsModule.Roster.Count}, range={AimRange:0})"); return; }
			BeginGrab(target, "aim");
		}

		private static void BeginGrab(VaTagsModule.PlayerEntry target, string hand)
		{
			_pendingUid = target.UserId; _pendingName = target.Name; _pendingAt = Time.realtimeSinceStartup;
			VRChatArchiveModPlugin.Logger.LogInfo($"[PlayerGrab] asking {target.Name} ({target.UserId}) hand={hand}");
			_holdingHand = hand;
			Status = "asking " + target.Name + "…";
			Send(target.UserId, "grab", hand);
		}

		private static void ReleaseHeld()
		{
			if (string.IsNullOrEmpty(_holdingUid)) return;
			Send(_holdingUid, "release", _holdingHand);
			Status = "released " + _holdingName;
			_holdingUid = _holdingName = null; StateText = "";
		}

		// ---------------------------------------------------------------- held side

		private static void OnGrabRequest(string fromUid, string hand)
		{
			if (!Active) { Send(fromUid, "nack", ""); return; }
			var entry = FindEntry(fromUid);
			VRChatArchiveModPlugin.Logger.LogInfo($"[PlayerGrab] grab request from {fromUid}: {(entry == null ? "NOT in my roster (" + VaTagsModule.Roster.Count + " entries)" : entry.Name)}");
			if (entry == null || entry.IsLocal) { Send(fromUid, "nack", ""); return; }   // not in my instance: ignore
			var api = ApiOf(entry); var me = PlayerRef.LocalApi();
			if (api == null || me == null) { Send(fromUid, "nack", ""); return; }
			Vector3 h;
			try { h = api.GetBonePosition(BoneFor(hand)); } catch { h = Vector3.zero; }
			if (h == Vector3.zero) { try { h = api.GetPosition(); } catch { Send(fromUid, "nack", ""); return; } }
			_holderApi = api; _heldByUid = fromUid; _heldByName = entry.Name; _heldHand = hand;
			_startHand = h; try { _startMe = me.GetPosition(); } catch { _startMe = Vector3.zero; }
			_p1 = _p2 = _startMe; _dt1 = Mathf.Max(0.001f, Time.deltaTime);
			_lastHoldAt = Time.realtimeSinceStartup; _handMissingSince = 0f;
			_inertiaUntil = 0f;
			StateText = "held by " + entry.Name;
			Status = entry.Name + " grabbed you" + (AllowEscape ? " — move (WASD / stick) to break free" : "");
			try { me.PlayHapticEventInHand(VRC.SDKBase.VRC_Pickup.PickupHand.Right, 0.3f, 0.8f, 40f); } catch { }
			Send(fromUid, "ack", hand);
		}

		private static void HeldUpdate(float now)
		{
			var me = PlayerRef.LocalApi();
			if (me == null || _holderApi == null) { ReleaseSelf(false); return; }
			if (now - _lastHoldAt > 3.5f) { Status = "dropped — lost contact with " + _heldByName; ReleaseSelf(true); return; }
			if (AllowEscape && WantsToEscape()) { Send(_heldByUid, "escaped", ""); Status = "you broke free"; ReleaseSelf(false); return; }

			Vector3 hand;
			try { hand = _holderApi.GetBonePosition(BoneFor(_heldHand)); } catch { hand = Vector3.zero; }
			if (hand == Vector3.zero)
			{
				if (_handMissingSince == 0f) _handMissingSince = now;
				if (now - _handMissingSince > 1f) { Status = "dropped — " + _heldByName + " vanished"; ReleaseSelf(true); }
				return;
			}
			_handMissingSince = 0f;

			float dt = Mathf.Max(0.001f, Time.deltaTime);
			Vector3 target = _startMe + (hand - _startHand);
			Vector3 pos; try { pos = me.GetPosition(); } catch { return; }
			Vector3 v = (target - pos) / dt * Follow;
			if (v.magnitude > 60f) v = v.normalized * 60f;
			try { me.SetVelocity(v); } catch { }
			_p2 = _p1; _p1 = target; _dt1 = dt;
		}

		private static void OnReleased()
		{
			var me = PlayerRef.LocalApi();
			Vector3 throwVel = (_p1 - _p2) / Mathf.Max(0.001f, _dt1) * ThrowForce;
			if (throwVel.magnitude > 45f) throwVel = throwVel.normalized * 45f;
			if (throwVel.magnitude < 0.5f) throwVel = Vector3.zero;
			string who = _heldByName;
			ReleaseSelf(false);
			if (me == null) return;
			try { me.SetVelocity(throwVel); } catch { }
			if (throwVel != Vector3.zero && Inertia > 0f)
			{
				_inertiaVel = throwVel; _inertiaTotal = Inertia; _inertiaUntil = Time.realtimeSinceStartup + Inertia;
			}
			Status = who + " threw you (" + throwVel.magnitude.ToString("0.#") + " m/s)";
		}

		// Keep the horizontal momentum alive for a moment after the throw — gravity still pulls
		// down, VRChat's ground friction would otherwise kill the flight the instant you touch it.
		private static void InertiaUpdate(float now)
		{
			var me = PlayerRef.LocalApi(); if (me == null) return;
			float k = Mathf.Clamp01((_inertiaUntil - now) / Mathf.Max(0.01f, _inertiaTotal));
			Vector3 v; try { v = me.GetVelocity(); } catch { return; }
			v.x = _inertiaVel.x * k; v.z = _inertiaVel.z * k;
			try { me.SetVelocity(v); } catch { }
		}

		private static void ReleaseSelf(bool zeroVelocity)
		{
			_heldByUid = _heldByName = _heldHand = null; _holderApi = null; StateText = "";
			if (zeroVelocity) { try { PlayerRef.LocalApi()?.SetVelocity(Vector3.zero); } catch { } }
		}

		private static bool WantsToEscape()
		{
			try
			{
				if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D)) return true;
				if (Mathf.Abs(Input.GetAxisRaw("Horizontal")) > 0.7f || Mathf.Abs(Input.GetAxisRaw("Vertical")) > 0.7f) return true;
			}
			catch { }
			return false;
		}

		private static HumanBodyBones BoneFor(string hand)
			=> hand == "L" ? HumanBodyBones.LeftHand : hand == "R" ? HumanBodyBones.RightHand : HumanBodyBones.Head;

		// ---------------------------------------------------------------- targeting

		private static VaTagsModule.PlayerEntry FindEntry(string uid)
		{
			if (string.IsNullOrEmpty(uid)) return null;
			foreach (var e in VaTagsModule.Roster)
				if (e != null && string.Equals(e.UserId, uid, StringComparison.OrdinalIgnoreCase)) return e;
			return null;
		}

		private static VRC.SDKBase.VRCPlayerApi ApiOf(VaTagsModule.PlayerEntry e)
		{
			try
			{
				if (e?.Player == null) return null;
				return FewTagsModule.GetMemberByTypeName(e.Player, "VRCPlayerApi", "prop_VRCPlayerApi_0", "field_Public_VRCPlayerApi_0")
					as VRC.SDKBase.VRCPlayerApi;
			}
			catch { return null; }
		}

		// VR: the other player whose ANY body bone is within reach of my hand.
		private static VaTagsModule.PlayerEntry NearestByBones(Vector3 handPos, float reach)
		{
			VaTagsModule.PlayerEntry best = null; float bestD = reach;
			foreach (var e in VaTagsModule.Roster)
			{
				if (e == null || e.IsLocal || string.IsNullOrEmpty(e.UserId)) continue;
				if (e.HasPos && Vector3.Distance(e.Position, handPos) > 3.5f) continue;   // cheap gate
				var api = ApiOf(e); if (api == null) continue;
				for (int i = 0; i < ReachBones.Length; i++)
				{
					Vector3 b; try { b = api.GetBonePosition(ReachBones[i]); } catch { continue; }
					if (b == Vector3.zero) continue;
					float d = Vector3.Distance(b, handPos);
					if (d < bestD) { bestD = d; best = e; }
				}
			}
			return best;
		}

		// Desktop: the other player nearest to the crosshair (smallest angle), within range.
		private static VaTagsModule.PlayerEntry NearestToCrosshair(float range)
		{
			var cam = Camera.main; if (cam == null) return null;
			Vector3 o = cam.transform.position, f = cam.transform.forward;
			VaTagsModule.PlayerEntry best = null; float bestAng = 8f;
			foreach (var e in VaTagsModule.Roster)
			{
				if (e == null || e.IsLocal || string.IsNullOrEmpty(e.UserId)) continue;
				Vector3 head = Vector3.zero;
				var api = ApiOf(e);
				if (api != null) { try { head = api.GetBonePosition(HumanBodyBones.Head); } catch { } }
				if (head == Vector3.zero) head = e.Position + Vector3.up * 1.4f;
				Vector3 to = head - o; float d = to.magnitude;
				if (d > range || d < 0.3f) continue;
				float ang = Vector3.Angle(f, to);
				if (ang < bestAng) { bestAng = ang; best = e; }
			}
			return best;
		}

		// ---------------------------------------------------------------- relay

		// ONE SENDER, IN ORDER — the fix for "it works, except sometimes, with two of us".
		//
		// Every event used to leave in its own Task.Run, so two produced a frame apart RACED each
		// other to the relay and could arrive swapped. The pair that matters is grab → release: in
		// that order everything is fine; reversed, the held client takes the release for an event
		// about nothing, then the grab, and stays stuck to a hand that let go seconds ago — until the
		// 3.5 s heartbeat watchdog drops them. Two people in one instance is exactly when it shows,
		// because it takes two clients pushing the same conversation at once.
		//
		// A queue with a single consumer makes the order on the wire the order they were produced.
		// Nothing else changes: same POSTs, same payloads, same handlers.
		private static readonly ConcurrentQueue<(string kind, string to, string body)> _outbox
			= new ConcurrentQueue<(string, string, string)>();
		private static volatile bool _sending;

		/// <summary>Send one relay event to one mod user (the player-grab handshake).</summary>
		public static void Send(string toUid, string kind, string hand, string data = null)
		{
			string me = VaTagsModule.LocalUserId();
			if (string.IsNullOrEmpty(me) || string.IsNullOrEmpty(toUid)) return;
			var dict = new Dictionary<string, object> { ["to"] = toUid, ["from"] = me, ["kind"] = kind, ["hand"] = hand ?? "" };
			if (!string.IsNullOrEmpty(data)) dict["data"] = data;
			_outbox.Enqueue((kind, toUid, JsonSerializer.Serialize(dict)));
			PumpOutbox();
		}

		/// <summary>Drains the outbox one POST at a time. Safe to call from anywhere and as often as
		/// you like: it returns at once when a drain is already running.</summary>
		private static void PumpOutbox()
		{
			if (_sending) return;
			_sending = true;
			Task.Run(async () =>
			{
				try
				{
					while (_outbox.TryDequeue(out var item))
					{
						try
						{
							var (ok, raw, code) = await VaAuth.PostAsync("/api/grab/send", item.body);
							VRChatArchiveModPlugin.Logger.LogInfo($"[PlayerGrab] send {item.kind} -> {item.to} : {(ok ? "ok" : "FAIL " + code + " " + (raw ?? "").Substring(0, Math.Min(120, (raw ?? "").Length)))}");
							if (!ok) Main.Enqueue(() => Status = code == 401
								? "login required — connect your VRChat Archive account (your tag ▸ Connect)"
								: "relay error " + code);
						}
						catch (Exception e) { Main.Enqueue(() => Status = "relay: " + e.Message); }
					}
				}
				finally { _sending = false; }
				// An Enqueue can land between the last failed TryDequeue and _sending going false, and
				// its PumpOutbox would have seen a drain still running. One more look closes that
				// window, and OnUpdate pumps too, so nothing waits in the queue longer than a frame.
				if (!_outbox.IsEmpty) PumpOutbox();
			});
		}

		public static void EnsurePolling()
		{
			if (_polling) return;
			_polling = true;
			Task.Run(async () =>
			{
				int backoff = 0;
				while (true)
				{
					string me = VaTagsModule.LocalUserId();
					if (string.IsNullOrEmpty(me)) { await Task.Delay(2000); continue; }
					try
					{
						var (ok, raw, code) = await VaAuth.PostAsync("/api/grab/poll", "{\"me\":\"" + me + "\",\"wait\":4}");
						if (!ok)
						{
							if (backoff == 0) VRChatArchiveModPlugin.Logger.LogWarning($"[PlayerGrab] poll FAIL {code}: {(raw ?? "").Substring(0, Math.Min(160, (raw ?? "").Length))}");
							if (code == 401) Main.Enqueue(() => Status = "login required — connect your VRChat Archive account (your tag ▸ Connect)");
							backoff = Math.Min(backoff + 1, 5);
							await Task.Delay(code == 401 ? 60000 : 1000 * backoff);   // not logged in: ask again in a minute, not five times a second
							continue;
						}
						backoff = 0;
						using var doc = JsonDocument.Parse(raw);
						if (doc.RootElement.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
							foreach (var ev in evs.EnumerateArray())
							{
								string from = ev.TryGetProperty("from", out var f) ? f.GetString() : null;
								string kind = ev.TryGetProperty("kind", out var k) ? k.GetString() : null;
								string hand = ev.TryGetProperty("hand", out var h) ? h.GetString() : "";
								string data = ev.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.String ? dd.GetString() : "";
								if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(kind)) continue;
								VRChatArchiveModPlugin.Logger.LogInfo($"[PlayerGrab] event {kind} from {from} hand={hand}");
								Main.Enqueue(() => HandleEvent(from, kind, hand ?? "", data ?? ""));
							}
					}
					catch { await Task.Delay(1500); }
				}
			});
		}

		private static void HandleEvent(string from, string kind, string hand, string data)
		{
			switch (kind)
			{
				case "grab": OnGrabRequest(from, hand); break;
				case "hold": if (from == _heldByUid) _lastHoldAt = Time.realtimeSinceStartup; break;
				case "release": if (from == _heldByUid) OnReleased(); break;
				case "ack":
					if (from == _pendingUid)
					{
						_holdingUid = _pendingUid; _holdingName = _pendingName; _pendingUid = _pendingName = null;
						_nextHold = Time.realtimeSinceStartup + 1f;
						StateText = "holding " + _holdingName; Status = "holding " + _holdingName + " — open your hand to throw";
						try { PlayerRef.LocalApi()?.PlayHapticEventInHand(_holdingHand == "L" ? VRC.SDKBase.VRC_Pickup.PickupHand.Left : VRC.SDKBase.VRC_Pickup.PickupHand.Right, 0.3f, 1f, 40f); } catch { }
					}
					break;
				case "nack":
					if (from == _pendingUid) { Status = _pendingName + " can't be grabbed (their player grab is off)"; _pendingUid = _pendingName = null; }
					break;
				case "escaped":
					if (from == _holdingUid) { Status = _holdingName + " broke free"; _holdingUid = _holdingName = null; StateText = ""; }
					break;
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// New world: nobody is holding anybody any more.
			_holdingUid = _holdingName = _pendingUid = _pendingName = null;
			ReleaseSelf(false); _inertiaUntil = 0f; StateText = "";
		}

		public override void OnShutdown() { if (Active) TurnOff("off"); }
	}
}
