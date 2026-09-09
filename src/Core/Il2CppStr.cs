using System;
using Il2CppInterop.Runtime;

namespace VRChatArchiveMod.Core
{
	// READING AN IL2CPP STRING WITHOUT BETTING THE PROCESS ON IT.
	//
	// THE CRASH THIS EXISTS FOR (2026-09-08, BepInEx/ErrorLog.log):
	//
	//     Fatal error. Internal CLR error. (0x80131506)
	//        at System.Buffer.__Memmove(Byte*, Byte*, UIntPtr)
	//        at System.String.Ctor(Char*, Int32, Int32)
	//        at Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(IntPtr)
	//        at VRC.SDKBase.VRCPlayerApi.get_displayName()
	//        at VRChatArchiveMod.Modules.SpoofModule.OnUpdate()
	//
	// Il2CppStringToManaged does no validation at all: it takes the pointer, reads the length out of
	// it, and hands both to String.Ctor, which memmoves. Feed it a stale pointer and the fault lands
	// inside memmove — not an exception, not something a try/catch can see, but a CLR fatal that ends
	// VRChat on the spot.
	//
	// AND THE GUARD THAT WAS THERE COULD NOT HAVE CAUGHT IT. SpoofModule did check
	// NativeGuard.Alive(api) first, and that check passed: the VRCPlayerObject itself was fine. What
	// was rotten was the POINTER STORED IN ITS FIELD — a string that had been collected or a field
	// belonging to an object whose memory had been reused. Alive() validates the object you hand it,
	// and nothing about what its fields point at, so the crash walked straight past it. Every
	// il2cpp string read off a long-lived object has this hole; this closes it.
	//
	// THE LAYOUT, which is fixed by il2cpp itself and not by any VRChat build:
	//     +0x00  Il2CppClass*      the type
	//     +0x08  MonitorData*      lock word
	//     +0x10  int32             length, in CHARACTERS
	//     +0x14  char[]            UTF-16, not NUL-terminated
	//
	// So the read is: prove the header is mapped, take the length, refuse a length that cannot be a
	// real one, prove the characters are mapped too, and only then convert. Four VirtualQuery calls
	// in the worst case — which is why callers should read a string when something changed, not
	// every frame (Core/NativeGuard documents what those syscalls cost).
	internal static class Il2CppStr
	{
		private const int LenOffset = 0x10;
		private const int CharsOffset = 0x14;

		// No VRChat string this code reads — a display name, a chatbox line, a world name — is
		// anywhere near this. The cap is not a correctness rule, it is a refusal to allocate
		// hundreds of megabytes because four bytes of freed memory happened to say so.
		private const int MaxChars = 8192;

		/// <summary>How many reads were refused because the pointer did not survive validation.
		/// Non-zero means this file earned its keep; it is logged rather than hidden.</summary>
		internal static int Rejected { get; private set; }

		/// <summary>Reads an il2cpp string safely. Returns false — never throws, never faults — when
		/// the pointer is null, unmapped, or does not look like a string.</summary>
		internal static unsafe bool TryRead(IntPtr strPtr, out string value)
		{
			value = null;
			try
			{
				if (strPtr == IntPtr.Zero) { value = ""; return true; }   // a null field is an empty name

				// The header: class pointer, monitor, length. IsReadable also insists on 8-alignment,
				// which is what rejects a stale value that happens to land on a committed page.
				if (!NativeGuard.IsReadable(strPtr, CharsOffset)) { Rejected++; return false; }

				// It must actually be an object: the first eight bytes are a pointer to its class,
				// and that has to be mapped too.
				if (!NativeGuard.IsLiveObject(strPtr)) { Rejected++; return false; }

				int len = *(int*)((byte*)strPtr + LenOffset);
				if (len < 0 || len > MaxChars) { Rejected++; return false; }
				if (len == 0) { value = ""; return true; }

				// The characters themselves. Not 8-aligned (they start at +0x14), so alignment is
				// off for this one — the same exception Core/NativeGuard documents for C strings.
				if (!NativeGuard.IsReadable(strPtr + CharsOffset, len * 2, false)) { Rejected++; return false; }

				value = new string((char*)((byte*)strPtr + CharsOffset), 0, len);
				return true;
			}
			catch { Rejected++; return false; }
		}

		/// <summary>The reference-field read the generated il2cpp properties do, minus the part that
		/// can kill the process: returns the raw pointer stored in a field, so the caller can hand it
		/// to TryRead instead of to a getter that converts blind.
		///
		/// The offset comes from il2cpp_field_get_offset, which is the same source the generated
		/// setter uses — and which Core/FieldOffsetFix has to have repaired first, or it answers with
		/// a metadata token and the read lands outside the object. Callers gate on
		/// FieldOffsetFix.Verified.</summary>
		internal static unsafe bool TryFieldPtr(IntPtr obj, IntPtr fieldInfo, out IntPtr value)
		{
			value = IntPtr.Zero;
			try
			{
				if (obj == IntPtr.Zero || fieldInfo == IntPtr.Zero) return false;
				if (!NativeGuard.IsLiveObject(obj)) return false;
				int off = (int)IL2CPP.il2cpp_field_get_offset(fieldInfo);
				if (off <= 0 || off > 0x10000) return false;          // a token, not an offset
				if (!NativeGuard.IsReadable(obj + off, IntPtr.Size)) return false;
				value = *(IntPtr*)((byte*)obj + off);
				return true;
			}
			catch { return false; }
		}

		/// <summary>Resolves a field by name on an object's own class. Cached by the caller: this
		/// walks il2cpp metadata and is not something to do per frame.</summary>
		internal static IntPtr FindField(IntPtr obj, string name)
		{
			try
			{
				if (obj == IntPtr.Zero || !NativeGuard.IsLiveObject(obj)) return IntPtr.Zero;
				IntPtr klass = IL2CPP.il2cpp_object_get_class(obj);
				if (klass == IntPtr.Zero) return IntPtr.Zero;
				return IL2CPP.il2cpp_class_get_field_from_name(klass, name);
			}
			catch { return IntPtr.Zero; }
		}
	}
}
