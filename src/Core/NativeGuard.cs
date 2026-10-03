using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes;

namespace Killiorim.Core
{
	// AN ACCESS VIOLATION IS NOT AN EXCEPTION YOU CAN CATCH.
	//
	// Every il2cpp field read is, underneath, `*(void**)(objectPointer + fieldOffset)`. Hand it a
	// proxy whose native object was never valid and the read lands on unmapped memory: .NET 6 does
	// not surface that as a catchable exception, it prints "Fatal error. AccessViolationException"
	// and ends the process. So the try/catch around our reflection kit is powerless against it —
	// the only defence is to not do the read.
	//
	// VRChat hands out exactly such a proxy at startup. `VRC.Player.prop_Player_0` is readable from
	// the first frame, long before the local player is spawned, and what comes back then is not a
	// live object. VaTags asks for the local user id as soon as the tag database is loaded, which is
	// immediately, so it read that proxy and took the game down.
	//
	// This asks Windows whether the memory is actually there before anyone touches it. VirtualQuery
	// never faults, whatever it is given.
	internal static class NativeGuard
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct MemoryBasicInformation
		{
			public IntPtr BaseAddress;
			public IntPtr AllocationBase;
			public uint AllocationProtect;
			public IntPtr RegionSize;
			public uint State;
			public uint Protect;
			public uint Type;
		}

		[DllImport("kernel32.dll")]
		private static extern IntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, IntPtr length);

		private const uint MemCommit = 0x1000;
		private const uint PageGuard = 0x100;

		// Whitelist rather than "not PAGE_NOACCESS": the protection constants are distinct values,
		// not bit flags, so masking for one of them quietly matches the wrong pages.
		private static bool ProtectionAllowsRead(uint protect)
		{
			switch (protect & 0xFF & ~(PageGuard | 0x200u /*NOCACHE*/ | 0x400u /*WRITECOMBINE*/))
			{
				case 0x02: // PAGE_READONLY
				case 0x04: // PAGE_READWRITE
				case 0x08: // PAGE_WRITECOPY
				case 0x20: // PAGE_EXECUTE_READ
				case 0x40: // PAGE_EXECUTE_READWRITE
				case 0x80: // PAGE_EXECUTE_WRITECOPY
					return true;
				default:
					return false;   // PAGE_NOACCESS, PAGE_EXECUTE, anything unexpected
			}
		}

		// requireAligned is for OBJECT pointers, which are always 8-aligned — the check is what
		// rejects a stale value that happens to land on a mapped page. C strings are not aligned,
		// so a caller reading a name has to turn it off or every name comes back null.
		internal static unsafe bool IsReadable(IntPtr p, int bytes = 8, bool requireAligned = true)
		{
			long addr = (long)p;
			if (addr <= 0) return false;
			if (addr < 0x10000) return false;      // the reserved low range: never a real object
			if (requireAligned && (addr & 7) != 0) return false;

			MemoryBasicInformation mbi;
			if (VirtualQuery(p, out mbi, (IntPtr)sizeof(MemoryBasicInformation)) == IntPtr.Zero) return false;
			if (mbi.State != MemCommit) return false;
			if ((mbi.Protect & PageGuard) != 0) return false;       // touching it would raise
			if (!ProtectionAllowsRead(mbi.Protect)) return false;
			// The read must not run past the end of the region it was queried in.
			return addr + bytes <= (long)mbi.BaseAddress + (long)mbi.RegionSize;
		}

		// An il2cpp object begins with a pointer to its Il2CppClass. Garbage that happens to sit on a
		// committed page still fails this, because the first eight bytes have to be a pointer into
		// committed memory as well.
		internal static unsafe bool IsLiveObject(IntPtr p)
		{
			if (!IsReadable(p, 16)) return false;
			IntPtr klass = *(IntPtr*)p;
			return IsReadable(klass, 16);
		}

		// The one call sites use. A plain managed object needs no guard and passes straight through;
		// only il2cpp proxies are checked.
		internal static bool Alive(object o)
		{
			if (o == null) return false;
			Il2CppObjectBase b = o as Il2CppObjectBase;
			if (b == null) return true;
			IntPtr p;
			try { p = b.Pointer; }
			catch { return false; }   // ObjectCollectedException: the handle is already gone
			return IsLiveObject(p);
		}

		// Convenience for the "give me it or null" shape.
		internal static T OrNull<T>(T o) where T : class => Alive(o) ? o : null;
	}
}
