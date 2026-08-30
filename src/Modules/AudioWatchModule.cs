using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// AUDIO WATCH — records which AudioClips actually PLAY, with a timestamp and the object that
	// played them.
	//
	// Static analysis can tell you a clip exists; only this tells you which one VRChat plays when
	// you hover a menu button. That is exactly what we needed to give our own QuickMenu cards the
	// game's real click sound instead of guessing at a clip called "click".
	//
	// Purely observational: it polls AudioSources for a rising edge of isPlaying. It never plays,
	// stops, mutes or modifies anything.
	public class AudioWatchModule : IModule
	{
		public override string Name => "AudioWatch";

		private const float PollInterval = 0.1f;      // 10 Hz is plenty for catching a UI blip
		private const int Capacity = 300;

		public sealed class Hit
		{
			public string Clock;
			public string Clip;
			public string Source;
			public int Count = 1;
		}

		private static readonly List<Hit> Hits = new List<Hit>(Capacity);
		private static readonly Dictionary<int, bool> WasPlaying = new Dictionary<int, bool>();
		private static readonly object Gate = new object();
		public static int TotalSeen { get; private set; }

		private float _nextPoll;
		private float _nextRescan;
		private readonly List<AudioSource> _sources = new List<AudioSource>();
		private StreamWriter _sink;

		public override void OnInitialize()
		{
			if (!Enabled) return;
			try
			{
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "audio");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "played_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
				_sink = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
				_sink.WriteLine("VRCHAT ARCHIVE MOD — clips that actually played (observed, nothing was triggered)");
				_sink.WriteLine("time      clip                                     source");
				VRChatArchiveModPlugin.Logger.LogInfo("[AudioWatch] recording to " + path);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[AudioWatch] sink failed: {e.Message}"); }
		}

		// On in debug builds, or when explicitly switched on.
		private static bool Enabled
		{
			get
			{
				try { if (ModConfig.AudioWatchEnabled != null && ModConfig.AudioWatchEnabled.Value) return true; } catch { }
				try { return DiagnosticsModule.Debug; } catch { return false; }
			}
		}

		public override void OnUpdate()
		{
			if (!Enabled) return;
			try
			{
				float now = Time.realtimeSinceStartup;

				// The source list is rebuilt rarely: scanning every loaded object is the expensive
				// part, and audio sources do not appear every frame.
				if (now >= _nextRescan)
				{
					_nextRescan = now + 10f;
					Rescan();
				}

				if (now < _nextPoll) return;
				_nextPoll = now + PollInterval;

				for (int i = 0; i < _sources.Count; i++)
				{
					var a = _sources[i];
					if (a == null) continue;
					int id;
					bool playing;
					try { id = a.GetInstanceID(); playing = a.isPlaying; } catch { continue; }

					WasPlaying.TryGetValue(id, out bool was);
					WasPlaying[id] = playing;
					if (!playing || was) continue;      // only the rising edge is a new sound

					string clip = "(none)";
					try { if (a.clip != null) clip = a.clip.name; } catch { }
					string src = "?";
					try { src = a.gameObject.name; } catch { }
					Record(clip, src);
				}

				lock (Gate) { try { _sink?.Flush(); } catch { } }
			}
			catch { }
		}

		public override void OnShutdown()
		{
			lock (Gate) { try { _sink?.Flush(); _sink?.Dispose(); } catch { } _sink = null; }
		}

		private void Rescan()
		{
			try
			{
				_sources.Clear();
				foreach (var a in Resources.FindObjectsOfTypeAll<AudioSource>())
				{
					if (a == null) continue;
					try { if (!a.gameObject.scene.IsValid()) continue; } catch { continue; }
					_sources.Add(a);
				}
			}
			catch { }
		}

		private void Record(string clip, string source)
		{
			TotalSeen++;
			lock (Gate)
			{
				if (Hits.Count > 0)
				{
					var last = Hits[Hits.Count - 1];
					if (last.Clip == clip && last.Source == source) { last.Count++; return; }
				}
				Hits.Add(new Hit { Clock = DateTime.Now.ToString("HH:mm:ss"), Clip = clip, Source = source });
				if (Hits.Count > Capacity) Hits.RemoveAt(0);
				try { _sink?.WriteLine($"{DateTime.Now:HH:mm:ss}  {Trunc(clip, 40),-40} {Trunc(source, 46)}"); }
				catch { }
			}
		}

		public static List<Hit> Snapshot() { lock (Gate) return new List<Hit>(Hits); }

		// The clip most likely to be VRChat's UI click: seen playing from the menu's own sound
		// player. Read by the QuickMenu tab so our cards use the game's real sound.
		public static AudioClip GuessUiClick()
		{
			try
			{
				foreach (var a in Resources.FindObjectsOfTypeAll<AudioSource>())
				{
					if (a == null) continue;
					try
					{
						if (a.name != "SoundPlayer") continue;
						if (a.clip != null) return a.clip;
					}
					catch { }
				}
			}
			catch { }
			return null;
		}

		private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));
	}
}
