using System;
using System.Text;
using Il2CppInterop.Runtime;

namespace VRChatArchiveMod.Core
{
	// WHY ConvertDelegate KILLS THE GAME — measured, not guessed.
	//
	// DelegateSupport.ConvertDelegate dies in Il2CppSystem.Delegate.set_method_ptr with an access
	// violation. That setter does one thing: write a pointer at objectPointer + fieldOffset. So the
	// object is wrong, or the offset is wrong, and those need very different fixes.
	//
	// The offset comes from Il2CppInterop's own reimplementation of il2cpp_field_get_offset, which
	// reads it at FieldInfo+0x08 — a constant baked in for some earlier VRChat build. VRChat reorders
	// runtime structs (Il2CppClass moved eight members on this one), so that constant is exactly the
	// kind of thing that goes stale silently.
	//
	// This reads the truth from the other side: il2cpp_class_get_field_from_name is a real game export,
	// so the FieldInfo it returns is genuine. Dumping its first words shows where the name and the
	// offset actually live, and System.Delegate's layout is known — method_ptr sits at 0x10, right
	// after the object header. If Il2CppInterop reports something else, the constant is the bug.
	//
	// Reading is safe; it is the WRITE at a bad offset that is fatal. Nothing here writes.
	internal static class DelegateProbe
	{
		// Field name -> where il2cpp puts it in a System.Delegate instance on 64-bit.
		private static readonly (string Name, int Expected)[] Known =
		{
			("method_ptr", 0x10),
			("invoke_impl", 0x18),
			("m_target", 0x20),
			("method", 0x28),
		};

		internal static unsafe void Run()
		{
			try
			{
				IntPtr klass = IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Delegate");
				if (klass == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[DelegateProbe] System.Delegate class not found.");
					return;
				}

				foreach (var (name, expected) in Known)
				{
					IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
					if (f == IntPtr.Zero)
					{
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[DelegateProbe] " + name + ": the game has no field by that name.");
						continue;
					}

					// Where does the FieldInfo actually keep its name and its offset?
					var sb = new StringBuilder();
					for (int o = 0; o <= 0x20; o += 8)
					{
						if (!NativeGuard.IsReadable(IntPtr.Add(f, o), 8)) { sb.Append(" +").Append(o.ToString("X")).Append(":??"); continue; }
						IntPtr v = *(IntPtr*)((byte*)f + o);
						string s = Ascii(v);
						sb.Append(" +").Append(o.ToString("X")).Append(':');
						if (s != null) sb.Append('"').Append(s).Append('"');
						else if ((long)v >= 0 && (long)v < 0x10000) sb.Append("int=0x").Append(((long)v).ToString("X"));
						else sb.Append("ptr");
					}

					uint reported = IL2CPP.il2cpp_field_get_offset(f);
					bool ok = reported == (uint)expected;
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[DelegateProbe] " + name + ": Il2CppInterop says 0x" + reported.ToString("X")
						+ ", il2cpp puts it at 0x" + expected.ToString("X")
						+ (ok ? "  MATCH" : "  <-- MISMATCH, this is the crash")
						+ " | FieldInfo words:" + sb);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[DelegateProbe] failed: " + Unwrap.Describe(e));
			}
		}

		// A short printable C string, or null. Bounded so a non-string pointer cannot run away.
		private static unsafe string Ascii(IntPtr p)
		{
			if (!NativeGuard.IsReadable(p, 1, requireAligned: false)) return null;
			var sb = new StringBuilder();
			byte* b = (byte*)p;
			for (int i = 0; i < 40; i++)
			{
				byte c = b[i];
				if (c == 0) return sb.Length >= 3 ? sb.ToString() : null;
				if (c < 0x20 || c > 0x7E) return null;
				sb.Append((char)c);
			}
			return null;
		}
	}
}
