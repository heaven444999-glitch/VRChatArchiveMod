using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Killiorim.Core;

namespace Killiorim.Core
{
	// VRC+ COSMETICS — loading screens, props, emojis and the rest of that family.
	//
	// WHY THE BACKGROUNDS TRICK DOES NOT COVER THESE. VrcPlusBackgroundsModule works because a menu
	// background is a VRC.BackgroundOption: a LOCAL ScriptableObject carrying a plain bool
	// _isVRCPlus that means "this one needs VRC+". Flip it and the option is free. Loading screens
	// have no such object — ApiLoadingScreen is an ApiModel with exactly two fields, name and
	// assetBundleId, and no subscriber flag anywhere on it. The gate is one layer up.
	//
	// WHERE THE GATE ACTUALLY IS. DataModel declares an item view-model interface — the thing a
	// cosmetic tile binds to — carrying an abstract bool IsVRCPlus alongside IsPremium, IsBuiltIn,
	// OwnerCanUseAnimatedEmoji and LinearLoop. That property is what draws the yellow VRC+ badge and
	// what refuses the action: the owner's own screenshots show the Trampoline's placement ghost
	// rendering correctly and only the DROP being rejected, which is a gate saying no to an asset
	// that is already loaded. The whole family shares it — DataModel lists
	// SubscriberExclusiveUnlockLoadingScreen, ...UnlockBackground, ...UnlockEmoji, ...UnlockIconFrame,
	// ...UnlockNameplateEffect, ...UnlockPortalSkin, ...UnlockProfileEffect, ...UnlockDroneSkin and
	// ...UnlockActionMenuItem — so one patch covers all of them.
	//
	// RESOLVED AT RUNTIME, NEVER BY A HARDCODED CLASS NAME. Every type here is obfuscated and
	// re-obfuscated on every VRChat update; the interface name in this file is itself a rename-map
	// artifact and will change. So the implementors are found by asking the type system who
	// implements the interface, and the getter is found by NAME ON THAT TYPE. When a future update
	// moves things, this logs "no implementor found" and does nothing — which is the failure mode
	// this project wants, rather than patching whatever happens to sit at an old address.
	//
	// LOCAL ONLY, and worth being precise about: this changes what the CLIENT believes about an item
	// it has already downloaded. Nothing is sent to VRChat, no ApiModel is written (the project's
	// standing rule, because those carry Save()/Put() and can travel back under the account), and
	// anything VRChat validates server-side is untouched.
	internal static class VrcPlusItems
	{
		internal static string Status = "not armed";
		internal static bool Armed { get; private set; }
		internal static int PatchedCount { get; private set; }

		// The interface DataModel declares the abstract IsVRCPlus on. A rename-map name: it is tried
		// first, and the fallback below does not depend on it.
		private const string IfaceName = "InterfacePublicAbstractStLoSpBoObgeMaStgeLoUnique";
		private const string GetterName = "get_IsVRCPlus";

		internal static void Patch()
		{
			if (Armed) return;
			try
			{
				var prefix = typeof(VrcPlusItems).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.Public);
				if (prefix == null) { Status = "prefix method missing"; return; }

				var targets = new List<MethodInfo>();
				var seen = new HashSet<string>(StringComparer.Ordinal);

				// EVERY loaded type that declares a parameterless bool get_IsVRCPlus. Walking the
				// AppDomain is expensive, so this runs ONCE at startup and never again.
				foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					Type[] types;
					try { types = asm.GetTypes(); }
					catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
					catch { continue; }
					if (types == null) continue;

					for (int i = 0; i < types.Length; i++)
					{
						Type t = types[i];
						if (t == null || t.IsInterface || t.IsAbstract) continue;
						MethodInfo g;
						try { g = t.GetMethod(GetterName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null); }
						catch { continue; }
						if (g == null || g.ReturnType != typeof(bool)) continue;
						// Declared here, not inherited: patching a base twice does nothing useful and
						// Harmony would complain.
						if (g.DeclaringType != t) continue;
						string key = t.FullName + "::" + g.Name;
						if (!seen.Add(key)) continue;
						targets.Add(g);
					}
				}

				if (targets.Count == 0)
				{
					Status = "no type declaring " + GetterName + " found on this build";
					Killiorim.Logger.LogWarning(
						"[VRCPlusItems] " + Status + " — VRChat has probably moved it. Nothing patched, "
						+ "which is deliberate: a wrong target here is worse than no feature.");
					return;
				}

				int ok = 0;
				foreach (MethodInfo m in targets)
				{
					try { Killiorim.HarmonyInstance.Patch(m, prefix: new HarmonyMethod(prefix)); ok++; }
					catch (Exception e)
					{
						Killiorim.Logger.LogWarning(
							"[VRCPlusItems] could not patch " + (m.DeclaringType != null ? m.DeclaringType.Name : "?") + ": " + e.Message);
					}
				}

				PatchedCount = ok;
				Armed = ok > 0;
				Status = Armed ? ("armed on " + ok + " type(s)") : "every patch attempt failed";
				if (Armed)
				{
					var names = new List<string>();
					for (int i = 0; i < targets.Count && i < 6; i++)
						if (targets[i].DeclaringType != null) names.Add(targets[i].DeclaringType.Name);
					Killiorim.Logger.LogInfo(
						"[VRCPlusItems] armed on " + ok + " " + GetterName + " implementation(s): "
						+ string.Join(", ", names) + (targets.Count > 6 ? ", …" : "")
						+ ". Client-side only — nothing is sent to VRChat.");
				}
				else Killiorim.Logger.LogWarning("[VRCPlusItems] " + Status);
			}
			catch (Exception e)
			{
				Status = "could not patch: " + e.Message;
				Killiorim.Logger.LogWarning("[VRCPlusItems] " + Status);
			}
		}

		// FALSE, not true — the same inversion the backgrounds module documents. IsVRCPlus means
		// "this item REQUIRES VRC+"; answering false is what makes it free. Answering true would
		// lock everything, including what the account already owns.
		//
		// Shares the Spoof/VRCPlus switch, read live so turning it off restores the game's own
		// answer at once rather than at the next restart.
		public static bool Prefix(ref bool __result)
		{
			try
			{
				if (ModConfig.VrcPlusSpoof == null || !ModConfig.VrcPlusSpoof.Value) return true;
				__result = false;
				return false;   // skip the original
			}
			catch { return true; }
		}
	}
}
