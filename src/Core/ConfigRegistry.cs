using System;
using System.Collections.Generic;
using System.Reflection;

namespace Killiorim.Core
{
	// EVERY SETTING THE MOD HAS, as one list.
	//
	// This is the backbone of the desktop client's MOD SETTINGS page: the control channel walks this
	// list to announce the schema (section, key, type, description) and again on every sync to send
	// the current values, and applies a change by writing straight back into the ConfigEntry.
	//
	// It lived inside DevToolsModule, next to a spike hunter and a type browser that had nothing to
	// do with it. That coupling meant the dev tooling could not be removed without taking the
	// client's entire settings page with it, so the registry now stands on its own.
	//
	// Read by REFLECTION over ModConfig's static fields, once. They are static fields of a known
	// type, so this needs no BepInEx internals and cannot drift out of step with the config file the
	// way a hand-written list would: add a ConfigEntry to ModConfig and it appears in the client.
	public static class ConfigRegistry
	{
		public sealed class Entry
		{
			public string Section;
			public string Key;
			public string Type;
			public object Value;
			public BepInEx.Configuration.ConfigEntryBase Raw;
		}

		private static List<Entry> _entries;

		public static List<Entry> Config()
		{
			if (_entries != null) return _entries;
			var list = new List<Entry>();
			try
			{
				foreach (var f in typeof(ModConfig).GetFields(BindingFlags.Public | BindingFlags.Static))
				{
					object v;
					try { v = f.GetValue(null); } catch { continue; }
					var ceb = v as BepInEx.Configuration.ConfigEntryBase;
					if (ceb == null) continue;
					list.Add(new Entry
					{
						Section = ceb.Definition.Section,
						Key = ceb.Definition.Key,
						Type = ceb.SettingType?.Name ?? "?",
						Raw = ceb,
					});
				}
				list.Sort((x, y) =>
				{
					int c = string.Compare(x.Section, y.Section, StringComparison.OrdinalIgnoreCase);
					return c != 0 ? c : string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
				});
			}
			catch (Exception e) { Killiorim.Logger.LogWarning("[ConfigRegistry] scan: " + e.Message); }
			_entries = list;
			return _entries;
		}

		// The value as TEXT, in the exact form the .cfg file holds it. BoxedValue.ToString() was
		// culture-sensitive: on a French Windows a float came out as "0,5", the client parsed that
		// as 5 (or failed), sent it back, the mod saw a difference and the two resent the same
		// setting at each other three times over. TomlTypeConverter is what BepInEx itself writes
		// the file with — invariant culture, '.' decimal — so mod, file and client all agree.
		public static string ValueOf(Entry e)
		{
			try
			{
				if (e?.Raw == null) return "";
				return BepInEx.Configuration.TomlTypeConverter.ConvertToString(e.Raw.BoxedValue, e.Raw.SettingType) ?? "";
			}
			catch { return "?"; }
		}

		public static void SetBool(Entry e, bool v) { try { e.Raw.BoxedValue = v; } catch { } }
	}
}
