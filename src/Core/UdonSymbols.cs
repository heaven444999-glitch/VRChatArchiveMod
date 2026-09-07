using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace VRChatArchiveMod.Core
{
	// THE SYMBOL NAMES OF AN UDON BEHAVIOUR, WITHOUT TOUCHING ITS VARIABLE TABLE.
	//
	// Every Udon program carries a symbol table, and IUdonSymbolTable.GetExportedSymbols() hands
	// back the exported (public) symbol names as a plain string[] -- an il2cpp array the interop
	// indexes directly. That is the whole reason this exists: the public-variable table's
	// VariableSymbols is a Dictionary.KeyCollection, and walking it through an IEnumerable
	// enumerator is what took the game down on 2026-09-02. An array cannot do that.
	//
	// Members are found BY TYPE, never by name: VRChat obfuscates the field names on this build
	// (there is no member literally called programSource), but a field's TYPE survives.
	internal static class UdonSymbols
	{
		private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static bool _resolved;
		private static MemberInfo _programMember;      // member of ub whose type is (or implements) IUdonProgram
		private static MemberInfo _sourceMember;       // member of ub whose type derives from AbstractUdonProgramSource
		private static PropertyInfo _serializedAsset;  // AbstractUdonProgramSource.SerializedProgramAsset
		private static MethodInfo _retrieve;           // AbstractSerializedUdonProgramAsset.RetrieveProgram()
		private static PropertyInfo _symbolTable;      // IUdonProgram.SymbolTable
		private static MethodInfo _exported;           // IUdonSymbolTable.GetExportedSymbols()
		private static MethodInfo _allSymbols;         // IUdonSymbolTable.GetSymbols() -- every symbol, private synced ones included
		private static PropertyInfo _syncTable;      // IUdonProgram.SyncMetadataTable
		private static MethodInfo _allSync;          // IUdonSyncMetadataTable.GetAllSyncMetadata()
		private static PropertyInfo _syncName;       // IUdonSyncMetadata.Name
		public static string LastPath = "none";

		private static Type Find(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { var t = asm.GetType(full, false); if (t != null) return t; } catch { }
			}
			return null;
		}

		private static Type MemberType(MemberInfo m) => m is PropertyInfo p ? p.PropertyType : (m as FieldInfo)?.FieldType;
		private static object Read(MemberInfo m, object o) => m is PropertyInfo p ? p.GetValue(o) : ((FieldInfo)m).GetValue(o);

		private static void Resolve(Type ub)
		{
			_resolved = true;
			try
			{
				Type iProgram = Find("VRC.Udon.Common.Interfaces.IUdonProgram");
				Type absSource = Find("VRC.Udon.AbstractUdonProgramSource");
				Type iTable = Find("VRC.Udon.Common.Interfaces.IUdonSymbolTable");
				for (Type t = ub; t != null && t != typeof(object); t = t.BaseType)
				{
					foreach (var m in t.GetMembers(Any | BindingFlags.DeclaredOnly))
					{
						var mt = MemberType(m);
						if (mt == null) continue;
						if (_programMember == null && iProgram != null && iProgram.IsAssignableFrom(mt)) _programMember = m;
						if (_sourceMember == null && absSource != null && absSource.IsAssignableFrom(mt)) _sourceMember = m;
					}
				}
				if (absSource != null) _serializedAsset = absSource.GetProperty("SerializedProgramAsset", Any);
				var assetType = _serializedAsset?.PropertyType;
				if (assetType != null) _retrieve = assetType.GetMethod("RetrieveProgram", Any, null, Type.EmptyTypes, null);
				if (iProgram != null) _symbolTable = iProgram.GetProperty("SymbolTable", Any);
				if (iTable != null) _exported = iTable.GetMethod("GetExportedSymbols", Any, null, Type.EmptyTypes, null);
				if (iTable != null) _allSymbols = iTable.GetMethod("GetSymbols", Any, null, Type.EmptyTypes, null);
				Type iSyncTable = Find("VRC.Udon.Common.Interfaces.IUdonSyncMetadataTable");
				Type iSyncMeta = Find("VRC.Udon.Common.Interfaces.IUdonSyncMetadata");
				if (iProgram != null) _syncTable = iProgram.GetProperty("SyncMetadataTable", Any);
				if (iSyncTable != null) _allSync = iSyncTable.GetMethod("GetAllSyncMetadata", Any, null, Type.EmptyTypes, null);
				if (iSyncMeta != null) _syncName = iSyncMeta.GetProperty("Name", Any);
				VRChatArchiveModPlugin.Logger.LogInfo("[UdonSymbols] resolved: program member=" + (_programMember?.Name ?? "-")
					+ " source member=" + (_sourceMember?.Name ?? "-") + " SerializedProgramAsset=" + (_serializedAsset != null)
					+ " RetrieveProgram=" + (_retrieve != null) + " SymbolTable=" + (_symbolTable != null) + " GetExportedSymbols=" + (_exported != null));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[UdonSymbols] resolve failed: " + e.Message); }
		}

		/// <summary>
		/// EVERY symbol of the program, private ones included. This is the list that matters for a
		/// video player: USharpVideo, ProTV and VideoTXL keep the URL they are playing in a private
		/// [UdonSynced] field (_syncedURL and friends), which publicVariables never lists but which
		/// SetProgramVariable writes like any other. Empty when the route is closed. Never throws.
		/// </summary>
		public static List<string> All(object ub) => Read(ub, true);

		/// <summary>Exported symbol names of the behaviour's program; empty when the route is closed. Never throws.</summary>
		public static List<string> Exported(object ub) => Read(ub, false);

		/// <summary>
		/// The [UdonSynced] symbol names of the behaviour's program, from its sync metadata table. Empty
		/// when the route is closed. Never throws. Read through the same live-program / serialized-asset
		/// path as the symbol names, and the metadata array is indexed, never enumerated.
		/// </summary>
		public static HashSet<string> Synced(object ub)
		{
			var outp = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				if (ub == null) return outp;
				if (!_resolved) Resolve(ub.GetType());
				if (_syncTable == null || _allSync == null || _syncName == null) return outp;
				object program = Program(ub);
				if (program == null) return outp;
				var table = _syncTable.GetValue(program);
				if (table == null || (table is Il2CppObjectBase to && !NativeGuard.Alive(to))) return outp;
				object arr = _allSync.Invoke(table, null);
				if (arr == null) return outp;
				var at = arr.GetType();
				var lenP = at.GetProperty("Length") ?? at.GetProperty("Count");
				var item = at.GetProperty("Item");
				if (!(lenP?.GetValue(arr) is int len) || item == null) return outp;
				for (int i = 0; i < len && i < 2000; i++)
				{
					try
					{
						object meta = item.GetValue(arr, new object[] { i });
						if (meta == null) continue;
						string n = _syncName.GetValue(meta) as string;
						if (!string.IsNullOrEmpty(n)) outp.Add(n);
					}
					catch { }
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[UdonSymbols] sync metadata: " + e.Message); }
			return outp;
		}

		// The live program, or the one rebuilt from the serialized asset. Null when neither is reachable.
		private static object Program(object ub)
		{
			object program = null;
			if (_programMember != null)
			{
				try { program = Read(_programMember, ub); } catch { program = null; }
				if (program is Il2CppObjectBase po && !NativeGuard.Alive(po)) program = null;
			}
			if (program == null && _sourceMember != null && _serializedAsset != null && _retrieve != null)
			{
				try
				{
					var source = Read(_sourceMember, ub);
					if (source is Il2CppObjectBase so && NativeGuard.Alive(so))
					{
						var asset = _serializedAsset.GetValue(source);
						if (asset is Il2CppObjectBase ao && NativeGuard.Alive(ao)) program = _retrieve.Invoke(asset, null);
					}
				}
				catch { program = null; }
			}
			return program;
		}

		// The runtime type of whatever GetSymbols handed back -- managed type, and for an il2cpp proxy
		// its native class name too, so a build the reader cannot walk names the exact type to add.
		private static string SafeTypeName(object o)
		{
			if (o == null) return "null";
			try
			{
				string mt = o.GetType().Name;
				if (o is Il2CppObjectBase ib) { try { return mt + "/" + MenuCard.Il2CppNameOf(ib); } catch { } }
				return mt;
			}
			catch { return "?"; }
		}

		private static List<string> Read(object ub, bool all)
		{
			var outp = new List<string>();
			try
			{
				if (ub == null) return outp;
				if (!_resolved) Resolve(ub.GetType());
				if (_symbolTable == null || _exported == null) { LastPath = "unresolved"; return outp; }

				// The live program first (already loaded, nothing to deserialise); the serialized asset
				// second (RetrieveProgram rebuilds it, which is fine for a user-triggered read).
				object program = null;
				if (_programMember != null)
				{
					try { program = Read(_programMember, ub); } catch { program = null; }
					if (program is Il2CppObjectBase po && !NativeGuard.Alive(po)) program = null;
					if (program != null) LastPath = "program." + _programMember.Name;
				}
				if (program == null && _sourceMember != null && _serializedAsset != null && _retrieve != null)
				{
					try
					{
						var source = Read(_sourceMember, ub);
						if (source is Il2CppObjectBase so && NativeGuard.Alive(so))
						{
							var asset = _serializedAsset.GetValue(source);
							if (asset is Il2CppObjectBase ao && NativeGuard.Alive(ao)) program = _retrieve.Invoke(asset, null);
						}
					}
					catch { program = null; }
					if (program != null) LastPath = "source.RetrieveProgram";
				}
				if (program == null) { LastPath = "no program"; return outp; }

				var table = _symbolTable.GetValue(program);
				if (table == null || (table is Il2CppObjectBase to && !NativeGuard.Alive(to))) { LastPath = "no table"; return outp; }
				var getter = all && _allSymbols != null ? _allSymbols : _exported;
				object raw = getter.Invoke(table, null);
				if (raw == null) { LastPath = "null symbols"; return outp; }
				// GetSymbols()/GetExportedSymbols() does NOT always hand back an Il2CppStringArray on this
				// build -- the old hard cast then gave null -> "no array", All() returned nothing, and the
				// private [UdonSynced] _syncedURL was never found, so video injection fell back to poking the
				// raw player and the world re-asserted its own URL ("Video error, retrying"). Read the array
				// through the shared reader, which handles string[], List<string>, ICollection.CopyTo and an
				// indexable proxy alike; the runtime type is logged so a build that still fails names it.
				var names = Il2CppSeq.Strings(raw);
				LastPath = (all ? "GetSymbols/" : "GetExportedSymbols/") + Il2CppSeq.LastPath + " [" + SafeTypeName(raw) + "]";
				for (int i = 0; i < names.Count && i < 2000; i++) { string s = names[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
			}
			catch (Exception e) { LastPath = "threw: " + e.Message; }
			return outp;
		}
	}
}
