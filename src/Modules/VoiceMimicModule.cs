using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// VOICE MIMIC — your outgoing voice BECOMES the target's voice stream. Nothing else moves.
	//
	// THE MODEL (2026-09-02, third and final shape, the one the owner asked for): "my voice should
	// be the copied stream, that's all". VRChat voice travels as Photon event 7: each talker's
	// client raises event 7 with an encoded (Opus) packet, every other client decodes it on that
	// talker's USpeaker. So the copy is a RELAY at the packet level:
	//   * every event 7 that arrives FROM THE TARGET is re-raised by us as our own event 7,
	//   * our own microphone's event 7 is dropped while the relay is on (otherwise two streams),
	//   * the packet header is patched from their actor number to ours when, and only when, the
	//     first four bytes read exactly as their actor number (self-validating: if the format is
	//     different the bytes are left alone and the log says so).
	// No avatar, bone, IK or position is touched. Ever. The two earlier versions (teleport to
	// their head; write our head bone onto theirs) are gone.
	//
	// WHAT IT IS NOT. It does not decode audio, does not mix, does not touch USpeak (whose class is
	// obfuscated on this build). It moves bytes from one Photon event to another. The receiving
	// clients decode our relayed packet with the same codec they decode the original with.
	//
	// HOOKS. Receive: postfix on VRCNetworkingClient.OnEvent(EventData) — the funnel NetworkLog and
	// PhotonGuard already use. Send: prefix on Photon's OpRaiseEvent(byte, object,
	// RaiseEventOptions, SendOptions), found by signature the way NetSendModule finds it; the same
	// MethodInfo is what the relay calls to send. Option objects are built from the method's own
	// parameter types, so no Photon type name is assumed.
	public class VoiceMimicModule : IModule
	{
		public override string Name => "VoiceMimic";

		public static string TargetUid { get; private set; } = "";
		public static string TargetName { get; private set; } = "";
		public static bool Active => !string.IsNullOrEmpty(TargetUid);
		public static string Status = "";

		// Voice rides Photon event code 1 on this build (VoiceProbe, 2026-09-02: code 1 sender count
		// tracked the talkers exactly). Read from config so it can be retargeted without a rebuild.
		private static byte _code = 1;
		private static byte VoiceCode => _code;
		private static void RefreshCode() { try { int c = ModConfig.VoiceMimicCode != null ? ModConfig.VoiceMimicCode.Value : 1; _code = (byte)(c < 0 ? 0 : c > 255 ? 255 : c); } catch { _code = 1; } }
		private static bool MuteSelf { get { try { return ModConfig.VoiceMimicMuteSelf != null && ModConfig.VoiceMimicMuteSelf.Value; } catch { return false; } } }

		private static int _targetActor = -1;
		private static int _localActor = -1;
		private static float _nextResolve;

		// Counters for the status line and the log.
		private static int _relayed, _droppedOwn, _headerPatched, _headerLeft;
		private static bool _firstLogged;

		// Re-entrancy flag: our own relayed sends must pass the "drop own mic" prefix.
		[ThreadStatic] private static bool _sending;

		// ---------------------------------------------------------------- public API

		public static void Toggle(VaTagsModule.PlayerEntry e)
		{
			if (e == null) { Status = "no such player"; return; }
			if (Active && string.Equals(e.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) { Stop("stopped"); return; }
			Start(e);
		}

		public static void Start(VaTagsModule.PlayerEntry e)
		{
			if (e == null || e.IsLocal) { Status = "pick somebody else"; Toast.Show("voice mimic: pick somebody else"); return; }
			EnsureHooks();
			if (!_recvHooked || _raise == null)
			{
				Status = "voice relay unavailable: " + HookInfo;
				Toast.Show("voice mimic: " + Status);
				Killiorim.Logger.LogWarning("[VoiceMimic] " + Status);
				return;
			}

			TargetUid = e.UserId ?? "";
			TargetName = e.Name ?? "";
			_targetActor = e.PlayerId;
			_localActor = LocalActor();
			RefreshCode();
			_relayed = _droppedOwn = _headerPatched = _headerLeft = 0;
			_firstLogged = false;
			_nextResolve = 0f;

			Status = "relaying voice of " + TargetName;
			Toast.Show("voice mimic: " + TargetName);
			Killiorim.Logger.LogInfo("[VoiceMimic] relay ON: target " + TargetName + " (actor " + _targetActor + ") -> us (actor " + _localActor + ") on Photon code " + _code + ". MuteSelf=" + MuteSelf + " (off = stay muted yourself).");
		}

		public static void Stop(string why)
		{
			if (!Active) return;
			Killiorim.Logger.LogInfo("[VoiceMimic] relay OFF (" + why + "): relayed " + _relayed + " packet(s) from " + TargetName
				+ ", dropped " + _droppedOwn + " of our own, header patched " + _headerPatched + " / left " + _headerLeft + ".");
			TargetUid = ""; TargetName = ""; _targetActor = -1;
			Status = why;
			Toast.Show("voice mimic: " + why);
		}

		// ---------------------------------------------------------------- lifecycle

		public override void OnInitialize() { EnsureHooks(); }

		public override void OnSceneLoaded(int buildIndex) { if (Active) Stop("world changed"); }

		public override void OnUpdate()
		{
			if (!Active) return;
			try
			{
				float now = Time.realtimeSinceStartup;
				if (now < _nextResolve) return;
				_nextResolve = now + 2f;

				// Actor numbers are per room and a rejoin renumbers everyone; re-read both on a slow
				// tick from the roster, and stop when the target is gone.
				var entry = FindEntry();
				if (entry == null) { Stop("they left"); return; }
				if (entry.PlayerId > 0) _targetActor = entry.PlayerId;
				RefreshCode();
				int la = LocalActor(); if (la > 0) _localActor = la;
				Status = "relaying voice of " + TargetName + " (" + _relayed + " packets)";
			}
			catch { }
		}

		private static VaTagsModule.PlayerEntry FindEntry()
		{
			try
			{
				foreach (var p in VaTagsModule.Roster)
					if (p != null && string.Equals(p.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) return p;
			}
			catch { }
			return null;
		}

		private static int LocalActor()
		{
			try { var lp = VRC.SDKBase.Networking.LocalPlayer; return lp != null ? lp.playerId : -1; }
			catch { return -1; }
		}

		// ---------------------------------------------------------------- hooks

		private static bool _recvHooked, _sendHooked, _tried;
		public static string HookInfo = "not attached";
		private static MethodInfo _raise;          // OpRaiseEvent(byte, object, RaiseEventOptions, SendOptions)
		private static object _raiseTarget;        // the LoadBalancingClient instance, captured from the send prefix
		private static Type _optType, _sendOptType;
		private static PropertyInfo _pCode, _pSender, _pCustom;

		private static void EnsureHooks()
		{
			if (_tried) return;
			_tried = true;
			var notes = new StringBuilder();
			// RECEIVE.
			try
			{
				Type client = FindType("VRCNetworkingClient");
				MethodInfo target = null;
				if (client != null)
					foreach (var m in client.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
					{
						if (m.Name != "OnEvent") continue;
						var ps = m.GetParameters();
						if (ps.Length == 1 && ps[0].ParameterType.Name.Contains("EventData")) { target = m; break; }
					}
				if (target != null)
				{
					Killiorim.HarmonyInstance.Patch(target, postfix: new HarmonyMethod(typeof(VoiceMimicModule).GetMethod(nameof(OnEventPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
					_recvHooked = true; notes.Append("recv=VRCNetworkingClient.OnEvent ");
				}
				else notes.Append("recv=OnEvent(EventData) not found ");
			}
			catch (Exception e) { notes.Append("recv failed: ").Append(e.Message).Append(' '); }

			// SEND: the same signature hunt NetSendModule does. The FIRST match is also what we call.
			try
			{
				foreach (string typeName in new[] { "Photon.Realtime.LoadBalancingClient", "LoadBalancingClient", "VRCNetworkingClient" })
				{
					Type t = FindType(typeName);
					if (t == null) continue;
					foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
					{
						var ps = m.GetParameters();
						if (ps.Length != 4 || ps[0].ParameterType != typeof(byte)) continue;
						if (ps[2].ParameterType.Name.IndexOf("RaiseEventOptions", StringComparison.Ordinal) < 0) continue;
						if (ps[3].ParameterType.Name.IndexOf("SendOptions", StringComparison.Ordinal) < 0) continue;
						Killiorim.HarmonyInstance.Patch(m, prefix: new HarmonyMethod(typeof(VoiceMimicModule).GetMethod(nameof(RaisePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
						if (_raise == null) { _raise = m; _optType = ps[2].ParameterType; _sendOptType = ps[3].ParameterType; }
						_sendHooked = true; notes.Append("send=").Append(t.Name).Append('.').Append(m.Name).Append(' ');
					}
				}
				if (!_sendHooked) notes.Append("send=OpRaiseEvent signature not found ");
			}
			catch (Exception e) { notes.Append("send failed: ").Append(e.Message).Append(' '); }

			HookInfo = notes.ToString().Trim();
			Killiorim.Logger.LogInfo("[VoiceMimic] hooks: " + HookInfo);
		}

		// SEND PREFIX. Two jobs: remember the client instance we must call OpRaiseEvent on, and drop
		// our own microphone's event 7 while the relay runs (our relayed sends set _sending).
		private static bool RaisePrefix(object __instance, byte __0)
		{
			try
			{
				if (_raiseTarget == null && __instance != null) _raiseTarget = __instance;
				if (Active && MuteSelf && __0 == VoiceCode && !_sending) { _droppedOwn++; return false; }
			}
			catch { }
			return true;
		}

		// RECEIVE POSTFIX. Runs for every inbound event; does nothing unless the relay is on, the
		// code is voice, and the sender is the target.
		private static void OnEventPostfix(object __0)
		{
			if (!Active || __0 == null) return;
			try
			{
				var t = __0.GetType();
				_pCode ??= t.GetProperty("Code");
				_pSender ??= t.GetProperty("Sender");
				_pCustom ??= t.GetProperty("CustomData");
				if (_pCode == null || _pSender == null || _pCustom == null) return;
				if ((byte)_pCode.GetValue(__0) != VoiceCode) return;
				if ((int)_pSender.GetValue(__0) != _targetActor) return;

				object data = _pCustom.GetValue(__0);
				var arr = (data as Il2CppObjectBase)?.TryCast<Il2CppStructArray<byte>>();
				if (arr == null) { if (!_firstLogged) { _firstLogged = true; Killiorim.Logger.LogWarning("[VoiceMimic] voice payload is not a byte[] (" + (data?.GetType().Name ?? "null") + ") - cannot relay on this build."); } return; }

				int n = arr.Length;
				if (n <= 0) return;
				var bytes = new byte[n];
				for (int i = 0; i < n; i++) bytes[i] = arr[i];

				// Header: patch their actor number to ours ONLY if the first int reads as theirs.
				bool patched = false;
				if (n >= 4 && BitConverter.ToInt32(bytes, 0) == _targetActor && _localActor > 0)
				{
					var mine = BitConverter.GetBytes(_localActor);
					Buffer.BlockCopy(mine, 0, bytes, 0, 4);
					patched = true; _headerPatched++;
				}
				else _headerLeft++;

				if (!_firstLogged)
				{
					_firstLogged = true;
					var hex = new StringBuilder();
					for (int i = 0; i < Math.Min(12, n); i++) hex.Append(arr[i].ToString("x2")).Append(' ');
					Killiorim.Logger.LogInfo("[VoiceMimic] first voice packet from actor " + _targetActor + ": " + n + " bytes, head " + hex.ToString().Trim()
						+ (patched ? " (actor header patched to " + _localActor + ")" : " (no actor header recognised; sent as-is)"));
				}

				Send(bytes);
			}
			catch (Exception e)
			{
				if (!_firstLogged) { _firstLogged = true; Killiorim.Logger.LogWarning("[VoiceMimic] relay read failed: " + e.Message); }
			}
		}

		private static object _opts;        // RaiseEventOptions instance, built once from the parameter type
		private static object _sendOpts;    // SendOptions (struct) default = unreliable, like voice

		private static void Send(byte[] bytes)
		{
			if (_raise == null || _raiseTarget == null) return;
			try
			{
				_opts ??= Activator.CreateInstance(_optType);
				_sendOpts ??= Activator.CreateInstance(_sendOptType);
				var payload = new Il2CppStructArray<byte>(bytes);
				object boxed = new Il2CppSystem.Object(payload.Pointer);
				_sending = true;
				try { _raise.Invoke(_raiseTarget, new object[] { VoiceCode, boxed, _opts, _sendOpts }); }
				finally { _sending = false; }
				_relayed++;
			}
			catch (Exception e)
			{
				if (_relayed == 0) { Killiorim.Logger.LogWarning("[VoiceMimic] relay send failed: " + e.Message); Stop("send failed - " + Short(e.Message)); }
			}
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = null;
				try { t = asm.GetType(full, false); } catch { }
				if (t != null) return t;
			}
			return null;
		}

		private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 80 ? s.Substring(0, 79) + "…" : s);
	}
}
