using System;
using UnityEngine;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// SPAWN STINGER — plays "The Spawn Dark Squad" once each time you finish loading into an
	// instance. Strictly a local, one-shot 2D AudioSource — nothing is sent anywhere and no game
	// object is touched. Toggle + volume live in Settings.
	//
	// The spawn moment is the RISING edge of the local player existing (VRC.Player.prop_Player_0
	// goes null → non-null on every instance join). It deliberately does NOT ride on
	// EraLoadingModule.IsLoading any more: that module returns early when the 2017 loading screen is
	// switched off, so IsLoading stayed false forever and the falling edge this used to wait for
	// never arrived — the stinger silently stopped working while its own toggle still read ON.
	// One feature's toggle must not decide whether another feature runs.
	public class SpawnSoundModule : IModule
	{
		public override string Name => "SpawnSound";

		private static AudioSource _src;
		private static AudioClip _clip;
		private static bool _clipRequested;

		// Spawn state. AwaySeconds exists because the local player reference can blink null for a
		// frame or two mid-session (avatar swap, respawn); without it every blink fired the stinger.
		private bool _inWorld;
		private float _goneSince = -1f;
		private const float AwaySeconds = 2f;

		public override void OnInitialize()
		{
			RequestClip();
			Killiorim.Logger.LogInfo("[SpawnSound] armed.");
		}

		public override void OnUpdate()
		{
			try
			{
				bool present = false;
				try { present = PlayerRef.LocalPlayer() != null; } catch { }
				float now = Time.realtimeSinceStartup;

				if (!present)
				{
					if (_goneSince < 0f) _goneSince = now;          // start counting the absence
					_inWorld = false;
					return;
				}

				if (!_inWorld)
				{
					// Present again. Only a LONG absence means a real instance load; anything
					// shorter is a blink and must not re-fire the stinger.
					bool realSpawn = _goneSince < 0f || (now - _goneSince) >= AwaySeconds;
					_inWorld = true;
					_goneSince = -1f;
					// A SIGNATURE WINS OVER THE STINGER. Somebody who has their own arrival clip would
					// otherwise hear both at once on their own spawn, one on top of the other.
					bool mine = false;
					try { mine = SignatureSoundModule.HasSignature(VaTagsModule.LocalUserId()); } catch { }
					if (realSpawn && !mine && ModConfig.SpawnSoundEnabled.Value) Play();
				}
			}
			catch { }
		}

		// A world change tears the local player down; clearing the timestamp here means the next
		// appearance counts as a spawn even if the gap was short.
		public override void OnSceneLoaded(int buildIndex)
		{
			_inWorld = false;
			_goneSince = 0f;      // 0 is "long ago" against realtimeSinceStartup, so the next
			                      // appearance is treated as a genuine spawn.
		}

		private static void Play()
		{
			try
			{
				if (_clip == null) { RequestClip(); return; }               // not decoded yet: skip this spawn
				if (_src == null)
				{
					var go = new GameObject("ArchiveSpawnSound");
					UnityEngine.Object.DontDestroyOnLoad(go);
					go.hideFlags = HideFlags.HideAndDontSave;
					_src = go.AddComponent<AudioSource>();
					_src.spatialBlend = 0f;              // 2D, plays in your head regardless of position
					_src.loop = false;
					_src.playOnAwake = false;
					_src.bypassEffects = true;
					_src.bypassListenerEffects = true;
					_src.ignoreListenerPause = true;
					_src.clip = _clip;
				}
				_src.volume = Mathf.Clamp01(ModConfig.SpawnSoundVolume.Value);
				_src.Stop();
				_src.Play();
				Killiorim.Logger.LogInfo("[SpawnSound] spawn stinger played.");
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[SpawnSound] play failed: {e.Message}"); }
		}

		// Preview from the Settings "Test" button, without having to rejoin an instance.
		public static void PlayNow() => Play();

		// Cut it off immediately when the user flips the toggle off from the menu.
		public static void StopNow() { try { if (_src != null) _src.Stop(); } catch { } }

		public static bool IsPlaying { get { try { return _src != null && _src.isPlaying; } catch { return false; } } }

		private static void RequestClip()
		{
			if (_clip != null || _clipRequested) return;
			_clipRequested = true;
			try
			{
				using (var res = typeof(SpawnSoundModule).Assembly.GetManifestResourceStream("spawn_darksquad.wav"))
				{
					if (res == null) { Killiorim.Logger.LogWarning("[SpawnSound] embedded wav missing."); return; }
					var wav = new byte[res.Length];
					int off = 0, n;
					while (off < wav.Length && (n = res.Read(wav, off, wav.Length - off)) > 0) off += n;
					_clip = WavAudio.Decode(wav, "ArchiveSpawnSound");
					Killiorim.Logger.LogInfo(_clip != null ? "[SpawnSound] clip decoded." : "[SpawnSound] clip decode failed.");
				}
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[SpawnSound] clip load failed: {e.Message}"); }
		}
	}
}
