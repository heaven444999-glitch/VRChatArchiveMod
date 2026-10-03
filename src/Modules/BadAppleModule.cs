using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// Fun: plays Bad Apple!! in the VRChat chatbox as hanzi shadow-art, over OSC —
	// same trick as the "Bad Apple in chinese characters" video: real, simple hanzi
	// ordered by measured ink coverage act as the grey ramp, so each frame reads like
	// a block of genuine Chinese text.
	//
	// Frames are pre-baked from the PV (tools/bake_badapple.py -> ressources/badapple.frames,
	// embedded in the DLL): a 10x7 grid of 32 grey levels (base-36 digits). The 32-glyph
	// ramp lives in ressources/badapple.charset (built by tools/pick_charset.py, which
	// renders candidate hanzi and measures their brightness).
	//
	// Frames are baked DENSE (50 ms apart) and playback samples them at the configured
	// cadence, so any IntervalMs keeps the full 3:39 runtime and stays in sync with the
	// music. Default cadence is 500 ms (validated in-game 2026-08-20; ~450 ms observed
	// working on other clients). The spam filter that matters is enforced by the
	// network/RECEIVING clients (not ours — we can't patch other people's); values
	// below ~450 ms are uncharted territory and may get the bubble muted for viewers.
	//
	// Delivery is plain OSC to VRChat's own local input port (/chatbox/input, UDP :9000) —
	// no game internals touched, but OSC must be enabled in the radial menu
	// (Options > OSC > Enabled). Right-Shift+B toggles playback; stopping clears the bubble.
	public class BadAppleModule : IModule
	{
		public override string Name => "BadApple";

		public static bool Playing => _playing;

		/// <summary>The baked frames, for other renderers (the Mark module's object Bad Apple).</summary>
		internal static bool TryGetFrames(out string[] frames, out int w, out int h, out int intervalMs)
		{
			frames = null; w = h = intervalMs = 0;
			if (!EnsureFramesLoaded()) return false;
			frames = _frames; w = _w; h = _h; intervalMs = _bakedIntervalMs;
			return frames != null && frames.Length > 0;
		}

		private static volatile bool _playing;
		private static Thread _thread;
		private static ManualResetEventSlim _stopSignal;
		private static bool _hotkeyWasDown;

		// Parsed once from the embedded frames file.
		private const int Levels = 32;            // base-36 digits 0..v, 0 = lightest
		private const int DefaultIntervalMs = 500; // SEND mode's proven-safe cadence in-game
		private static int _w, _h, _bakedIntervalMs;
		private static bool _newlineSep;   // false = rely on the bubble's natural wrap
		private static string[] _frames;
		private static string _bakedCharset;

		public override void OnUpdate()
		{
			bool combo = Input.GetKey(KeyCode.RightShift) && Input.GetKey(KeyCode.B);
			if (combo && !_hotkeyWasDown) RequestToggle();
			_hotkeyWasDown = combo;
		}

		public override void OnShutdown() => Stop();

		// Called from the hotkey and from the menu button (main thread).
		public static void RequestToggle()
		{
			if (_playing) Stop();
			else Start();
		}

		private static void Start()
		{
			if (_playing) return;
			if (!EnsureFramesLoaded()) return;

			_stopSignal = new ManualResetEventSlim(false);
			_playing = true;
			_thread = new Thread(PlaybackLoop) { IsBackground = true, Name = "BadAppleChatbox" };
			_thread.Start();
			Killiorim.Logger.LogInfo(
				$"[BadApple] playback started ({_frames.Length} frames). OSC must be enabled in-game (radial menu > Options > OSC).");
		}

		private static void Stop()
		{
			if (!_playing) return;
			_stopSignal?.Set();
		}

		// Runs on a background thread: pure .NET UDP, no Unity API allowed here.
		private static void PlaybackLoop()
		{
			UdpClient udp = null;
			try
			{
				udp = new UdpClient();
				udp.Connect(ModConfig.BadAppleOscHost.Value, ModConfig.BadAppleOscPort.Value);

				int interval = ModConfig.BadAppleIntervalMs.Value > 0
					? Mathf.Max(100, ModConfig.BadAppleIntervalMs.Value)
					: DefaultIntervalMs;
				if (interval < 450)
					Killiorim.Logger.LogWarning(
						$"[BadApple] cadence {interval} ms is below the ~500 ms the chatbox spam "
						+ "filter tolerates — viewers' bubbles may stop updating.");

				// Any charset length >= 2 works: baked levels are rescaled onto it.
				string charset = ModConfig.BadAppleCharset.Value;
				if (string.IsNullOrEmpty(charset) || charset.Length < 2) charset = _bakedCharset;

				do
				{
					// Absolute schedule (start + i*interval) so drift can't accumulate
					// over the ~3.5 minute run. Each send picks the dense baked frame
					// nearest to the elapsed time, so any cadence keeps video timing.
					var sw = Stopwatch.StartNew();
					for (int i = 0; ; i++)
					{
						long elapsed = (long)i * interval;
						int idx = (int)((elapsed + _bakedIntervalMs / 2) / _bakedIntervalMs);
						if (idx >= _frames.Length) break;
						Send(udp, RenderFrame(_frames[idx], charset));
						long wait = elapsed + interval - sw.ElapsedMilliseconds;
						if (wait > 0 && _stopSignal.Wait((int)wait)) break;
						if (_stopSignal.IsSet) break;
					}
				} while (ModConfig.BadAppleLoop.Value && !_stopSignal.IsSet);

				Send(udp, "");   // empty send clears the bubble on the way out
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogError($"[BadApple] playback thread threw: {e}");
			}
			finally
			{
				try { udp?.Close(); } catch { }
				_playing = false;
				Killiorim.Logger.LogInfo("[BadApple] playback stopped.");
			}
		}

		private static string RenderFrame(string digits, string charset)
		{
			var sb = new StringBuilder(digits.Length + _h);
			for (int r = 0; r < _h; r++)
			{
				if (r > 0 && _newlineSep) sb.Append('\n');
				for (int c = 0; c < _w; c++)
				{
					char d = digits[r * _w + c];
					int level = d <= '9' ? d - '0' : d - 'a' + 10;
					// Rescale 0..31 onto the charset (rounded), so custom ramps of any size work.
					sb.Append(charset[(level * (charset.Length - 1) + (Levels - 1) / 2) / (Levels - 1)]);
				}
			}
			return sb.ToString();
		}

		private static void Send(UdpClient udp, string text)
		{
			// /chatbox/input ,sTF <text> : T = bypass the keyboard and send now, F = no SFX.
			// (A keyboard/"typing preview" variant exists but renders wrong in-game, so the
			// player always does real sends.)
			var ms = new MemoryStream(256);
			WriteOscString(ms, "/chatbox/input");
			WriteOscString(ms, ",sTF");
			WriteOscString(ms, text);
			byte[] packet = ms.ToArray();
			udp.Send(packet, packet.Length);
		}

		// OSC strings: UTF-8 bytes, null-terminated, padded with nulls to a multiple of 4.
		private static void WriteOscString(MemoryStream ms, string s)
		{
			byte[] b = Encoding.UTF8.GetBytes(s);
			ms.Write(b, 0, b.Length);
			int pad = 4 - (b.Length % 4);
			for (int i = 0; i < pad; i++) ms.WriteByte(0);
		}

		private static bool EnsureFramesLoaded()
		{
			if (_frames != null) return true;
			try
			{
				using (var cs = typeof(BadAppleModule).Assembly.GetManifestResourceStream("badapple.charset"))
				using (var cr = new StreamReader(cs, Encoding.UTF8))
					// TrimEnd only, and never a bare Trim(): the ramp STARTS with U+3000
					// (ideographic space, level 0), which .NET counts as whitespace.
					_bakedCharset = cr.ReadToEnd().TrimEnd('\r', '\n');

				using var stream = typeof(BadAppleModule).Assembly.GetManifestResourceStream("badapple.frames");
				using var reader = new StreamReader(stream, Encoding.ASCII);
				string[] header = reader.ReadLine().Split(' ');
				_w = int.Parse(header[0]);
				_h = int.Parse(header[1]);
				_bakedIntervalMs = int.Parse(header[2]);
				_newlineSep = header.Length < 4 || header[3] != "0";
				var frames = new System.Collections.Generic.List<string>(256);
				string line;
				while ((line = reader.ReadLine()) != null)
					if (line.Length == _w * _h) frames.Add(line);
				_frames = frames.ToArray();
				return _frames.Length > 0;
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogError($"[BadApple] failed to load embedded frames: {e}");
				_frames = null;
				return false;
			}
		}
	}
}
