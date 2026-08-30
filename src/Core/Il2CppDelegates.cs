using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace VRChatArchiveMod.Core
{
	// ONE GATE IN FRONT OF A CALL THAT KILLS THE GAME.
	//
	// Il2CppInterop.DelegateSupport.ConvertDelegate wraps a managed delegate as the il2cpp delegate a
	// game method expects. On this VRChat build it dies inside Il2CppSystem.Delegate.set_method_ptr
	// with an access violation, which .NET does not let anyone catch — the process is simply gone. It
	// is not a hook failing; it is the game not starting.
	//
	// Six features call it, so a try/catch at each of them would be six pieces of code that cannot
	// work. Instead they all ask here first, and the answer is a setting rather than an attempt: you
	// cannot probe this safely, because probing IS the crash.
	//
	// The default is off. Set [Compatibility] AllowIl2CppDelegates = true to turn the features back
	// on once ConvertDelegate is understood; the features themselves are fine, the bridge under them
	// is not. What is lost while it is off: full game log capture, the diagnostics report's Unity
	// error feed, video-URL detection in the instance log, and avatar-card click wiring.
	internal static class Il2CppDelegates
	{
		private static bool _explained;

		internal static bool Available
		{
			get
			{
				// The gate existed because a field write at the wrong offset killed the game. Once the
				// offset is measured and repaired, the reason is gone -- so the repair opens it, rather
				// than leaving four features off behind a setting nobody would think to flip.
				try { if (FieldOffsetFix.Verified) return true; } catch { }
				try { return ModConfig.AllowIl2CppDelegates != null && ModConfig.AllowIl2CppDelegates.Value; }
				catch { return false; }
			}
		}

		// Returns null when the bridge is off, so a caller that checks for null degrades to "this one
		// feature is missing" instead of taking everything down with it.
		internal static T TryConvert<T>(Delegate managed, string who) where T : Il2CppObjectBase
		{
			if (managed == null) return null;
			if (!Available) { Explain(who); return null; }
			try { return DelegateSupport.ConvertDelegate<T>(managed); }
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[" + who + "] il2cpp delegate conversion failed: " + Unwrap.Describe(e));
				return null;
			}
		}

		private static void Explain(string who)
		{
			if (_explained)
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[" + who + "] off: il2cpp delegates disabled.");
				return;
			}
			_explained = true;
			VRChatArchiveModPlugin.Logger.LogWarning(
				"[" + who + "] off: Il2CppInterop's ConvertDelegate crashes this VRChat build (access "
				+ "violation in Il2CppSystem.Delegate.set_method_ptr), so features built on it are "
				+ "held back. Re-enable with [Compatibility] AllowIl2CppDelegates = true.");
		}
	}
}
