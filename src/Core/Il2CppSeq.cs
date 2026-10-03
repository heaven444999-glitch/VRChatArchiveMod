using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Killiorim.Core
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
		//
		//    THIS PATH MUST NOT END THE SEARCH WHEN IT FINDS NOTHING (2026-09-07).
		//    ImmutableArray<string> -- exactly what IUdonSymbolTable.GetSymbols() hands back on this
		//    build -- DOES satisfy the TryCast below, because the struct implements ICollection<T>.
		//    So this path was entered, its Count came back 0 for all 60 behaviours in a USharpVideo
		//    world, and "return outp" handed back an empty list, retiring the search before step 4 --
		//    the indexer, which is the one written FOR ImmutableArray. The symptom was silent and
		//    total: the injector found no VRCUrl symbol anywhere, concluded the world had no
		//    scriptable player, and fell back to the local-only direct LoadURL. The crash trail said
		//    so on every one of its 60 lines:
		//        "UdonSymbols gave nothing: GetSymbols/ICollection.CopyTo
		//         [ImmutableArray`1/ImmutableArray`1]"
		//    So: commit to this path only if it actually produced names; otherwise fall through. A
		//    genuinely empty collection costs one wasted indexer pass and still returns empty, which
		//    is the right answer anyway.
		try
		{
			var col = (seq as Il2CppObjectBase)?.TryCast<Il2CppSystem.Collections.Generic.ICollection<string>>();
			if (col != null)
			{
				LastPath = "ICollection.CopyTo";
				int total = col.Count;
				if (total > 0)
				{
					var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(total);
					col.CopyTo(arr, 0);
					int n = total > Cap ? Cap : total;
					for (int i = 0; i < n; i++) { string s = arr[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
				}
				if (outp.Count > 0) return outp;
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
				{
					// ONE ARGS ARRAY, REUSED. This is a reflection call per element, and it became
					// the hot path the moment step 3 was taught to fall through to it — a video
					// injection reads the symbol table of every candidate script, which in a ProTV
					// world is several hundred symbols. Allocating a fresh object[1] for each of them
					// bought nothing: GetValue copies the value out before returning, so the same box
					// can carry every index.
					object[] args = new object[1];
					for (int i = 0; i < len && i < Cap; i++)
					{
						try
						{
							args[0] = i;
							string s = item.GetValue(seq, args) as string;
							if (!string.IsNullOrEmpty(s)) outp.Add(s);
						}
						catch { }
					}
				}
			}
			catch { }
			return outp;
		}
	}
}
