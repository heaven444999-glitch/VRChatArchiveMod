using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// AUTO PROBE — everything the reverse-engineering work needs, gathered once, by the mod itself.
	//
	// This replaces three hand-run UnityExplorer scripts. It answers the two questions still open:
	//
	//   FAVOURITES  What does a REAL FavoriteListModel look like — the one VRChat built, with every
	//               field already correct including the ones we cannot name? The first attempt at a
	//               custom avatar category built that record by hand: it compiled, every obfuscated
	//               member resolved, and it crashed the game on menu open, because a record carries
	//               fields whose meaning was never established and the native code dereferences
	//               them. Copying a real one removes the guessing.
	//
	//   UDON        Which events are ACTUALLY network-callable? The manager currently decides from
	//               the name — an event starting with '_' can never be networked, so everything
	//               else is badged GLOBAL. That is a heuristic, and it is wrong in the direction
	//               that matters: a world can define "ObjectOrbit" and only ever fire it locally,
	//               so the badge promises something the world never does.
	//
	// THE RULE THIS FILE IS BUILT AROUND: bounded work, always. Earlier probes froze the game (a
	// process-wide walk), spammed hundreds of errors (forcing Il2CppInterop to resolve absent
	// types), and crashed it (activating menu pages). Every loop here has a cap, the whole run has
	// a stopwatch budget, and it gives up with a partial report rather than overrun. A partial
	// answer costs nothing; a frozen game costs the user their session.
	//
	// Strictly read-only: no record is created, no list modified, no Udon event raised, nothing sent.
	public static class AutoProbe
	{
		private const int BudgetMs = 250;
		private static System.Diagnostics.Stopwatch _sw;

		private static bool Spent => _sw != null && _sw.ElapsedMilliseconds > BudgetMs;

		private const BindingFlags F =
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

		// True once a run actually SAW the favourites data. The caller retries until then: at the
		// moment the probe first fires, VRChat may not have logged in or built its favourites yet,
		// and a report that says "null" is not an answer — it would just mean relaunching the game,
		// which is exactly the manual step this is supposed to remove.
		public static bool GotData { get; private set; }

		public static string Run()
		{
			_sw = System.Diagnostics.Stopwatch.StartNew();
			var sb = new System.Text.StringBuilder(96 * 1024);

			sb.Append("VRCHAT ARCHIVE — AUTO PROBE (favourites + udon). Read-only.\n");
			sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
			sb.Append("UniverseLib type resolution: ").Append(MenuPanelProbe.HasActualType ? "AVAILABLE" : "absent (names will be raw)").Append('\n');
			sb.Append(new string('=', 92)).Append("\n");

			Section(sb, "FAVOURITES — the live data layer", b => Favourites(b));
			Section(sb, "UDON — the real network surface", b => Udon(b));

			sb.Append("\nelapsed: ").Append(_sw.ElapsedMilliseconds).Append(" ms");
			if (Spent) sb.Append("   [BUDGET SPENT — partial by design]");
			sb.Append("\nRead-only: nothing was created, modified, raised or sent.\n");
			return sb.ToString();
		}

		private static void Section(System.Text.StringBuilder sb, string title, Action<System.Text.StringBuilder> body)
		{
			sb.Append("\n######## ").Append(title).Append(" ########\n");
			try { body(sb); }
			catch (Exception e) { sb.Append("  !! threw: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n'); }
		}

		// ------------------------------------------------------------------ favourites

		private static void Favourites(System.Text.StringBuilder sb)
		{
			object area = null;
			Type apiType = FindType("VRC.Core.API");
			if (apiType == null) { sb.Append("  VRC.Core.API not found\n"); return; }

			try
			{
				var p = apiType.GetProperty("Favorites", BindingFlags.Public | BindingFlags.Static);
				if (p != null) area = p.GetValue(null);
				else
				{
					var f = apiType.GetField("Favorites", BindingFlags.Public | BindingFlags.Static);
					if (f != null) area = f.GetValue(null);
				}
			}
			catch (Exception e) { sb.Append("  reading API.Favorites threw: ").Append(e.Message).Append('\n'); }

			if (area == null)
			{
				sb.Append("  API.Favorites is null — not logged in yet, or the menu has never been opened.\n");
				sb.Append("  (the probe will run again by itself once it is available)\n");
				return;
			}
			GotData = true;

			Type at = TypeOf(area);
			sb.Append("  FavoriteArea: ").Append(at?.FullName ?? "?").Append('\n');
			if (at == null) return;

			// FIELDS **AND** PROPERTIES. Il2CppInterop surfaces a native field as a PROPERTY on the
			// managed wrapper, so scanning GetFields alone found nothing at all on FavoriteArea and
			// the first run of this reported the type and then an empty section. Collect both and
			// read them through one accessor.
			var members = new List<KeyValuePair<string, Func<object, object>>>();
			var typeNames = new Dictionary<string, string>(StringComparer.Ordinal);

			foreach (var f in at.GetFields(F))
			{
				string tn = f.FieldType.Name;
				if (tn.IndexOf("List", StringComparison.Ordinal) < 0
					&& tn.IndexOf("IReadOnly", StringComparison.Ordinal) < 0) continue;
				var ff = f;
				members.Add(new KeyValuePair<string, Func<object, object>>(f.Name, o => ff.GetValue(o)));
				typeNames[f.Name] = tn;
			}
			foreach (var p in at.GetProperties(F))
			{
				if (p.GetIndexParameters().Length > 0) continue;
				string tn = p.PropertyType.Name;
				if (tn.IndexOf("List", StringComparison.Ordinal) < 0
					&& tn.IndexOf("IReadOnly", StringComparison.Ordinal) < 0) continue;
				if (typeNames.ContainsKey(p.Name)) continue;
				var pp = p;
				members.Add(new KeyValuePair<string, Func<object, object>>(p.Name, o => pp.GetValue(o)));
				typeNames[p.Name] = tn;
			}

			sb.Append("  collection members: ").Append(members.Count).Append('\n');

			foreach (var mem in members)
			{
				if (Spent) { sb.Append("  [budget spent]\n"); return; }

				string ftn = typeNames[mem.Key];
				var f = mem;

				object val = null;
				try { val = f.Value(area); } catch { }

				int count = -1;
				Type vt = TypeOf(val);
				try
				{
					var cp = vt?.GetProperty("Count") ?? vt?.GetProperty("Length");
					if (cp != null && val != null) count = Convert.ToInt32(cp.GetValue(val));
				}
				catch { }

				sb.Append("\n  member ").Append(ftn).Append("  ").Append(f.Key)
				  .Append("   count=").Append(count).Append('\n');

				// THE TEMPLATE. One real entry, field by field, values included — this is the
				// shape a synthetic entry has to match.
				if (val == null || count <= 0 || vt == null) continue;
				try
				{
					var item = vt.GetProperty("Item");
					if (item == null) continue;
					object first = item.GetValue(val, new object[] { 0 });
					Type et = TypeOf(first);
					sb.Append("    [0] ").Append(et?.FullName ?? "?").Append('\n');
					if (et == null) continue;

					int n = 0;
					foreach (var mf in et.GetFields(F))
					{
						if (n++ > 40 || Spent) break;
						sb.Append("        field ").Append(mf.FieldType.Name).Append("  ")
						  .Append(mf.Name).Append(" = ").Append(Val(mf, first)).Append('\n');
					}
					foreach (var mp in et.GetProperties(F))
					{
						if (n++ > 60 || Spent) break;
						if (mp.GetIndexParameters().Length > 0) continue;
						sb.Append("        prop  ").Append(mp.PropertyType.Name).Append("  ")
						  .Append(mp.Name).Append(" = ").Append(Val(mp, first)).Append('\n');
					}
				}
				catch (Exception e) { sb.Append("    first entry threw: ").Append(e.Message).Append('\n'); }
			}
		}

		// ------------------------------------------------------------------ udon

		private static void Udon(System.Text.StringBuilder sb)
		{
			Type ub = FindType("VRC.Udon.UdonBehaviour");
			if (ub == null) { sb.Append("  VRC.Udon.UdonBehaviour not found\n"); return; }
			sb.Append("  UdonBehaviour: ").Append(ub.FullName).Append('\n');

			// The real network API, so the manager can stop guessing from event names.
			sb.Append("\n  -- members mentioning Network / Event / Program --\n");
			const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
				| BindingFlags.Instance | BindingFlags.Static;
			int shown = 0;
			foreach (var m in ub.GetMethods(All))
			{
				if (shown > 40 || Spent) break;
				string n = m.Name;
				if (!Interesting(n)) continue;
				var ps = m.GetParameters();
				var sig = new System.Text.StringBuilder();
				for (int i = 0; i < ps.Length; i++) { if (i > 0) sig.Append(", "); sig.Append(ps[i].ParameterType.Name); }
				sb.Append("    m  ").Append(m.ReturnType.Name).Append("  ").Append(n)
				  .Append('(').Append(sig).Append(")\n");
				shown++;
			}
			foreach (var f in ub.GetFields(All))
			{
				if (shown > 60 || Spent) break;
				if (!Interesting(f.Name)) continue;
				sb.Append("    f  ").Append(f.FieldType.Name).Append("  ").Append(f.Name).Append('\n');
				shown++;
			}

			// A few live world behaviours, with the FACTUAL verdict per entry point.
			sb.Append("\n  -- live world behaviours --\n");
			int done = 0, skipped = 0;
			try
			{
				var found = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(ub));
				if (found == null) { sb.Append("    none\n"); return; }

				var metaByName = ub.GetMethod("GetNetworkCallingMetadata", new[] { typeof(string) });
				sb.Append("    GetNetworkCallingMetadata(string): ")
				  .Append(metaByName != null ? "present" : "ABSENT").Append('\n');

				for (int i = 0; i < found.Length && done < 5; i++)
				{
					if (Spent) { sb.Append("    [budget spent]\n"); return; }

					var b = found[i]?.TryCast<Behaviour>();
					if (b == null) continue;
					try { if (!b.gameObject.scene.IsValid() || !b.gameObject.scene.isLoaded) continue; } catch { continue; }

					string path = PathOf(b.transform);
					if (path.IndexOf("VRCPlayer", StringComparison.Ordinal) >= 0) continue;

					// KEEP LOOKING UNTIL WE FIND ONES WITH A PROGRAM. The first run reported
					// "(no program)" five times and stopped: the first five behaviours in memory
					// happened to be LatencyRecord objects that carry none, and they used up the
					// whole sample. An empty behaviour must not count against the quota.
					object prog = null;
					try { prog = ub.GetMethod("GetPrograms")?.Invoke(b, null); } catch { }
					if (prog == null) { skipped++; continue; }

					done++;
					sb.Append("\n    === ").Append(Trunc(path, 110)).Append('\n');

					Type pt = TypeOf(prog);
					sb.Append("        GetPrograms -> ").Append(pt?.FullName ?? "?").Append('\n');
					if (pt == null) continue;

					try
					{
						var lenP = pt.GetProperty("Length") ?? pt.GetProperty("Count");
						var item = pt.GetProperty("Item");
						if (lenP == null || item == null) { sb.Append("        (list shape unknown)\n"); continue; }

						int len = Convert.ToInt32(lenP.GetValue(prog));
						sb.Append("        entry points: ").Append(len).Append('\n');
						for (int k = 0; k < len && k < 30; k++)
						{
							if (Spent) break;
							string name = item.GetValue(prog, new object[] { k }) as string;
							if (string.IsNullOrEmpty(name)) continue;

							string verdict = "(no metadata api)";
							if (metaByName != null)
							{
								try
								{
									object meta = metaByName.Invoke(b, new object[] { name });
									verdict = meta == null ? "null -> LOCAL" : ("NON-NULL -> " + meta);
								}
								catch (Exception e) { verdict = "threw: " + e.Message; }
							}
							sb.Append("          ").Append(name).Append("   ").Append(verdict).Append('\n');
						}
					}
					catch (Exception e) { sb.Append("        entry points threw: ").Append(e.Message).Append('\n'); }
				}
			}
			catch (Exception e) { sb.Append("    scan threw: ").Append(e.Message).Append('\n'); }
		}

		private static bool Interesting(string n)
			=> n.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0
			|| n.IndexOf("EntryPoint", StringComparison.OrdinalIgnoreCase) >= 0
			|| n.IndexOf("Program", StringComparison.OrdinalIgnoreCase) >= 0;

		// ------------------------------------------------------------------ helpers

		// UniverseLib when UnityExplorer is loaded — it names the MANAGED wrapper type, which is a
		// legal C# identifier that can be pasted into source. Plain GetType() answers with the
		// static type and is useless here.
		private static Type TypeOf(object o)
		{
			if (o == null) return null;
			Type t = MenuPanelProbe.ActualTypeOfPublic(o);
			if (t != null) return t;
			try { return o.GetType(); } catch { return null; }
		}

		private static string Val(FieldInfo f, object on)
		{
			try { return Trunc(f.GetValue(on)?.ToString() ?? "null", 70); }
			catch (Exception e) { return "<" + e.GetType().Name + ">"; }
		}

		private static string Val(PropertyInfo p, object on)
		{
			try { return Trunc(p.GetValue(on)?.ToString() ?? "null", 70); }
			catch (Exception e) { return "<" + e.GetType().Name + ">"; }
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = null;
				try { t = asm.GetType(full, false); } catch { }
				if (t != null) return t;
			}
			return null;
		}

		private static string PathOf(Transform t)
		{
			var sb = new System.Text.StringBuilder(96);
			try
			{
				var stack = new List<string>();
				for (Transform p = t; p != null; p = p.parent) stack.Add(p.name);
				for (int i = stack.Count - 1; i >= 0; i--) { sb.Append(stack[i]); if (i > 0) sb.Append('/'); }
			}
			catch { }
			return sb.ToString();
		}

		private static string Trunc(string s, int n)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");
	}
}
