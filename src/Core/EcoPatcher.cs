using System;
using System.Reflection;
using HarmonyLib;
using VRC.Economy.Internal;

namespace Killiorim.Core
{
	// STORE OWNERSHIP, LOCALLY — so the owner can walk their own published worlds' paid content
	// without buying their own products back to test a gate.
	//
	// WHAT IT DOES. VRC.Economy.Internal.Store carries two private methods that answer "does this
	// player have this product": Method_Private_Boolean_VRCPlayerApi_IProduct_PDM_0 and
	// Method_Private_Boolean_IProduct_PDM_0, both confirmed present in this build's Assembly-CSharp.
	// A prefix returns true and skips the original, so whatever the CLIENT gates on that answer opens
	// up on this machine.
	//
	// WHAT IT CANNOT DO, AND THAT IS THE HONEST HALF. This is a client-side read. Anything VRChat
	// validates on its own servers — a real purchase, an entitlement that has to round-trip — is
	// untouched and behaves exactly as before. It makes the UI act as though a product is owned; it
	// does not obtain the product.
	//
	// ON BY DEFAULT at the owner's request. The switch is read INSIDE the prefix rather than at
	// patch time, so turning it off restores the game's own answer immediately rather than at the
	// next restart — which is what makes a default of ON reasonable to live with.
	//
	// Members are resolved reflectively and null-checked. Every name here is obfuscated on this build
	// and re-obfuscated on every VRChat update, so a member that moved has to produce a clean
	// "unavailable" line in the log — an exception during Load() would take the whole mod down with
	// it, and this runs before anything else.
	internal static class EcoPatcher
	{
		internal static string Status = "not armed";
		internal static bool Armed { get; private set; }

		internal static void Patch()
		{
			if (Armed) return;
			try
			{
				var prefix = typeof(EcoPatcher).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.Public);
				if (prefix == null) { Status = "prefix method missing"; return; }

				const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
				int patched = 0;

				foreach (string name in new[]
				{
					"Method_Private_Boolean_VRCPlayerApi_IProduct_PDM_0",
					"Method_Private_Boolean_IProduct_PDM_0",
				})
				{
					MethodInfo m = null;
					try { m = typeof(Store).GetMethod(name, Any); }
					catch { }
					if (m == null) continue;
					try
					{
						Killiorim.HarmonyInstance.Patch(m, prefix: new HarmonyMethod(prefix));
						patched++;
					}
					catch (Exception e)
					{
						Killiorim.Logger.LogWarning("[Eco] could not patch " + name + ": " + e.Message);
					}
				}

				if (patched == 0)
				{
					Status = "neither ownership method resolved on this build";
					Killiorim.Logger.LogWarning("[Eco] " + Status + " — store testing unavailable.");
					return;
				}

				Armed = true;
				Status = "armed (" + patched + " method(s))";
				Killiorim.Logger.LogInfo(
					"[Eco] armed on " + patched + " ownership check(s). Client-side only — what VRChat "
					+ "validates server-side is unaffected. Inactive until Spoof/StoreOwnership is on.");
			}
			catch (Exception e)
			{
				Status = "could not patch: " + e.Message;
				Killiorim.Logger.LogWarning("[Eco] " + Status);
			}
		}

		// Runs on every ownership check the client makes, so it does one config read and nothing else.
		public static bool Prefix(ref bool __result)
		{
			try
			{
				if (ModConfig.StoreOwnership == null || !ModConfig.StoreOwnership.Value) return true;
				__result = true;
				return false;   // skip the original
			}
			catch { return true; }
		}
	}
}
