using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace VRChatArchiveMod.Core
{
	// READ A SEQUENCE OF STRINGS OUT OF WHATEVER SHAPE IL2CPP HANDED US.
	//
	// Udon's public-variable table hands back its symbol names as an IReadOnlyCollection<string>
	// PROXY — an Il2CppInterop object over a native collection. Such a proxy is NOT a managed
	// IEnumerable (the `is System.Collections.IEnumerable` test fails), and its managed type is the
	// interface, not the concrete class, so reflecting Length/Item on it finds nothing either. Both
	// readers the mod used to have came back empty, which is why the variable table never listed a
	// single symbol and the video player's VRCUrl field was never found.
	//
	// The one thing every il2cpp collection honours is its own IEnumerable: TryCast the proxy to
	// Il2CppSystem.Collections.IEnumerable (a cast on the NATIVE class, not the managed wrapper) and
	// walk its enumerator. Each element is checked alive before its pointer is read — a dead proxy
	// read is an access violation no try/catch survives.
	//
	// Order of attempts: a plain managed enumerable (unit tests, older builds that marshal to
	// managed), then the il2cpp enumeration, then the reflected Length/Item fallback for
	// ImmutableArray-style proxies that expose an indexer but no enumerator.
	internal static class Il2CppSeq
	{
		private const int Cap = 400;   // a table this long is a generated one, not a read

		/// <summary>Which reader answered last ("string[]", "List<string>", "enumerator", "indexer", "managed"). For probes.</summary>
		internal static string LastPath = "none";

		internal static List<string> Strings(object seq)
		{
			var outp = new List<string>();
			if (seq == null) return outp;

			// 1) Already managed: nothing to marshal.
			try
			{
				if (seq is System.Collections.IEnumerable en)
				{
					LastPath = "managed";
					foreach (object o in en)
					{
						string s = o as string ?? o?.ToString();
						if (!string.IsNullOrEmpty(s)) outp.Add(s);
						if (outp.Count >= Cap) break;
					}
					if (outp.Count > 0) return outp;
				}
			}
			catch { }

			// 2) The concrete il2cpp shapes that can be INDEXED. A string[] or a List<string> is walked
		//    by index through the interop's own array/list wrappers, which touch nothing but the
		//    element pointers. This comes BEFORE the generic enumerator on purpose: an enumerator
		//    obtained through IEnumerable is a boxed struct driven by interface dispatch, and on a
		//    build whose runtime structs are reshuffled that is the most fragile call in the mod.
		try
		{
			var arr = (seq as Il2CppObjectBase)?.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray>();
			if (arr != null)
			{
				LastPath = "string[]";
				for (int i = 0; i < arr.Length && i < Cap; i++) { string s = arr[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
				return outp;
			}
			var list = (seq as Il2CppObjectBase)?.TryCast<Il2CppSystem.Collections.Generic.List<string>>();
			if (list != null)
			{
				LastPath = "List<string>";
				int n = list.Count;
				for (int i = 0; i < n && i < Cap; i++) { string s = list[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
				return outp;
			}
		}
		catch { }

		// 3) ANY ICollection<string> -- Dictionary.KeyCollection included -- is COPIED into an array
		//    with its own CopyTo and then indexed. This replaces the enumerator walk that was here:
		//    on 2026-09-02 the game died on the first MoveNext() of the boxed struct enumerator that
		//    IEnumerable.GetEnumerator() hands back for a KeyCollection (the last log line was the
		//    probe just before it). CopyTo is one interface call on a normal class object and the
		//    array it fills is read through the interop's own wrapper: no boxed struct, no
		//    interface dispatch per element, nothing an enumerator could get wrong.
		try
		{
			var col = (seq as Il2CppObjectBase)?.TryCast<Il2CppSystem.Collections.Generic.ICollection<string>>();
			if (col != null)
			{
				LastPath = "ICollection.CopyTo";
				int total = col.Count;
				if (total <= 0) return outp;
				var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(total);
				col.CopyTo(arr, 0);
				int n = total > Cap ? Cap : total;
				for (int i = 0; i < n; i++) { string s = arr[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
				return outp;
			}
		}
		catch { }

		// 4) Indexable proxy (ImmutableArray<string> and friends): Length/Count + Item.
			try
			{
				LastPath = "indexer";
				var t = seq.GetType();
				var lenP = t.GetProperty("Length") ?? t.GetProperty("Count");
				var item = t.GetProperty("Item");
				if (lenP?.GetValue(seq) is int len && item != null)
					for (int i = 0; i < len && i < Cap; i++)
					{
						try
						{
							string s = item.GetValue(seq, new object[] { i }) as string;
							if (!string.IsNullOrEmpty(s)) outp.Add(s);
						}
						catch { }
					}
			}
			catch { }
			return outp;
		}
	}
}
