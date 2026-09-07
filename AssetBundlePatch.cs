using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MAKE VRCHAT'S OWN CACHE ARCHIVABLE.
	//
	// Unity can encrypt an asset bundle on its way into the download cache: you hand
	// UnityWebRequestAssetBundle.GetAssetBundle an EncryptionKey, and everything it writes under
	// Cache-WindowsPlayer is ciphertext. VRChat does exactly that, which is why the cache is full
	// of bundles nobody can open (see the cache notes in the archive project).
	//
	// This module prefixes that call and clears the key. Nothing is downloaded differently, nothing
	// extra is written: the same bundle lands in the same place, in the clear, ready to be copied.
	//
	// The important detail, and the one that is easy to get wrong: THE KEY MUST BE CLEARED ON EVERY
	// PATH. A bundle only reaches the cache the first time it is fetched, when there is no cache
	// folder for it yet — so a version that clears the key only after finding an existing folder
	// clears it exactly when it no longer matters, and never when it does.
	//
	// There is no setting for this. An archive mod whose cache is unreadable is not doing its job,
	// and a switch for it would only ever be a way to break the chain by accident.
	//
	// The prefix also honours a local file when one is dropped in, which is inert in normal play:
	// the real cache is Cache-WindowsPlayer\<name>\<hash>\__data, so the <hash> folder this looks
	// for sits at the NAME level and does not exist, and even if a name did collide the lookup only
	// counts files — a folder of subfolders comes back empty.
	public class AssetBundlePatchModule : IModule
	{
		public override string Name => "AssetBundlePatch";

		// Surfaced in diagnostics the same way the other hooks are, so a silent failure is visible.
		internal static string HookInfo = "not installed";
		internal static long Cleared;      // bundles allowed into the cache unencrypted
		internal static long Redirected;   // bundles served from a local file instead

		private static string _cacheRoot;

		public override void OnInitialize()
		{
			_cacheRoot = ResolveCacheRoot();
			Install();
		}

		// C:\Users\<user>\AppData\LocalLow\VRChat\VRChat\Cache-WindowsPlayer
		//
		// Built from LocalApplicationData rather than hardcoded, but NOT with a blind
		// Replace("Local", "LocalLow") — that rewrites every occurrence, so a user whose profile
		// path happens to contain "Local" would get a folder that does not exist. Only the trailing
		// segment is the one to change.
		private static string ResolveCacheRoot()
		{
			try
			{
				string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				string parent = Path.GetDirectoryName(local);
				string root = string.IsNullOrEmpty(parent)
					? local.Replace("Local", "LocalLow")
					: Path.Combine(parent, "LocalLow");
				return Path.Combine(root, "VRChat", "VRChat", "Cache-WindowsPlayer");
			}
			catch { return null; }
		}

		private void Install()
		{
			try
			{
				MethodInfo target = FindTarget();
				if (target == null)
				{
					HookInfo = "GetAssetBundle(string, CachedAssetBundle, uint, bool, Nullable<EncryptionKey>) not found";
					VRChatArchiveModPlugin.Logger.LogWarning("[AssetBundlePatch] " + HookInfo);
					// A VRChat update that reshapes this overload leaves nothing to go on otherwise.
					// Printing what DOES exist turns "not found" into the one line needed to fix it.
					foreach (MethodInfo m in typeof(UnityWebRequestAssetBundle)
						.GetMethods(BindingFlags.Static | BindingFlags.Public))
					{
						if (m.Name != "GetAssetBundle") continue;
						string sig = "";
						foreach (ParameterInfo p in m.GetParameters())
							sig += (sig.Length > 0 ? ", " : "") + p.ParameterType.Name;
						VRChatArchiveModPlugin.Logger.LogWarning("[AssetBundlePatch]   candidate: GetAssetBundle(" + sig + ")");
					}
					return;
				}

				var pre = new HarmonyMethod(typeof(AssetBundlePatchModule)
					.GetMethod(nameof(GetAssetBundlePrefix), BindingFlags.Static | BindingFlags.NonPublic));
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: pre);

				HookInfo = "prefix on UnityWebRequestAssetBundle.GetAssetBundle";
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[AssetBundlePatch] armed — cache root: " + (_cacheRoot ?? "<unresolved>"));

				// "Armed" only ever meant the patch was ACCEPTED, never that it runs. The game calls
				// this from il2cpp, so the prefix reaches it only if Il2CppInterop's Harmony support
				// redirected the patch to the native method; without that the install still succeeds
				// and nothing is ever intercepted, which is indistinguishable from a working hook
				// until you notice the cache is still ciphertext. So say what was actually patched,
				// and report the first real call when it happens.
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[AssetBundlePatch] target: " + target.DeclaringType?.FullName + "." + target.Name
					+ " (" + target.GetParameters().Length + " params)");
			}
			catch (Exception e)
			{
				HookInfo = "install failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogError($"[AssetBundlePatch] hook install failed: {e}");
			}
		}

		// Resolved by SHAPE, not by name or index. GetAssetBundle has six overloads and Il2Cpp
		// interop is free to reorder them between game builds; the five-parameter form ending in a
		// Nullable<EncryptionKey> is unambiguous and survives that.
		private static MethodInfo FindTarget()
		{
			foreach (MethodInfo m in typeof(UnityWebRequestAssetBundle)
				.GetMethods(BindingFlags.Static | BindingFlags.Public))
			{
				if (m.Name != "GetAssetBundle") continue;
				ParameterInfo[] p = m.GetParameters();
				if (p.Length != 5) continue;
				if (p[0].ParameterType != typeof(string)) continue;
				if (p[1].ParameterType != typeof(CachedAssetBundle)) continue;
				if (p[2].ParameterType != typeof(uint)) continue;
				if (p[3].ParameterType != typeof(bool)) continue;
				if (p[4].ParameterType != typeof(Il2CppSystem.Nullable<EncryptionKey>)) continue;
				return m;
			}
			return null;
		}

		// Runs on the game thread for every bundle request, so it stays cheap and it NEVER throws:
		// an exception here would propagate through the Il2Cpp trampoline and abandon the whole
		// call, which would look like avatars silently failing to load.
		private static string Trunc(string s, int n)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "\u2026");

		// BUNDLE GUARD counters, surfaced in the Protection page so the switch can prove it works.
		public static int Blocked;
		public static string LastBlocked = "";

		// VRChat serves its content from its own CDN. A bundle arriving from anywhere else was not
		// put there by VRChat, and a file:// uri we did not write ourselves is somebody handing the
		// loader a local path. Neither is normal traffic, so the guard refuses them.
		private static bool SourceLooksSane(string uri)
		{
			if (string.IsNullOrEmpty(uri)) return false;
			if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;   // our own cache redirect
			if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
			return uri.IndexOf("vrchat.com", StringComparison.OrdinalIgnoreCase) >= 0
			    || uri.IndexOf("vrchat.cloud", StringComparison.OrdinalIgnoreCase) >= 0
			    || uri.IndexOf("vrchat.net", StringComparison.OrdinalIgnoreCase) >= 0
			    || uri.IndexOf("amazonaws.com", StringComparison.OrdinalIgnoreCase) >= 0    // VRChat's S3 buckets
			    || uri.IndexOf("cloudfront.net", StringComparison.OrdinalIgnoreCase) >= 0;  // and its CDN
		}

		private static void GetAssetBundlePrefix(
			ref string uri,
			ref CachedAssetBundle cachedAssetBundle,
			ref uint crc,
			ref bool enableValidation,
			ref Il2CppSystem.Nullable<EncryptionKey> key)
		{
			try
			{
				// UNCONDITIONAL, AND FIRST. An empty Nullable, not null: the interop marshals this
				// parameter with Il2CppObjectBaseToPtrNotNull, which throws on a null reference.
				// HasValue = false is what "no encryption" actually looks like on the native side.
				bool wasEncrypted = key != null && key.HasValue;
				key = new Il2CppSystem.Nullable<EncryptionKey>();
				Cleared++;

				// The first call is the one that answers "does this hook run at all", so it is worth
				// a line. After that it would be one log entry per bundle, so the rest are counted
				// and reported every 50 — enough to see the cache filling, quiet enough to read.
				if (Cleared == 1)
				{
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[AssetBundlePatch] FIRST INTERCEPT — the hook is live. Bundle was "
						+ (wasEncrypted ? "ENCRYPTED, key cleared" : "already unencrypted")
						+ ". Cache from here on is written in the clear.");
				}
				else if (Cleared % 50 == 0)
				{
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[AssetBundlePatch] " + Cleared + " bundles passed through, " + Redirected + " served locally.");
				}

				// ---- BUNDLE GUARD ----------------------------------------------------------
				//
				// What this can and cannot do, honestly: at this point the bundle has not been
				// downloaded yet, so there is nothing to inspect INSIDE it. What is knowable is where
				// it comes from and whether the integrity check is on \u2014 and those are exactly the two
				// things that make a corrupted or planted bundle fail safely instead of being parsed.
				// What a bundle DOES once loaded is AntiCrashModule's job, and it already does it.
				// The guard is one vector of ANTI-CRASH, so the master switch gates it too: with the
				// master off, "off" has to mean off everywhere, not "off except for downloads".
				bool guard = true;
				try { guard = ModConfig.AntiCrashEnabled.Value && ModConfig.BundleGuardEnabled.Value; } catch { }
				if (guard)
				{
					// KEEP THE CRC CHECK. Unity verifies the download against the CRC VRChat passes;
					// with it on, a truncated or tampered bundle is rejected by the loader rather than
					// handed to the asset parser. We never turn it off, and we turn it back ON if
					// anything upstream cleared it.
					try { if (ModConfig.BundleGuardKeepValidation.Value && crc != 0) enableValidation = true; }
					catch { }

					if (!SourceLooksSane(uri))
					{
						Blocked++;
						LastBlocked = Trunc(uri, 90);
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[BundleGuard] refused a bundle from an unexpected source: " + LastBlocked);
						// Point it at nothing rather than throwing: the loader reports a failed download,
						// which is a path VRChat already handles, instead of us raising inside its stack.
						uri = "";
						return;
					}
				}

				if (_cacheRoot == null || cachedAssetBundle == null) return;

				Hash128 hash = cachedAssetBundle.hash;
				if (!hash.isValid) return;

				string folder = Path.Combine(_cacheRoot, hash.ToString());
				if (!Directory.Exists(folder)) return;

				string[] files = Directory.GetFiles(folder);
				if (files.Length == 0) return;

				// A cache entry we are about to hand the loader as truth: an empty file is a failed
				// write, and one far past any real avatar is a broken entry or a decompression bomb.
				// Either way, better to let VRChat download it again than to feed the parser garbage.
				try
				{
					if (ModConfig.AntiCrashEnabled.Value && ModConfig.BundleGuardEnabled.Value)
					{
						var fi = new FileInfo(files[0]);
						int maxMb = ModConfig.BundleGuardMaxMb.Value;
						if (fi.Length == 0 || (maxMb > 0 && fi.Length > (long)maxMb * 1024L * 1024L))
						{
							Blocked++;
							LastBlocked = "cached file " + (fi.Length / 1048576L) + " MB";
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[BundleGuard] ignoring a bad cache entry (" + LastBlocked + "), letting VRChat re-download: " + folder);
							return;
						}
					}
				}
				catch { }

				uri = "file://" + files[0];
				Redirected++;
			}
			catch { }
		}
	}
}
