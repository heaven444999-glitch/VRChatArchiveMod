using System;
using HarmonyLib;
using VRC.Core;

namespace Killiorim.Core
{
	// VRC+ STATUS, LOCALLY. The client's own "am I a subscriber" flag reads true, so the features it
	// gates in the UI open up on this machine.
	//
	// WHAT IT TOUCHES, AND WHY THAT IS THE SAME LINE THE MOD ALREADY DREW. VRCPlusStatus exposes a
	// ReactiveProperty<bool> that the menu binds to; a postfix on its getter sets the property's own
	// backing value. That is a CLIENT-SIDE READ. It is not APIUser.isSupporter, which this project
	// deliberately refuses to touch — those are ApiModel fields carrying Save()/Put()/PostOrPut(),
	// so a forged value there can travel back to VRChat under the account. Nothing here is
	// serialised anywhere.
	//
	// The mod already unlocks VRC+ menu backgrounds locally (Modules/VrcPlusBackgroundsModule, which
	// sets VRC.BackgroundOption._isVRCPlus to false). This is the same category, one level up.
	//
	// PATCHED ONCE, AND ONLY IF THE PROPERTY IS REALLY THERE. Every name here is obfuscated on this
	// build and re-obfuscated on every VRChat update, so a missing member has to be a clean
	// "unavailable" in the log rather than an exception during startup — the plugin's Load() runs
	// before anything else and a throw there takes the whole mod with it.
	internal static class VRCPlusSpoof
	{
		internal static string Status = "not armed";
		internal static bool Armed { get; private set; }

		internal static void Patch()
		{
			if (Armed) return;
			try
			{
				var prop = typeof(VRCPlusStatus).GetProperty(nameof(VRCPlusStatus.prop_ReactiveProperty_1_Boolean_0));
				var getter = prop?.GetGetMethod();
				if (getter == null)
				{
					Status = "VRCPlusStatus.prop_ReactiveProperty_1_Boolean_0 has no getter on this build";
					Killiorim.Logger.LogWarning("[VRCPlus] " + Status);
					return;
				}

				Killiorim.HarmonyInstance.Patch(
					getter,
					postfix: new HarmonyMethod(typeof(VRCPlusSpoof), nameof(PfStatus)));

				Armed = true;
				Status = "armed";
				Killiorim.Logger.LogInfo(
					"[VRCPlus] armed — the client's own VRC+ flag reads true. Local only: this is the UI's "
					+ "reactive property, never APIUser, so nothing is sent to VRChat.");
			}
			catch (Exception e)
			{
				Status = "could not patch: " + e.Message;
				Killiorim.Logger.LogWarning("[VRCPlus] " + Status);
			}
		}

		// The postfix runs on EVERY read of that property, so it does the least possible: one config
		// read and one field write. No allocation, no logging, no lookup.
		internal static void PfStatus(ref ReactiveProperty<bool> __result)
		{
			try
			{
				if (__result == null) return;
				if (ModConfig.VrcPlusSpoof == null || !ModConfig.VrcPlusSpoof.Value) return;
				__result.field_Protected_T_0 = true;
			}
			catch { }
		}
	}
}
