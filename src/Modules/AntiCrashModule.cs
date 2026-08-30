using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Avatar anti-crash: scans loaded avatars and strips components that are in
	// genuine "crasher" territory (particle bombs, thousands of lights / audio
	// sources / cloth, PhysBone / Contact floods). Heavy-but-legit avatars are
	// left untouched — thresholds sit far above VRChat's own performance ratings.
	//
	// Trigger is a throttled poll over VRCAvatarDescriptor (a name-stable SDK3
	// type) rather than a Harmony hook on an obfuscated method that rotates every
	// game build — so it keeps working across VRChat updates with no re-mapping.
	public class AntiCrashModule : IModule
	{
		public override string Name => "AntiCrash";

		private int _frame;
		private static readonly HashSet<int> _processed = new HashSet<int>();

		// --- live stats for the menu ---
		public static int AvatarsScanned => _processed.Count;
		public static int NeutralizedTotal { get; private set; }

		public override void OnInitialize()
		{
			if (!ModConfig.AntiCrashEnabled.Value)
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] disabled by config.");
				return;
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] armed (avatar component clamping via VRCAvatarDescriptor poll).");
		}

		public override void OnUpdate()
		{
			if (!ModConfig.AntiCrashEnabled.Value) return;

			// Menu "Rescan" button: forget what we've seen so every avatar is re-checked.
			if (Menu.ConsumeRescanRequest())
			{
				_processed.Clear();
				VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] rescan requested — re-checking all loaded avatars.");
			}

			int interval = ModConfig.ScanIntervalFrames.Value;
			if (interval < 1) interval = 1;
			if (++_frame < interval) return;
			_frame = 0;

			try
			{
				// Walk the PLAYERS, not the whole loaded-object table. Resources.FindObjectsOfTypeAll
				// sweeps every loaded object (inactive ones and unloaded-scene assets included) and
				// allocates a fresh array each pass — in a busy public instance that is a multi-ms
				// native stall paid twice a second, forever, even when nothing new has spawned.
				// Every avatar we care about hangs off a player, so bound the work by player count.
				var players = VRC.SDKBase.VRCPlayerApi.AllPlayers;
				if (players == null) return;

				for (int i = 0; i < players.Count; i++)
				{
					VRCAvatarDescriptor desc = null;
					try
					{
						var api = players[i];
						if (api == null) continue;
						var go = api.gameObject;
						if (go == null) continue;
						desc = go.GetComponentInChildren<VRCAvatarDescriptor>(true);
					}
					catch { continue; }
					if (desc == null) continue;

					GameObject root;
					try { root = desc.gameObject; } catch { continue; }
					if (root == null) continue;

					int id;
					try { id = root.GetInstanceID(); } catch { continue; }
					if (!_processed.Add(id)) continue; // already handled this avatar instance

					ScanAndClamp(root);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[AntiCrash] scan pass threw: {e}");
			}
		}

		// Avatar instance ids are per-instance; carrying them across worlds would slowly grow the
		// set and (worse) let a recycled id skip a real scan.
		public override void OnSceneLoaded(int buildIndex) => _processed.Clear();

		// Scans a loaded avatar root and neutralizes crasher-tier components.
		public static void ScanAndClamp(GameObject avatarRoot)
		{
			if (avatarRoot == null || !ModConfig.AntiCrashEnabled.Value) return;

			try
			{
				int removed = 0;

				if (ModConfig.ClampParticles.Value)
					removed += ClampByCount<ParticleSystem>(avatarRoot.GetComponentsInChildren<ParticleSystem>(true), ModConfig.MaxParticleSystems.Value, "ParticleSystem");

				if (ModConfig.ClampLights.Value)
					removed += ClampByCount<Light>(avatarRoot.GetComponentsInChildren<Light>(true), ModConfig.MaxLights.Value, "Light");

				if (ModConfig.ClampAudioSources.Value)
					removed += ClampByCount<AudioSource>(avatarRoot.GetComponentsInChildren<AudioSource>(true), ModConfig.MaxAudioSources.Value, "AudioSource");

				if (ModConfig.ClampCloth.Value)
					removed += ClampByCount<Cloth>(avatarRoot.GetComponentsInChildren<Cloth>(true), ModConfig.MaxCloth.Value, "Cloth");

				if (ModConfig.ClampPhysBones.Value)
					removed += ClampByCount<VRCPhysBone>(avatarRoot.GetComponentsInChildren<VRCPhysBone>(true), ModConfig.MaxPhysBones.Value, "VRCPhysBone");

				if (ModConfig.ClampContacts.Value)
				{
					int half = ModConfig.MaxContacts.Value / 2;
					removed += ClampByCount<VRCContactReceiver>(avatarRoot.GetComponentsInChildren<VRCContactReceiver>(true), half, "VRCContactReceiver");
					removed += ClampByCount<VRCContactSender>(avatarRoot.GetComponentsInChildren<VRCContactSender>(true), half, "VRCContactSender");
				}

				if (removed > 0)
				{
					NeutralizedTotal += removed;
					VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] neutralized {removed} crasher-tier component(s) on '{SafeName(avatarRoot)}'.");
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[AntiCrash] ScanAndClamp threw: {e}");
			}
		}

		// Destroys every component past 'max'. Returns how many were destroyed.
		private static int ClampByCount<T>(Il2CppArrayBase<T> comps, int max, string label)
			where T : UnityEngine.Object
		{
			if (comps == null) return 0;
			int total = comps.Length;
			if (total <= max) return 0;

			int destroyed = 0;
			for (int i = max; i < total; i++)
			{
				try
				{
					var c = comps[i];
					if (c != null) { UnityEngine.Object.Destroy(c); destroyed++; }
				}
				catch { }
			}
			if (destroyed > 0)
				VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] {label}: {total} found, removed {destroyed} over limit {max}.");
			return destroyed;
		}

		private static string SafeName(GameObject go)
		{
			try { return go.name; } catch { return "?"; }
		}
	}
}
