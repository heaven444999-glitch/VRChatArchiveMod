using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace VRChatArchiveMod.Core
{
	// THE ONE BUG BEHIND BOTH CRASHES.
	//
	// Every il2cpp field access is `*(T*)(objectPointer + fieldOffset)`, and Il2CppInterop gets that
	// offset from its own reimplementation of il2cpp_field_get_offset, which reads it at
	// FieldInfo+0x08 — a constant measured against some older VRChat build. On this build 0x08 holds
	// the metadata TOKEN. So every field read returns a value from address object+0x04000770-ish:
	// unmapped memory, an access violation, and .NET ends the process rather than raising something
	// catchable.
	//
	// Measured, not guessed: System.Delegate's four leading fields report tokens 0x04000770..3 while
	// FieldInfo+0x18 holds 0x10, 0x18, 0x20, 0x28 — the textbook layout of a delegate. Same story for
	// VRC.Player.field_Private_APIUser_0.
	//
	// This looked for a long time like several unrelated failures, because only `field_*` members go
	// through it. A `prop_*` member is a method call and resolves fine, so most of the mod worked and
	// the few things that touch fields directly — the delegate bridge, the local player's APIUser —
	// died in ways that each looked like their own bug.
	//
	// The slot is FOUND, not assumed, and the patch is not installed unless all four known fields
	// agree. A wrong answer here would corrupt every field access in the process, so "probably right"
	// is not good enough.
	internal static class FieldOffsetFix
	{
		private static int _slot = -1;

		// True once the slot is confirmed and the patch is live. Other code keys off this rather than
		// assuming the repair happened.
		internal static bool Verified => _slot >= 0;
		internal static int Slot => _slot;

		// System.Delegate's layout is fixed by il2cpp itself: object header, then these.
		private static readonly (string Name, uint Offset)[] Known =
		{
			("method_ptr", 0x10), ("invoke_impl", 0x18), ("m_target", 0x20), ("method", 0x28),
		};

		internal static unsafe void Install()
		{
			try
			{
				IntPtr klass = IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Delegate");
				if (klass == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[FieldOffsetFix] System.Delegate not found — cannot verify, leaving field offsets alone.");
					return;
				}

				var fields = new IntPtr[Known.Length];
				for (int i = 0; i < Known.Length; i++)
				{
					fields[i] = IL2CPP.il2cpp_class_get_field_from_name(klass, Known[i].Name);
					if (fields[i] == IntPtr.Zero || !NativeGuard.IsReadable(fields[i], 0x30))
					{
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[FieldOffsetFix] cannot read FieldInfo for " + Known[i].Name + " — leaving field offsets alone.");
						return;
					}
				}

				// The slot that gives the right answer for ALL four, and only that slot.
				int found = -1;
				for (int slot = 0; slot <= 0x28; slot += 4)
				{
					bool all = true;
					for (int i = 0; i < Known.Length && all; i++)
						all = *(uint*)((byte*)fields[i] + slot) == Known[i].Offset;
					if (!all) continue;
					if (found >= 0)
					{
						VRChatArchiveModFallback("[FieldOffsetFix] ambiguous: slots 0x" + found.ToString("X")
							+ " and 0x" + slot.ToString("X") + " both fit. Refusing to guess.");
						return;
					}
					found = slot;
				}

				if (found < 0)
				{
					VRChatArchiveModFallback("[FieldOffsetFix] no FieldInfo slot holds the expected offsets. "
						+ "The layout changed again; field access stays broken rather than made worse.");
					return;
				}

				uint stock = *(uint*)((byte*)fields[0] + 0x08);
				if (found == 0x08)
				{
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[FieldOffsetFix] field offsets already correct (slot 0x08) — nothing to patch.");
					_slot = found;
					return;
				}

				MethodInfo target = typeof(IL2CPP).GetMethod("il2cpp_field_get_offset",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
					null, new[] { typeof(IntPtr) }, null);
				if (target == null)
				{
					VRChatArchiveModFallback("[FieldOffsetFix] IL2CPP.il2cpp_field_get_offset not found.");
					return;
				}

				_slot = found;
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: new HarmonyMethod(
					typeof(FieldOffsetFix).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));

				VRChatArchiveModPlugin.Logger.LogInfo(
					"[FieldOffsetFix] field offsets repaired: read from FieldInfo+0x" + found.ToString("X")
					+ ", not +0x08 (which holds the metadata token 0x" + stock.ToString("X")
					+ "). All four System.Delegate fields agree.");
			}
			catch (Exception e)
			{
				_slot = -1;
				VRChatArchiveModPlugin.Logger.LogError("[FieldOffsetFix] install failed: " + Unwrap.Describe(e));
			}
		}

		private static void VRChatArchiveModFallback(string msg)
			=> VRChatArchiveModPlugin.Logger.LogWarning(msg);

		// Runs for every field access in the process, so it does exactly one load and nothing else.
		// The pointer comes from il2cpp itself; validating it here would cost a syscall per access.
		private static unsafe bool Prefix(IntPtr field, ref uint __result)
		{
			__result = field == IntPtr.Zero ? 0u : *(uint*)((byte*)field + _slot);
			return false;
		}
	}
}
