using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// DEV TOOLS — the instruments, kept apart from the menu that draws them.
	//
	// Three things live here, and each one exists because a real question was hard to answer:
	//
	//   SPIKE HUNTER  "the game stutters and I don't know where it comes from". A per-second
	//                 average cannot answer that: a module that costs 40 ms once a second reads
	//                 as 40 ms/s, the same as one costing 0.7 ms every frame — and only the first
	//                 one is felt. So this records the WORST FRAMES themselves, with what the mod
	//                 was doing during them, instead of an average that hides exactly the event
	//                 being hunted.
	//
	//   CONFIG EDITOR every setting, searchable and editable live. 130-odd config entries exist
	//                 and barely a third have a control anywhere, so the rest meant editing a
	//                 .cfg and restarting the game to test one value.
	//
	//   TYPE FINDER   search the loaded IL2CPP types and list their members. This is the single
	//                 most repeated task in this project — "what is this class actually called,
	//                 and what can I call on it" — and it was being answered by dumping megabytes
	//                 of text to disk and grepping it.
	//
	// All read-only with respect to the game: nothing here calls a VRChat method, spawns an
	// object or sends anything. The cost when the DEV tab is not open is one float compare per
	// frame.
	public class DevToolsModule : IModule
	{
		public override string Name => "DevTools";

		// ------------------------------------------------------------------ spike hunter

		public sealed class Spike
		{
			public float At;             // realtime when it happened
			public float Ms;             // how long the frame took
			public string Worst;         // costliest mod module that frame
			public float WorstMs;
			public string Clock;         // wall clock, for matching against the log
		}

		private static readonly List<Spike> Spikes = new List<Spike>();
		private static readonly object Gate = new object();
		private const int MaxSpikes = 60;

		// A frame is a spike when it takes longer than this. 50 ms is the honest threshold: below
		// it you are at 20 FPS or better and nothing reads as a stutter; above it, you felt it.
		public static float SpikeMs = 50f;
		public static bool Hunting;

		// Rolling frame-time history for the graph — small, fixed, never allocates after startup.
		private const int History = 180;
		private static readonly float[] Frames = new float[History];
		private static int _cursor;

		public static int SpikeCount { get { lock (Gate) return Spikes.Count; } }
		public static List<Spike> SpikeSnapshot() { lock (Gate) return new List<Spike>(Spikes); }
		public static void ClearSpikes() { lock (Gate) Spikes.Clear(); }

		public static float[] FrameHistory => Frames;
		public static int FrameCursor => _cursor;

		// Worst frame seen since the hunt started, so a single stutter is not lost between glances.
		public static float WorstMs;

		public override void OnUpdate()
		{
			try
			{
				float ms = Time.unscaledDeltaTime * 1000f;
				Frames[_cursor] = ms;
				_cursor = (_cursor + 1) % History;

				if (!Hunting) return;
				if (ms > WorstMs) WorstMs = ms;
				if (ms < SpikeMs) return;

				// Blame the costliest module of the second the spike LANDED IN, not of the last
				// completed window — at spike time that one describes the second before it, which
				// is the one second guaranteed not to contain the stutter.
				string worst = "?";
				float worstMs = 0f;
				try
				{
					var hot = ModuleManager.CurrentHot();
					if (!string.IsNullOrEmpty(hot.Key) && hot.Key != "?" && hot.Value > 0.01f)
					{
						worst = hot.Key; worstMs = hot.Value;
					}
					else
					{
						var rep = ModuleManager.ProfileReport();
						if (rep != null && rep.Count > 0) { worst = rep[0].Key; worstMs = rep[0].Value; }
					}
				}
				catch { }

				lock (Gate)
				{
					Spikes.Insert(0, new Spike
					{
						At = Time.realtimeSinceStartup,
						Ms = ms,
						Worst = worst,
						WorstMs = worstMs,
						Clock = DateTime.Now.ToString("HH:mm:ss"),
					});
					while (Spikes.Count > MaxSpikes) Spikes.RemoveAt(Spikes.Count - 1);
				}
			}
			catch { }
		}

		// ------------------------------------------------------------------ config editor

		public sealed class Entry
		{
			public string Section;
			public string Key;
			public string Type;
			public object Value;
			public BepInEx.Configuration.ConfigEntryBase Raw;
		}

		private static List<Entry> _entries;

		// Every ConfigEntry declared on ModConfig, read by reflection ONCE. They are static fields
		// of a known type, so this needs no BepInEx internals and cannot drift out of sync with the
		// config file the way a hand-written list would.
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
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[DevTools] config scan: " + e.Message); }
			_entries = list;
			return _entries;
		}

		public static string ValueOf(Entry e)
		{
			try { return e?.Raw?.BoxedValue?.ToString() ?? ""; } catch { return "?"; }
		}

		public static void SetBool(Entry e, bool v) { try { e.Raw.BoxedValue = v; } catch { } }

		// ------------------------------------------------------------------ type finder

		public sealed class Hit
		{
			public string Full;
			public string Assembly;
			public int Members;
		}

		private static readonly List<Hit> Found = new List<Hit>();
		public static string LastQuery = "";
		public static string FindStatus = "";
		public static List<Hit> Hits { get { lock (Gate) return new List<Hit>(Found); } }

		// Substring search across every loaded assembly. Capped, because a bare "a" would otherwise
		// try to render forty thousand rows.
		public static void FindTypes(string query)
		{
			lock (Gate) Found.Clear();
			LastQuery = query ?? "";
			if (string.IsNullOrEmpty(query) || query.Length < 3)
			{
				FindStatus = "type at least 3 characters";
				return;
			}

			int scanned = 0;
			var res = new List<Hit>();
			try
			{
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					Type[] types;
					try { types = asm.GetTypes(); }
					catch (ReflectionTypeLoadException ex) { types = ex.Types ?? new Type[0]; }
					catch { continue; }

					string an = "";
					try { an = asm.GetName().Name; } catch { }

					foreach (var t in types)
					{
						if (t == null) continue;
						scanned++;
						string n;
						try { n = t.Name; } catch { continue; }
						if (n.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

						int members = 0;
						try { members = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length; }
						catch { }

						res.Add(new Hit { Full = t.FullName ?? n, Assembly = an, Members = members });
						if (res.Count >= 200) break;
					}
					if (res.Count >= 200) break;
				}
			}
			catch (Exception e) { FindStatus = "search failed: " + e.Message; return; }

			lock (Gate) Found.AddRange(res);
			FindStatus = res.Count + " match(es) of " + scanned + " types"
				+ (res.Count >= 200 ? " — capped at 200, narrow the search" : "");
		}

		// The members of one found type, formatted for reading rather than for parsing.
		public static List<string> Members(string fullName, int max = 160)
		{
			var outp = new List<string>();
			try
			{
				Type t = null;
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					try { t = asm.GetType(fullName, false); } catch { }
					if (t != null) break;
				}
				if (t == null) { outp.Add("(type no longer resolvable)"); return outp; }

				const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

				foreach (var p in t.GetProperties(F))
				{
					if (outp.Count >= max) break;
					try { outp.Add("prop  " + Short(p.PropertyType) + "  " + p.Name); } catch { }
				}
				foreach (var f in t.GetFields(F))
				{
					if (outp.Count >= max) break;
					try { outp.Add("field " + Short(f.FieldType) + "  " + f.Name); } catch { }
				}
				foreach (var m in t.GetMethods(F))
				{
					if (outp.Count >= max) break;
					try
					{
						var ps = m.GetParameters();
						var sb = new System.Text.StringBuilder(64);
						sb.Append("m     ").Append(Short(m.ReturnType)).Append("  ").Append(m.Name).Append('(');
						for (int i = 0; i < ps.Length; i++)
						{
							if (i > 0) sb.Append(", ");
							sb.Append(Short(ps[i].ParameterType));
						}
						sb.Append(')');
						outp.Add(sb.ToString());
					}
					catch { }
				}
			}
			catch (Exception e) { outp.Add("threw: " + e.Message); }
			return outp;
		}

		private static string Short(Type t) { try { return t == null ? "?" : t.Name; } catch { return "?"; } }
	}
}
