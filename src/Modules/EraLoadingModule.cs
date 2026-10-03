using System;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using UnityEngine.Networking;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// Loading screen overlay: opaque black sky, a procedural starfield and a restrained
	// indeterminate loader. Loading detection stays passive; VRChat's world-loading machinery
	// is never patched or replaced.
	//
	// Loading is detected passively: VRChat's active scene changes and the local player
	// disappears while a world loads, so we watch for that instead of hooking anything.
	public class EraLoadingModule : IModule
	{
		public override string Name => "EraLoading";

		private const float GraceSeconds = 0.6f;    // ignore blips shorter than this
		private const float MaxLoadSeconds = 90f;   // ceiling: the track can never outlast a real load

		// True once the local player exists in the scene — i.e. we have actually spawned in.
		// This is the ground truth for "loading is over"; the popup can linger past it.
		private static bool LocalPlayerInWorld()
		{
			try { return PlayerRef.LocalTransform() != null; } catch { return false; }
		}

		private bool _loading;
		// Set when a HARD STOP ended a load while the popup was still reporting "active".
		// Without it the next tick saw popup=active + _loading=false and started the whole load
		// over again — the music restarting forever, which is exactly what "the loading music
		// never stops" looked like. Cleared only when the popup genuinely goes inactive.
		private bool _suppressed;
		private float _loadingSince;
		private int _frame;

		// The game's own loading popup is the ground truth for "a world is loading":
		//   UserInterface/MenuContent/Popups/LoadingPopup
		// Watching its active state beats guessing from a missing local player, which also
		// goes null during ordinary avatar swaps.
		private readonly List<Transform> _loadingPopups = new List<Transform>();
		private float _popupRescanAt;
		private int _lastPopupCount = -1;

		// VRChat's own loading audio, silenced while our era track plays so ours REPLACES
		// it instead of layering on top. Everything is restored when loading ends.
		private readonly List<AudioSource> _ducked = new List<AudioSource>();

		// Era music, taken from the real builds' own scenes:
		//   2017  'PartiallyOffline'  (the era's own menu track, chosen for this screen)
		//   2018  'passing-through'   (LoadingPopup/LoadingSound in the real 2018 scene)
		// Both shipped as mono 22 kHz PCM: this build's IL2CPP cannot decode OGG at runtime.
		private static AudioSource _music;
		private static AudioClip _clip;
		private static bool _clipRequested;

		public override void OnInitialize()
		{
			RequestClip();
			Killiorim.Logger.LogInfo("[EraLoading] armed — starfield loading screen.");
		}

		public override void OnUpdate()
		{
			try
			{
				// DETECTION ALWAYS RUNS; THE TOGGLE ONLY DECIDES WHETHER THE SCREEN IS SHOWN.
				//
				// This used to `return` here, and that is almost certainly what got the module deleted:
				// with the feature off, DetectLoading() never ran, IsLoading stayed false for the entire
				// session, and SpawnSoundModule — which rode on that flag — silently stopped playing its
				// stinger while its own toggle still read ON. SpawnSoundModule.cs:12-16 still describes
				// that failure. One feature's toggle must not decide whether another feature RUNS.
				//
				// The teardown the early return existed for has not been lost: switching off mid-load
				// still stops the era track and unmutes VRChat's own loading sources, below, where the
				// audio is handled. It simply no longer takes detection down with it.
				bool show = ModConfig.LoadingScreenEnabled.Value;
				if (++_frame < 5) return;
				_frame = 0;

				// The popup is up during login too, so it only means "a world is loading" once the
				// pre-world screens are gone.
				bool nowLoading = DetectLoading() && !AtLoginScreen();

				// Release the latch only on a REAL falling edge of the popup.
				if (!nowLoading) _suppressed = false;
				else if (_suppressed) nowLoading = false;   // hard-stopped already: stay stopped

				// HARD STOPS — the popup alone proved unreliable: it can stay active (or keep being
				// found) after you are already in the world, which left the music looping forever.
				// Loading is over the moment we are actually IN the world, and can never outlast a
				// sane ceiling, whatever the popup claims.
				if (nowLoading && _loading)
				{
					if (Time.realtimeSinceStartup - _loadingSince > MaxLoadSeconds)
					{
						Killiorim.Logger.LogInfo("[EraLoading] load exceeded the ceiling — ending (music off).");
						nowLoading = false; _suppressed = true;
					}
					else if (Time.realtimeSinceStartup - _loadingSince >= GraceSeconds && LocalPlayerInWorld())
					{
						Killiorim.Logger.LogInfo("[EraLoading] local player is in the world — ending (music off).");
						nowLoading = false; _suppressed = true;
					}
				}

				if (nowLoading && !_loading)
				{
					_loading = true; IsLoading = true; _loadingSince = Time.realtimeSinceStartup;
					// Decisive evidence: until this line appears in a report we cannot claim the
					// era screen ever engaged, only that the popup object was found.
					Killiorim.Logger.LogInfo("[EraLoading] LOADING STARTED — drawing the starfield screen.");
				}
				else if (!nowLoading && _loading)
				{
					EndLoading();
				}

				// Audio waits out the same grace period as the visuals, so a popup blip can no
				// longer fire a burst of music plus a mute/unmute click on the game's audio.
				// And we only silence VRChat once OUR track is actually playing — ducking with
				// no replacement made every load dead silent when Era music was off or failed.
				if (show && _loading && !_audioStarted && Time.realtimeSinceStartup - _loadingSince >= GraceSeconds)
				{
					if (StartMusic()) { DuckGameAudio(); _audioStarted = true; }
				}
				// Switched off mid-load: give the game its audio back at once, exactly as the old
				// early return did, without pretending nothing is loading.
				else if (!show && _audioStarted)
				{
					StopMusic(); UnduckGameAudio(); _audioStarted = false;
					Killiorim.Logger.LogInfo("[EraLoading] switched off mid-load — audio restored, detection still running.");
				}
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[EraLoading] update threw: {e.Message}"); }
		}

		// A world change can destroy the popup we cached; re-arm the scan so detection re-resolves
		// it. (_loadingPopups is NOT cleared here — RescanPopups clears and refills it, and dropping
		// it early would blind detection at exactly the moment a load begins.)
		public override void OnSceneLoaded(int buildIndex) => _popupRescanAt = 0f;

		// One teardown path for every way loading can end (popup gone, feature switched off,
		// era changed, shutdown), so audio state can never be left half-applied.
		private void EndLoading()
		{
			if (!_loading && !_audioStarted) return;
			_loading = false; IsLoading = false;
			Killiorim.Logger.LogInfo("[EraLoading] loading ended.");
			if (_audioStarted) { StopMusic(); UnduckGameAudio(); _audioStarted = false; }
		}
		private bool _audioStarted;

		// Read by the diagnostics report.
		public static bool IsLoading { get; private set; }
		public static string MusicState =>
			_clip == null ? "no clip" : (_music != null && _music.isPlaying ? "playing" : "ready");

		// True while VRChat is loading a world.
		// True while the game's own loading popup is on screen. Several objects can share
		// the name (the popup, its mirrored VR copy, and prefab ASSETS that are never active),
		// so we test them all and ignore anything that isn't a live scene object — taking
		// only the first match made detection fail whenever a prefab was found first.
		// VRChat's pre-world screens live under UserInterface/MenuContent/Screens. While any of
		// them is up we are at boot / login / the title screen — NOT loading a world. The generic
		// LoadingPopup is also up during that whole phase, which is why the era track used to
		// start at launch and play for the entire login: the popup alone cannot tell the two
		// apart. Cached by name, rescanned only while nothing has been found.
		private readonly List<Transform> _loginScreens = new List<Transform>();
		private float _loginRescanAt;

		private bool AtLoginScreen()
		{
			try
			{
				float now = Time.realtimeSinceStartup;
				bool haveLive = false;
				for (int i = 0; i < _loginScreens.Count; i++)
					if (_loginScreens[i] != null) { haveLive = true; break; }

				if (!haveLive && now >= _loginRescanAt)
				{
					_loginRescanAt = now + 5f;
					_loginScreens.Clear();
					foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
					{
						if (t == null) continue;
						string n = t.name;
						if (n != "Authentication" && n != "FirstLogin" && n != "TitleXR" && n != "UpdateRequired") continue;
						try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
						_loginScreens.Add(t);
					}
				}

				for (int i = 0; i < _loginScreens.Count; i++)
				{
					var t = _loginScreens[i];
					if (IsLoginScreenVisible(t)) return true;
				}
			}
			catch { }
			return false;
		}

		private static bool IsLoginScreenVisible(Transform screen)
		{
			try
			{
				if (screen == null || !screen.gameObject.activeInHierarchy) return false;
				for (Transform t = screen; t != null; t = t.parent)
				{
					var group = t.GetComponent<CanvasGroup>();
					if (group != null && group.alpha <= 0.05f) return false;
				}
				return true;
			}
			catch { return false; }
		}

		private bool DetectLoading()
		{
			try
			{
				float now = Time.realtimeSinceStartup;
				// The throttle is armed BEFORE scanning: the old `Count == 0 ||` short-circuit
				// meant that while the popup did not exist yet (the whole boot/login phase) we
				// ran a full FindObjectsOfTypeAll<Transform>() every 5th frame and spammed the
				// log — a measurable frame-rate cost for nothing.
				// Once we HOLD a live popup reference there is nothing left to look for: the popup is
				// only deactivated between loads, never destroyed, so the cached Transform stays
				// valid and DetectLoading just re-reads activeInHierarchy off it. Re-sweeping every
				// loaded Transform every 5s for the rest of the session bought nothing and cost a
				// visible hitch in a heavy world. Scan only while we have no live entry.
				if (now >= _popupRescanAt)
				{
					bool haveLive = false;
					for (int i = 0; i < _loadingPopups.Count; i++)
						if (_loadingPopups[i] != null) { haveLive = true; break; }

					_popupRescanAt = now + (haveLive ? 60f : 2f);
					if (!haveLive) RescanPopups();
				}

				for (int i = 0; i < _loadingPopups.Count; i++)
				{
					Transform t = _loadingPopups[i];
					if (t == null) continue;
					if (t.gameObject.activeInHierarchy) return true;
				}
			}
			catch { }
			return false;
		}

		private void RescanPopups()
		{
			try
			{
				_loadingPopups.Clear();
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.name != "LoadingPopup") continue;
					// Prefab assets live outside any scene; only scene instances can be shown.
					// `catch { continue; }`, NOT `catch { }`. Three objects in this build are named
					// LoadingPopup: one live in level1, plus LoadingScreenCosmeticsHandler/LoadingPopup
					// and LoadingPopupOld as prefab copies in resources.assets. This test is the only
					// thing that separates them, so swallowing its exception is how a prefab copy
					// becomes a false positive. AtLoginScreen already gets this right.
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
					_loadingPopups.Add(t);
				}
				if (_loadingPopups.Count != _lastPopupCount)
				{
					_lastPopupCount = _loadingPopups.Count;
					Killiorim.Logger.LogInfo($"[EraLoading] loading popup candidates: {_loadingPopups.Count}.");
				}
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[EraLoading] popup scan failed: {e.Message}"); }
		}

		// Silence VRChat's own loading audio for the duration, so the era track replaces it.
		private void DuckGameAudio()
		{
			try
			{
				if (_ducked.Count > 0) return;
				for (int i = 0; i < _loadingPopups.Count; i++)
				{
					Transform root = _loadingPopups[i];
					if (root == null) continue;
					var sources = root.GetComponentsInChildren<AudioSource>(true);
					if (sources == null) continue;
					for (int k = 0; k < sources.Length; k++)
					{
						var a = sources[k];
						if (a == null || a.mute) continue;
						a.mute = true;
						_ducked.Add(a);
					}
				}
				if (_ducked.Count > 0)
					Killiorim.Logger.LogInfo($"[EraLoading] muted {_ducked.Count} of VRChat's own loading source(s).");
			}
			catch { }
		}

		private void UnduckGameAudio()
		{
			for (int i = 0; i < _ducked.Count; i++)
			{
				try { if (_ducked[i] != null) _ducked[i].mute = false; } catch { }
			}
			_ducked.Clear();
		}

		// Returns true only if the era track is really playing — the caller uses that to
		// decide whether it may silence VRChat's own loading audio.
		private bool StartMusic()
		{
			try
			{
				if (!ModConfig.LoadingMusic.Value) return false;
				if (_clip == null) { RequestClip(); return false; }
				if (_music == null)
				{
					var go = new GameObject("ArchiveEraMusic");
					UnityEngine.Object.DontDestroyOnLoad(go);
					go.hideFlags = HideFlags.HideAndDontSave;
					_music = go.AddComponent<AudioSource>();
					_music.loop = true;
					_music.spatialBlend = 0f;          // 2D, as in the original scenes
				}
				_music.clip = _clip;
				_music.volume = Mathf.Clamp01(ModConfig.LoadingMusicVolume.Value);
				if (!_music.isPlaying) _music.Play();
				return _music.isPlaying;
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[EraLoading] music start failed: {e.Message}"); return false; }
		}

		private static void StopMusic()
		{
			try { if (_music != null && _music.isPlaying) _music.Stop(); } catch { }
		}

		// Unity cannot decode an OGG straight from memory, so the embedded track is written
		// beside the mod once and then loaded from disk.
		// Loads the era track WITHOUT any Unity codec.
		//
		// This build's IL2CPP has no DownloadHandlerAudioClip(string, AudioType) - the log
		// said "Method not found", so UnityWebRequestMultimedia.GetAudioClip could never
		// return a clip and the music silently never played. We therefore ship plain PCM
		// WAV and hand the samples to AudioClip.Create/SetData, which this build does have.
		private void RequestClip()
		{
			if (_clipRequested) return;
			_clipRequested = true;
			try
			{
				string key = "2017";
				byte[] wav;
				using (var res = typeof(EraLoadingModule).Assembly.GetManifestResourceStream("era_music" + key + ".wav"))
				{
					if (res == null) { Killiorim.Logger.LogWarning("[EraLoading] embedded music missing."); return; }
					wav = new byte[res.Length];
					int read = 0;
					while (read < wav.Length)
					{
						int n = res.Read(wav, read, wav.Length - read);
						if (n <= 0) break;
						read += n;
					}
				}

				_clip = ClipFromWav(wav, "ArchiveEraMusic" + key);
				if (_clip != null)
				{
					Killiorim.Logger.LogInfo(
						$"[EraLoading] {key} music loaded ({_clip.length:F1}s, {_clip.frequency}Hz, {_clip.channels}ch).");
					if (_loading) StartMusic();
				}
				else Killiorim.Logger.LogWarning("[EraLoading] could not decode the embedded music.");
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[EraLoading] music load failed: {e.Message}"); }
		}

		// Minimal RIFF/WAVE reader: walks the chunks, accepts 16-bit PCM, converts to the
		// float samples AudioClip.SetData expects.
		private static AudioClip ClipFromWav(byte[] d, string name)
		{
			try
			{
				if (d == null || d.Length < 44) return null;
				if (d[0] != (byte)'R' || d[1] != (byte)'I' || d[2] != (byte)'F' || d[3] != (byte)'F') return null;

				int channels = 0, sampleRate = 0, bits = 0, dataAt = -1, dataLen = 0;
				int pos = 12;                                  // skip "RIFF####WAVE"
				while (pos + 8 <= d.Length)
				{
					string id = "" + (char)d[pos] + (char)d[pos + 1] + (char)d[pos + 2] + (char)d[pos + 3];
					int size = BitConverter.ToInt32(d, pos + 4);
					int body = pos + 8;
					if (size < 0 || body + size > d.Length) size = d.Length - body;
					if (id == "fmt ")
					{
						channels = BitConverter.ToInt16(d, body + 2);
						sampleRate = BitConverter.ToInt32(d, body + 4);
						bits = BitConverter.ToInt16(d, body + 14);
					}
					else if (id == "data") { dataAt = body; dataLen = size; }
					pos = body + size + (size & 1);            // chunks are word-aligned
				}
				if (dataAt < 0 || channels <= 0 || sampleRate <= 0 || bits != 16) return null;

				int count = dataLen / 2;
				var samples = new float[count];
				for (int i = 0; i < count; i++)
					samples[i] = BitConverter.ToInt16(d, dataAt + i * 2) / 32768f;

				var clip = AudioClip.Create(name, count / channels, channels, sampleRate, false);
				if (clip == null) return null;
				clip.SetData(samples, 0);
				clip.hideFlags = HideFlags.HideAndDontSave;
				return clip;
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogWarning($"[EraLoading] wav decode failed: {e.Message}");
				return null;
			}
		}


		public override void OnGui()
		{
			try
			{
				if (!ModConfig.LoadingScreenEnabled.Value) return;
				if (!_loading || Event.current.type != EventType.Repaint) return;
				float elapsed = Time.realtimeSinceStartup - _loadingSince;
				if (elapsed < GraceSeconds) return;
				DrawStarfield(elapsed);
			}
			catch (Exception e) { GUI.color = Color.white; Killiorim.Logger.LogError($"[EraLoading] draw threw: {e}"); }
		}

		private struct ForegroundStar
		{
			public float X, Y, Speed, Size, Brightness, Phase;
		}

		private static Texture2D _starfield;
		private static int _starfieldWidth, _starfieldHeight;
		private static readonly ForegroundStar[] ForegroundStars = CreateForegroundStars();
		private static GUIStyle _titleStyle, _captionStyle;

		private static ForegroundStar[] CreateForegroundStars()
		{
			var random = new System.Random(20171017);
			var stars = new ForegroundStar[54];
			for (int i = 0; i < stars.Length; i++)
			{
				stars[i] = new ForegroundStar
				{
					X = (float)random.NextDouble(),
					Y = (float)random.NextDouble(),
					Speed = 0.00015f + (float)random.NextDouble() * 0.00075f,
					Size = random.NextDouble() < 0.12 ? 2f : 1f,
					Brightness = 0.25f + (float)random.NextDouble() * 0.65f,
					Phase = (float)random.NextDouble() * Mathf.PI * 2f,
				};
			}
			return stars;
		}

		private static void EnsureStarfield()
		{
			int width = 1024;
			int height = Mathf.Clamp(Mathf.RoundToInt(width * Screen.height / (float)Mathf.Max(1, Screen.width)), 256, 1200);
			if (_starfield != null && width == _starfieldWidth && height == _starfieldHeight) return;
			if (_starfield != null) UnityEngine.Object.Destroy(_starfield);

			var pixels = new Color[width * height];
			for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.black;
			var random = new System.Random(198411);
			for (int i = 0; i < 920; i++)
			{
				int x = random.Next(width), y = random.Next(height);
				bool bright = random.NextDouble() < 0.045;
				float intensity = bright
					? 0.62f + (float)random.NextDouble() * 0.38f
					: 0.12f + (float)random.NextDouble() * 0.48f;
				float sigma = bright ? 0.85f + (float)random.NextDouble() * 0.9f : 0.28f + (float)random.NextDouble() * 0.30f;
				float tint = 0.90f + (float)random.NextDouble() * 0.10f;
				int radius = Mathf.CeilToInt(sigma * 3f);
				for (int dy = -radius; dy <= radius; dy++)
				{
					int py = y + dy;
					if (py < 0 || py >= height) continue;
					for (int dx = -radius; dx <= radius; dx++)
					{
						int px = x + dx;
						if (px < 0 || px >= width) continue;
						float d2 = dx * dx + dy * dy;
						float value = intensity * Mathf.Exp(-d2 / (2f * sigma * sigma));
						if (value < 0.012f) continue;
						int index = py * width + px;
						Color old = pixels[index];
						pixels[index] = new Color(
							Mathf.Max(old.r, value * tint),
							Mathf.Max(old.g, value * Mathf.Min(1f, tint + 0.025f)),
							Mathf.Max(old.b, value), 1f);
					}
				}
			}

			_starfield = new Texture2D(width, height, TextureFormat.RGB24, false);
			_starfield.name = "KilliorimStarfield";
			_starfield.hideFlags = HideFlags.HideAndDontSave;
			_starfield.filterMode = FilterMode.Bilinear;
			_starfield.wrapMode = TextureWrapMode.Clamp;
			_starfield.SetPixels(pixels);
			_starfield.Apply(false, true);
			_starfieldWidth = width;
			_starfieldHeight = height;
		}

		private static void DrawStarfield(float elapsed)
		{
			Color previous = GUI.color;
			GUI.color = Color.black;
			GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
			GUI.color = Color.white;
			EnsureStarfield();
			if (_starfield != null)
				GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _starfield, ScaleMode.StretchToFill, false);

			DrawForegroundStars(elapsed);
			DrawLoadingIndicator(elapsed);
			GUI.color = previous;
		}

		private static void DrawForegroundStars(float elapsed)
		{
			Texture2D pixel = Texture2D.whiteTexture;
			for (int i = 0; i < ForegroundStars.Length; i++)
			{
				ForegroundStar star = ForegroundStars[i];
				float x = Mathf.Repeat(star.X + elapsed * star.Speed, 1f) * Screen.width;
				float y = Mathf.Repeat(star.Y + elapsed * star.Speed * 0.11f, 1f) * Screen.height;
				float alpha = star.Brightness * (0.78f + 0.22f * Mathf.Sin(elapsed * 1.3f + star.Phase));
				GUI.color = new Color(0.83f, 0.90f, 1f, alpha * 0.16f);
				if (star.Size > 1f) GUI.DrawTexture(new Rect(x - 3f, y - 3f, 7f, 7f), pixel);
				GUI.color = new Color(0.91f, 0.95f, 1f, alpha);
				GUI.DrawTexture(new Rect(x, y, star.Size, star.Size), pixel);
			}
			GUI.color = Color.white;
		}

		private static void DrawLoadingIndicator(float elapsed)
		{
			float width = Mathf.Min(460f, Screen.width * 0.42f);
			float x = (Screen.width - width) * 0.5f;
			float y = Screen.height * 0.77f;
			EnsureLoadingStyles();

			GUI.color = new Color(0.78f, 0.84f, 0.92f, 0.95f);
			GUI.Label(new Rect(x, y - 62f, width, 26f), "LOADING WORLD", _titleStyle);
			GUI.color = new Color(0.52f, 0.59f, 0.68f, 0.88f);
			GUI.Label(new Rect(x, y - 34f, width, 18f), "CONNECTING TO INSTANCE", _captionStyle);

			GUI.color = new Color(0.36f, 0.43f, 0.52f, 0.45f);
			GUI.DrawTexture(new Rect(x, y, width, 2f), Texture2D.whiteTexture);
			float segment = width * 0.22f;
			float phase = Mathf.PingPong(elapsed * 0.32f, 1f);
			phase = phase * phase * (3f - 2f * phase);
			float segmentX = x + (width - segment) * phase;
			GUI.color = new Color(0.63f, 0.77f, 0.94f, 0.16f);
			GUI.DrawTexture(new Rect(segmentX - 8f, y - 3f, segment + 16f, 8f), Texture2D.whiteTexture);
			GUI.color = new Color(0.83f, 0.90f, 0.98f, 0.94f);
			GUI.DrawTexture(new Rect(segmentX, y, segment, 2f), Texture2D.whiteTexture);
			GUI.color = Color.white;
		}

		private static void EnsureLoadingStyles()
		{
			if (_titleStyle == null)
				_titleStyle = new GUIStyle(GUI.skin.label)
				{
					alignment = TextAnchor.MiddleCenter,
					fontSize = 17,
					fontStyle = FontStyle.Bold,
				};
			if (_captionStyle == null)
				_captionStyle = new GUIStyle(GUI.skin.label)
				{
					alignment = TextAnchor.MiddleCenter,
					fontSize = 10,
					fontStyle = FontStyle.Normal,
				};
		}
	}
}
