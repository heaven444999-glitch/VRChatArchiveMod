using System;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using UnityEngine.Networking;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Era loading screen: while a world is loading we draw VRChat's OWN period loading
	// screen over the game's modern one, using artwork lifted from the real Steam builds.
	//
	//   2017 — the turquoise diamond, dashed ring, glow halo, waveform line and the
	//          speech-bubble logo (popup-* textures from the 1 Feb 2017 build)
	//   2018 — the flat cyan progress bar over a dark backdrop (GUI_LoadingBar from the
	//          22 Dec 2018 build)
	//
	// Only TEXTURES and audio survive the jump between engines: the 2017 screen was a Unity
	// 5.3 scene full of materials and shaders that cannot be dropped into Unity 2022. So we
	// re-draw the same layout ourselves in screen space from those textures, which also
	// means nothing is injected into the game's own loading machinery.
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
		private float _fakeProgress;
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
			VRChatArchiveModPlugin.Logger.LogInfo("[EraLoading] armed — 2017 loading screen.");
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
						VRChatArchiveModPlugin.Logger.LogInfo("[EraLoading] load exceeded the ceiling — ending (music off).");
						nowLoading = false; _suppressed = true;
					}
					else if (Time.realtimeSinceStartup - _loadingSince >= GraceSeconds && LocalPlayerInWorld())
					{
						VRChatArchiveModPlugin.Logger.LogInfo("[EraLoading] local player is in the world — ending (music off).");
						nowLoading = false; _suppressed = true;
					}
				}

				if (nowLoading && !_loading)
				{
					_loading = true; IsLoading = true; _loadingSince = Time.realtimeSinceStartup; _fakeProgress = 0f;
					// Decisive evidence: until this line appears in a report we cannot claim the
					// era screen ever engaged, only that the popup object was found.
					VRChatArchiveModPlugin.Logger.LogInfo("[EraLoading] LOADING STARTED — drawing the 2017 screen.");
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
					VRChatArchiveModPlugin.Logger.LogInfo("[EraLoading] switched off mid-load — audio restored, detection still running.");
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[EraLoading] update threw: {e.Message}"); }
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
			VRChatArchiveModPlugin.Logger.LogInfo("[EraLoading] loading ended.");
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
					if (t != null && t.gameObject.activeInHierarchy) return true;
				}
			}
			catch { }
			return false;
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
					if (t.hideFlags == HideFlags.HideAndDontSave) continue;
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
					VRChatArchiveModPlugin.Logger.LogInfo($"[EraLoading] loading popup candidates: {_loadingPopups.Count}.");
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[EraLoading] popup scan failed: {e.Message}"); }
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
					VRChatArchiveModPlugin.Logger.LogInfo($"[EraLoading] muted {_ducked.Count} of VRChat's own loading source(s).");
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
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[EraLoading] music start failed: {e.Message}"); return false; }
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
					if (res == null) { VRChatArchiveModPlugin.Logger.LogWarning("[EraLoading] embedded music missing."); return; }
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
					VRChatArchiveModPlugin.Logger.LogInfo(
						$"[EraLoading] {key} music loaded ({_clip.length:F1}s, {_clip.frequency}Hz, {_clip.channels}ch).");
					if (_loading) StartMusic();
				}
				else VRChatArchiveModPlugin.Logger.LogWarning("[EraLoading] could not decode the embedded music.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[EraLoading] music load failed: {e.Message}"); }
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
				VRChatArchiveModPlugin.Logger.LogWarning($"[EraLoading] wav decode failed: {e.Message}");
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

				// The game never tells us the real percentage, so the bar eases towards 100%
				// and never quite reaches it — the same illusion the original screens used.
				_fakeProgress = Mathf.Clamp01(1f - Mathf.Exp(-elapsed * 0.18f)) * 0.97f;

				GuiKit.Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0.02f, 0.03f, 0.05f, 0.96f));
				Draw2017();
				GUI.color = Color.white;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[EraLoading] draw threw: {e}"); }
		}

		// Both screens are laid out in the ORIGINAL canvas units read straight from the old
		// scenes, then scaled to the running resolution the way Unity's CanvasScaler did
		// (reference height 1080). That is why the proportions match the real thing instead
		// of being eyeballed:
		//
		//   2017 (level1, all at scale 0.80)      2018 (level1)
		//     InnerDashRing    (0,   0) 604x604     VRChat_LOGO  (0, 110) 350x149
		//     ProgressLine     (0, 160) 325x89      LOADING_BAR  (0,  39) 632x20
		//     ProgressLineBack (0,  93) 325x89      LowPercent   (-154,130) scale 0.80
		//     ProgressPercent  (0, -43) 200x50
		//     Low/HighPercent (∓152,130) 100x50
		//     TextVRChat       (-8, 196) 437x129 (scale 1.0)
		private const float RefHeight = 1080f;   // design resolution of the old canvases
		private static float U => Screen.height / RefHeight;

		// Canvas rect (centre-anchored, Y up) -> screen rect (Y down).
		private static Rect R(float x, float y, float w, float h, float scale = 1f)
		{
			float u = U, sw = w * scale * u, sh = h * scale * u;
			return new Rect(Screen.width * 0.5f + x * scale * u - sw * 0.5f,
			                Screen.height * 0.5f - y * scale * u - sh * 0.5f, sw, sh);
		}

		// Authentic ring stack, read straight out of both original builds' level1 scene.
		// Every ring carries a `UiSpinner` whose Update is exactly:
		//     transform.Rotate(RotationSpeed * Time.deltaTime)
		// so the motion is a constant Z spin in DEGREES PER SECOND — no easing, no clip:
		//     InnerDashRing  604 x 604   @ (0,0)        Z = 10 deg/s
		//     OuterGlowRing  855 x 854   @ (0,-157.3)   Z = 10 deg/s
		//     RingGlow       855 x 854   @ (0,0)        Z =  1 deg/s   (slow drift)
		//     MidRing        645 x 644   @ (0,0)        NO spinner — static
		// Unity's Rotate(+Z) turns counter-clockwise; IMGUI's Y axis points down, so the
		// screen-space angle is negated to match what the old clients actually showed.
		private const float SpinFast = 10f;    // InnerDashRing + OuterGlowRing
		private const float SpinSlow = 1f;     // RingGlow

		private void DrawRings(float t, float S)
		{
			// Exactly the three rings the real LoadingPopup carries, in its own child order.
			// (OuterGlowRing belongs to a DIFFERENT popup — it is not part of this screen.)
			Spin("l17_ringglow", R(0f, 0f, 855f, 854f, S), new Color(0.15f, 0.85f, 0.85f, 0.40f), -t * SpinSlow);
			Tex ("l17_midring",  R(0f, 0f, 645f, 644f, S), new Color(1f, 1f, 1f, 0.30f));
			Spin("l17_dashring", R(0f, 0f, 604f, 604f, S), new Color(0.18f, 0.85f, 0.87f, 1f), -t * SpinFast);
		}

		// 2017 — the real LoadingPopup child list, with each element's own RectTransform:
		//     Rectangle        788 x 788   (0, 0)      backdrop
		//     MidRing          645 x 644   (0, 0)      static
		//     InnerDashRing    604 x 604   (0, 0)      UiSpinner Z = 10 deg/s
		//     RingGlow         855 x 854   (0, 0)      UiSpinner Z =  1 deg/s
		//     ProgressLineBack 325 x 89    (0, 93)     the wave line, dim
		//     ProgressLine     325 x 89    (0, 93)     the SAME wave, filled by progress
		//     LowPercent       100 x 50    (-152, 63)  "0%"
		//     HighPercent      100 x 50    ( 152, 63)  "100%"
		//     ProgressPercent  199.8 x 50  (0, -43)    the live percentage
		//     TitleText        526 x 92.8  (0, 8)
		// Everything except Rectangle sits at the popup's own 0.8 scale.
		private void Draw2017()
		{
			const float S = 0.80f;
			float t = Time.realtimeSinceStartup;

			Tex("l17_diamond", R(0f, 0f, 788f, 788f), new Color(0.09f, 0.58f, 0.55f, 0.55f));
			DrawRings(t, S);

			// The wave line is ONE sprite drawn twice at the SAME spot: a dim full-width base,
			// then the bright copy clipped left-to-right by the progress — that clip IS the
			// animation (both rects are at y=93 in the scene; they are not stacked).
			// Colours taken from the real Image components in level1 (both are plain
			// "Simple" images stacked on the SAME rect): the dark teal wave is the track and
			// the WHITE wave is revealed across it as the load progresses.
			//   ProgressLineBack  color = (0.048, 0.343, 0.382, 1)
			//   ProgressLine      color = (1, 1, 1, 1)
			Rect back = R(0f, 93f, 325f, 89f, S);
			Tex("l17_wave", back, new Color(0.048f, 0.343f, 0.382f, 1f));
			DrawClipped("l17_wave", back, Color.white, _fakeProgress);

			var logo = AssetLoader.EraTexture("logo");
			if (logo != null)
			{
				GUI.color = Color.white;
				GUI.DrawTexture(R(0f, 196f, 437f, 129f), logo, ScaleMode.ScaleToFit, true);
			}

			Label(R(-152f, 63f, 100f, 50f, S), "0%", 12, new Color(0.6f, 0.9f, 0.95f));
			Label(R( 152f, 63f, 100f, 50f, S), "100%", 12, new Color(0.6f, 0.9f, 0.95f));
			Label(R(0f, 8f, 526f, 92.8f, S), "LOADING", 30, new Color(0.20f, 0.83f, 0.85f));
			Label(R(0f, -43f, 199.8f, 50f, S), (_fakeProgress * 100f).ToString("F2") + "%", 22, Color.white);
		}

		// 2018 — the REAL LoadingPopup, read from the 22 Dec 2018 build's level1:
		//     3DElements/LoadingBackground_TealGradient   a teal 3D scene (approximated here)
		//     ProgressPanel/Parent_Loading_Progress
		//       Panel_Backdrop     1040 x 176.6  (0, 0)
		//       Decoration_Left      60 x 60     (-470, -26)
		//       Decoration_Right     60 x 60     ( 470, -26)  mirrored (scale -1)
		//       Loading Elements
		//         txt_LOADING      425.5 x 81.9  (-390, -20)
		//         txt_Percent      375.5 x 82    ( 143, -15)
		//         LOADING_BAR_BG     632 x 19.5  (0, 39.3)
		//         LOADING_BAR        632 x 19.5  (0, 39.3)
		// It carries NO rings and NO logo: the spinning rings found elsewhere in level1
		// belong to a different popup, and drawing them here was simply wrong.


		// Draws the left `fill` fraction of a texture, clipping instead of squashing it —
		// this is how the ProgressLine grows over ProgressLineBack in the original scene.
		private static void DrawClipped(string key, Rect r, Color tint, float fill)
		{
			var tex = AssetLoader.EraTexture(key);
			if (tex == null) return;
			fill = Mathf.Clamp01(fill);
			if (fill <= 0f) return;
			GUI.color = tint;
			GUI.BeginGroup(new Rect(r.x, r.y, r.width * fill, r.height));
			GUI.DrawTexture(new Rect(0f, 0f, r.width, r.height), tex, ScaleMode.StretchToFill, true);
			GUI.EndGroup();
			GUI.color = Color.white;
		}

		// One spinning ring: rotates about the RECT'S OWN CENTRE, like the scene's UiSpinner
		// (the old code pivoted on screen centre, which made off-centre rings orbit instead).
		private static void Spin(string key, Rect r, Color tint, float angle)
		{
			var tex = AssetLoader.EraTexture(key);
			if (tex == null) return;
			Matrix4x4 old = GUI.matrix;
			GUIUtility.RotateAroundPivot(angle % 360f, new Vector2(r.center.x, r.center.y));
			GUI.color = tint;
			GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
			GUI.color = Color.white;
			GUI.matrix = old;
		}

		private static void Tex(string key, Rect r, Color tint)
		{
			var t = AssetLoader.EraTexture(key);
			if (t == null) return;
			GUI.color = tint;
			GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true);
			GUI.color = Color.white;
		}

		private static GUIStyle _style;
		private static void Label(Rect r, string text, int size, Color col)
		{
			if (_style == null) _style = new GUIStyle { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = true };
			_style.fontSize = size;
			_style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
			GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, _style);
			_style.normal.textColor = col;
			GUI.Label(r, text, _style);
		}
	}
}
