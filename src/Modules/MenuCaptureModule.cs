using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MENU CAPTURE — every VRChat menu page you open, recorded, automatically.
	//
	// WHY THIS EXISTS. Rebuilding VRChat's own sections (worlds, social) means cloning VRChat's own
	// cells, and every guess about which object that is has cost a round trip: the worlds grid was
	// rebuilt three times, and the social grid cloned a WORLD card because the one dump we had was
	// taken with the friends list closed. A dump is only as good as the pages that happened to be
	// open when it ran. So this stops taking one-off dumps and records CONTINUOUSLY: open a page,
	// it lands in a file, with the real component type names and the cell structure.
	//
	// SAFETY, learned the hard way — earlier probes froze and crashed the game three times:
	//   * NEVER Resources.FindObjectsOfTypeAll: walks every Transform in the game. Uses the cached
	//     main-menu canvas instead (Core.QuickMenu).
	//   * NEVER activate or touch anything. Read-only, always.
	//   * NEVER force il2cpp type resolution by name; ActualTypeOfPublic is the proven-safe route.
	//   * BOUNDED: node and depth caps, one page per frame, each page written at most twice.
	// It runs on its own with no hotkey — the point is that a normal play session produces the
	// captures without anyone remembering to press anything.
	public class MenuCaptureModule : IModule
	{
		public override string Name => "MenuCapture";

		// Pages worth recording: the big menu screens and the detail panels that open from them.
		private static readonly string[] Interesting =
		{
			"Menu_MM_Worlds", "Menu_Social", "Menu_MM_Avatars", "Menu_MM_Groups",
			"Menu_MM_WorldDetail", "Menu_MM_AvatarDetail", "Menu_MM_UserDetail", "Menu_MM_GroupDetail",
			"Menu_Settings", "Menu_MM_LiveNow", "Menu_MM_Shop", "Menu_MM_Marketplace",
		};

		private const int MaxNodes = 7000;     // per page dump
		private const int MaxDepth = 16;
		private const int MaxDumpsPerPage = 2; // one when it opens, one once it has filled in
		private const int MaxFiles = 40;       // a whole session, so a long play cannot fill the disk

		private sealed class Seen { public int Dumps; public int LastChildren; public float NextAllowed; }
		private static readonly Dictionary<string, Seen> Pages = new Dictionary<string, Seen>(StringComparer.Ordinal);
		private static int _files;
		private float _next;
		public static string Status = "idle";

		public override void OnSceneLoaded(int buildIndex) { Pages.Clear(); }

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.MenuCaptureEnabled.Value) return;
				if (_files >= MaxFiles) return;

				float now = Time.realtimeSinceStartup;
				if (now < _next) return;
				_next = now + 1f;                       // at most one check a second

				if (!Core.QuickMenu.MainVisible) return;
				var main = Core.QuickMenu.Main();
				if (main == null) return;

				// ONE page per tick, so a tick is never more than a single bounded walk.
				var page = FindOpenPage(main);
				if (page == null) return;

				string key = page.name;
				if (!Pages.TryGetValue(key, out var seen)) { seen = new Seen(); Pages[key] = seen; }
				if (seen.Dumps >= MaxDumpsPerPage) return;
				if (now < seen.NextAllowed) return;

				// WAIT FOR IT TO FILL IN. A page is created empty and populated a moment later by a
				// server fetch, so dumping the instant it opens captures an empty grid — which is
				// exactly the useless dump this module exists to replace. Only record once the child
				// count has stopped changing.
				int kids = CountDescendants(page, 3);
				if (kids != seen.LastChildren)
				{
					seen.LastChildren = kids;
					seen.NextAllowed = now + 2f;        // let it settle, look again
					return;
				}

				Dump(page, key, seen.Dumps + 1);
				seen.Dumps++;
				seen.NextAllowed = now + 12f;           // second pass much later, if it changed again
				seen.LastChildren = -1;
			}
			catch (Exception e) { Status = "failed: " + e.Message; }
		}

		// The active page under the main menu, by name. Scoped to the cached canvas — never a
		// whole-scene scan.
		private static Transform FindOpenPage(Transform main)
		{
			try
			{
				foreach (var t in main.GetComponentsInChildren<Transform>(false))   // active only
				{
					if (t == null) continue;
					string n; try { n = t.name; } catch { continue; }
					for (int i = 0; i < Interesting.Length; i++)
						if (n.IndexOf(Interesting[i], StringComparison.Ordinal) >= 0) return t;
				}
			}
			catch { }
			return null;
		}

		private static int CountDescendants(Transform t, int depth)
		{
			int n = 0;
			try
			{
				if (depth <= 0) return 0;
				for (int i = 0; i < t.childCount; i++)
				{
					var c = t.GetChild(i);
					if (c == null || !c.gameObject.activeSelf) continue;
					n += 1 + CountDescendants(c, depth - 1);
				}
			}
			catch { }
			return n;
		}

		// ------------------------------------------------------------------ the dump
		private static void Dump(Transform page, string key, int pass)
		{
			var sb = new StringBuilder(256 * 1024);
			sb.Append("VRCHAT ARCHIVE — MENU CAPTURE\n");
			sb.Append("page  : ").Append(key).Append("   (pass ").Append(pass).Append(")\n");
			sb.Append("path  : ").Append(PathOf(page)).Append('\n');
			sb.Append("time  : ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
			sb.Append("types : ").Append(MenuPanelProbe.HasActualType ? "REAL runtime names (UniverseLib)" : "raw proxy names").Append('\n');
			sb.Append(new string('=', 100)).Append('\n');

			int nodes = 0;
			var cells = new Dictionary<string, Transform>(StringComparer.Ordinal);
			Walk(sb, page, 0, ref nodes, cells);

			sb.Append('\n').Append(new string('=', 100)).Append('\n');
			sb.Append("CELL TEMPLATES — one example of each distinct cell, with what drives it.\n");
			sb.Append("These are the objects to CLONE, and the components to strip.\n");
			sb.Append(new string('=', 100)).Append('\n');
			foreach (var kv in cells) DumpCell(sb, kv.Key, kv.Value);

			sb.Append("\nnodes walked: ").Append(nodes).Append(nodes >= MaxNodes ? "  (CAPPED)" : "").Append('\n');

			try
			{
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "captures", "pages");
				Directory.CreateDirectory(dir);
				string safe = key.Replace("(", "").Replace(")", "").Replace(" ", "_");
				string file = Path.Combine(dir, safe + "_p" + pass + "_" + DateTime.Now.ToString("HH-mm-ss") + ".txt");
				File.WriteAllText(file, sb.ToString());
				_files++;
				Status = "captured " + key + " (" + nodes + " nodes)";
				VRChatArchiveModPlugin.Logger.LogInfo("[MenuCapture] " + key + " -> " + file + "  (" + nodes + " nodes, " + cells.Count + " cell type(s))");
			}
			catch (Exception e) { Status = "write failed: " + e.Message; }
		}

		private static void Walk(StringBuilder sb, Transform t, int depth, ref int nodes, Dictionary<string, Transform> cells)
		{
			if (t == null || depth > MaxDepth || nodes >= MaxNodes) return;
			nodes++;

			string name;
			try { name = t.name; } catch { return; }

			sb.Append(new string(' ', depth * 2)).Append(name);

			// Components with their REAL managed type names — the whole reason a capture is worth
			// more than a screenshot: an obfuscated name is still a stable identity to match on.
			try
			{
				var comps = t.GetComponents<Component>();
				if (comps != null && comps.Length > 0)
				{
					sb.Append("  [");
					bool first = true;
					for (int i = 0; i < comps.Length; i++)
					{
						var c = comps[i];
						if (c == null) continue;
						if (!first) sb.Append(", ");
						first = false;
						sb.Append(TypeName(c));
					}
					sb.Append(']');
				}
			}
			catch { }

			try
			{
				var rt = t as RectTransform;
				if (rt != null)
				{
					var r = rt.rect;
					sb.Append("  {").Append((int)r.width).Append('x').Append((int)r.height)
					  .Append(" pos(").Append((int)rt.anchoredPosition.x).Append(',').Append((int)rt.anchoredPosition.y).Append(")}");
				}
				if (!t.gameObject.activeSelf) sb.Append(" [off]");
			}
			catch { }

			// Any text on this node, quoted — it is what tells you WHICH child holds the label.
			try
			{
				var tmp = t.GetComponent<TMPro.TMP_Text>();
				if (tmp != null)
				{
					string s = tmp.text ?? "";
					if (s.Length > 40) s = s.Substring(0, 40) + "…";
					sb.Append("  text=\"").Append(s.Replace("\n", "\\n")).Append('"');
				}
			}
			catch { }

			sb.Append('\n');

			// Remember one example of each distinct cell/button family for the template section.
			try
			{
				if ((name.StartsWith("Cell_", StringComparison.Ordinal)
					|| name.StartsWith("Button_MM_", StringComparison.Ordinal))
					&& !cells.ContainsKey(BaseName(name)))
					cells[BaseName(name)] = t;
			}
			catch { }

			int n = 0;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++)
			{
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				Walk(sb, c, depth + 1, ref nodes, cells);
				if (nodes >= MaxNodes) return;
			}
		}

		// One cell, in detail: its components, whether it is clickable, and every descendant that
		// carries a label or an image — i.e. everything needed to clone it and drive it.
		private static void DumpCell(StringBuilder sb, string family, Transform cell)
		{
			try
			{
				sb.Append("\n---- ").Append(family).Append("   path: ").Append(PathOf(cell)).Append('\n');

				var comps = cell.GetComponents<Component>();
				if (comps != null)
				{
					sb.Append("     components on the ROOT (strip all but layout/graphic when cloning):\n");
					foreach (var c in comps)
					{
						if (c == null) continue;
						sb.Append("       + ").Append(TypeName(c));
						if (c is Button) sb.Append("   <-- Button (onClick)");
						if (c is Selectable && !(c is Button)) sb.Append("   <-- Selectable");
						sb.Append('\n');
					}
				}

				sb.Append("     children that carry content:\n");
				CellChildren(sb, cell, cell, 0);
			}
			catch (Exception e) { sb.Append("     (cell dump failed: ").Append(e.Message).Append(")\n"); }
		}

		private static void CellChildren(StringBuilder sb, Transform root, Transform t, int depth)
		{
			if (t == null || depth > 5) return;
			int n = 0;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++)
			{
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;

				string rel = RelPath(root, c);
				try
				{
					var tmp = c.GetComponent<TMPro.TMP_Text>();
					if (tmp != null)
					{
						string s = tmp.text ?? "";
						if (s.Length > 40) s = s.Substring(0, 40) + "…";
						sb.Append("       TEXT   ").Append(rel).Append("   = \"").Append(s.Replace("\n", "\\n")).Append("\"\n");
					}
					var raw = c.GetComponent<RawImage>();
					if (raw != null)
						sb.Append("       IMAGE  ").Append(rel).Append("   RawImage(").Append(TypeName(raw))
						  .Append(")  texture=").Append(raw.texture != null ? "set" : "null").Append('\n');
					else
					{
						var im = c.GetComponent<Image>();
						if (im != null)
							sb.Append("       IMAGE  ").Append(rel).Append("   Image(").Append(TypeName(im))
							  .Append(")  sprite=").Append(im.sprite != null ? "set" : "null").Append('\n');
					}
					var b = c.GetComponent<Button>();
					if (b != null) sb.Append("       BUTTON ").Append(rel).Append('\n');
				}
				catch { }
				CellChildren(sb, root, c, depth + 1);
			}
		}

		// ------------------------------------------------------------------ helpers
		private static string TypeName(Component c)
		{
			try
			{
				var t = MenuPanelProbe.ActualTypeOfPublic(c);
				if (t != null) return t.Name;
			}
			catch { }
			try { return c.GetType().Name; } catch { return "?"; }
		}

		// "Cell_MM_World(Clone)" and "Cell_MM_World" are the same family.
		private static string BaseName(string n)
		{
			int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
			return i > 0 ? n.Substring(0, i) : n;
		}

		private static string PathOf(Transform t)
		{
			try
			{
				var sb = new StringBuilder();
				var p = t;
				while (p != null) { sb.Insert(0, p.name); p = p.parent; if (p != null) sb.Insert(0, "/"); }
				return sb.ToString();
			}
			catch { return "?"; }
		}

		private static string RelPath(Transform root, Transform t)
		{
			try
			{
				var sb = new StringBuilder();
				var p = t;
				while (p != null && p != root) { sb.Insert(0, p.name); p = p.parent; if (p != null && p != root) sb.Insert(0, "/"); }
				return sb.ToString();
			}
			catch { return "?"; }
		}
	}
}
