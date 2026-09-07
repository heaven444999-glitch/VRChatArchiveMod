using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// READ VRCHAT'S MENU AS IT IS TODAY, instead of guessing at it.
	//
	// Why this exists. Our UI is built by hand — new GameObject, new RectTransform, our own sprites —
	// and two injections fail on every single world load:
	//     [ArchiveHijack] still cannot find the avatar-menu panel (FindPanel returned null)
	//     [UserMenu] not injected: no per-user page under Body
	// Clients whose menus look native do the opposite: they take a REAL VRChat button and
	// Object.Instantiate it, then set its text, icon and tooltip through the sub-objects the clone
	// already has. A clone inherits the font, the material, the hover animation and the click sound
	// for free, and it keeps inheriting them when VRChat restyles its menu.
	//
	// The catch is that cloning needs the PATH of the thing to clone, and paths move. A 2022-era
	// reference client reads "UserInterface/QuickMenu/ShortcutMenu/WorldsButton"; this build's
	// QuickMenu is "Canvas_QuickMenu(Clone)/CanvasGroup/Container/Window" (Core/QuickMenu.cs), so
	// every one of those paths is already wrong. Porting them blind would reproduce exactly the
	// silent failure we already have.
	//
	// So: dump the tree, read it, then write the clone code against names that were observed rather
	// than remembered. Read-only — it creates nothing, touches nothing, and only runs when asked.
	public class UiTreeDumpModule : IModule
	{
		public override string Name => "UiTreeDump";

		public static string Status = "";

		private const int MaxDepth = 14;
		private const int MaxNodes = 40000;   // a freeze would be worse than a truncated file

		private static bool _pending;

		/// <summary>Asked for by the client's DUMP MENU TREE action. The walk itself happens on the
		/// next Update: Unity's transform hierarchy may only be read from the main thread.</summary>
		public static void Request()
		{
			_pending = true;
			Status = "menu tree: queued…";
		}

		public override void OnUpdate()
		{
			if (!_pending) return;
			_pending = false;
			try { Status = Dump(); }
			catch (Exception e)
			{
				Status = "menu tree failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[UiTree] " + Status);
			}
		}

		private static string Dump()
		{
			var sb = new StringBuilder(1 << 20);
			sb.Append("VRCHAT MENU TREE — dumped ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
			sb.Append("Read this to find what to Object.Instantiate: a real button to clone, and the\n");
			sb.Append("sub-objects that carry its label, icon and background.\n\n");

			int nodes = 0;
			Walk(sb, "QUICK MENU", QuickMenu.Root(), ref nodes);
			Walk(sb, "MAIN MENU", QuickMenu.Main(), ref nodes);

			// THE HUD, because that is where the panels we are copying actually live. A reference
			// client's floating player list is not in the QuickMenu at all: it clones a HUD image
			// (UserInterface/UnscaledUI/HudContent/Hud/NotificationDotParent/NotificationDot) and hangs
			// a Text off it. HudContent exists on this build; NotificationDotParent does not — which is
			// exactly the kind of thing only a dump can tell us.
			Walk(sb, "HUD", FindHud(), ref nodes);

			// The wings called out on their own: they are what a side panel attaches beside, and
			// hunting them inside a 40 000-line tree is what this section exists to avoid.
			sb.Append("\n\n==== WINGS ====\n");
			WingReport(sb, MenuDonor.Wing(true), "LEFT");
			WingReport(sb, MenuDonor.Wing(false), "RIGHT");

			// The interesting parts, called out so they do not have to be hunted for in 40 000 lines.
			sb.Append("\n\n==== CANDIDATE BUTTON TEMPLATES (nodes whose name suggests a button) ====\n");
			var seen = new HashSet<string>(StringComparer.Ordinal);
			Candidates(sb, QuickMenu.Root(), "", seen, 0);
			Candidates(sb, QuickMenu.Main(), "", seen, 0);

			string path;
			try { path = Path.Combine(Directory.GetCurrentDirectory(), "VRChatArchive-ui-tree.txt"); }
			catch { path = "VRChatArchive-ui-tree.txt"; }
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

			string msg = "menu tree written: " + path + " (" + nodes + " nodes)";
			VRChatArchiveModPlugin.Logger.LogInfo("[UiTree] " + msg);
			return msg;
		}

		private static void Walk(StringBuilder sb, string title, Transform root, ref int nodes)
		{
			sb.Append("\n==== ").Append(title).Append(" ====\n");
			if (root == null) { sb.Append("(not found)\n"); return; }
			sb.Append("root: ").Append(FullPath(root)).Append('\n');
			Node(sb, root, 0, ref nodes);
		}

		// The HUD lives outside both menus, so neither QuickMenu.Root() nor Main() reaches it.
		private static Transform FindHud()
		{
			foreach (string p in new[]
			{
				"UserInterface/UnscaledUI/HudContent/Hud",
				"UserInterface/UnscaledUI/HudContent",
				"UserInterface/UnscaledUI",
			})
			{
				try
				{
					var go = GameObject.Find(p);
					if (go != null) return go.transform;
				}
				catch { }
			}
			return null;
		}

		private static void WingReport(StringBuilder sb, Transform wing, string side)
		{
			sb.Append("\n-- ").Append(side).Append(" --\n");
			if (wing == null) { sb.Append("(not resolved by MenuDonor)\n"); return; }
			sb.Append("path: ").Append(FullPath(wing)).Append('\n');
			sb.Append("geometry: ").Append(Geometry(wing)).Append('\n');
			sb.Append("pages (direct children — one is shown at a time):\n");
			try
			{
				for (int i = 0; i < wing.childCount; i++)
				{
					Transform c = wing.GetChild(i);
					if (c == null) continue;
					sb.Append("   ").Append(c.name);
					if (!c.gameObject.activeSelf) sb.Append("  [inactive]");
					sb.Append("   ").Append(Geometry(c)).Append('\n');
				}
			}
			catch { }
		}

		// Size and anchoring, without which a dump cannot tell you where to PUT anything — the whole
		// reason the first side panel landed on top of VRChat's own rows.
		private static string Geometry(Transform t)
		{
			try
			{
				var rt = t as RectTransform ?? t.GetComponent<RectTransform>();
				if (rt == null) return "";
				Vector2 sz = rt.sizeDelta, ap = rt.anchoredPosition, an = rt.anchorMin, ax = rt.anchorMax, pv = rt.pivot;
				return "{" + (int)sz.x + "x" + (int)sz.y + " pos(" + (int)ap.x + "," + (int)ap.y + ")"
					+ " a(" + an.x.ToString("0.#") + "," + an.y.ToString("0.#") + "-" + ax.x.ToString("0.#") + "," + ax.y.ToString("0.#") + ")"
					+ " p(" + pv.x.ToString("0.#") + "," + pv.y.ToString("0.#") + ")}";
			}
			catch { return ""; }
		}

		private static void Node(StringBuilder sb, Transform t, int depth, ref int nodes)
		{
			if (t == null || depth > MaxDepth || nodes >= MaxNodes) return;
			nodes++;
			try
			{
				sb.Append(' ', depth * 2).Append(t.name);
				if (!t.gameObject.activeSelf) sb.Append("  [inactive]");
				string comps = Components(t);
				if (comps.Length > 0) sb.Append("   <").Append(comps).Append('>');
				string g = Geometry(t);
				if (g.Length > 0) sb.Append("  ").Append(g);
				sb.Append('\n');
			}
			catch { return; }

			int n;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++)
			{
				Transform c = null;
				try { c = t.GetChild(i); } catch { }
				if (c != null) Node(sb, c, depth + 1, ref nodes);
			}
		}

		// Only the components that matter for cloning: what makes a thing clickable, labelled, drawn.
		private static string Components(Transform t)
		{
			var wanted = new List<string>();
			try
			{
				foreach (var c in t.GetComponents<Component>())
				{
					if (c == null) continue;
					string n;
					try { n = c.GetIl2CppType().Name; } catch { continue; }
					if (n == "Transform" || n == "RectTransform") continue;
					wanted.Add(n);
					if (wanted.Count >= 8) break;
				}
			}
			catch { }
			return string.Join(", ", wanted.ToArray());
		}

		private static void Candidates(StringBuilder sb, Transform t, string prefix, HashSet<string> seen, int depth)
		{
			if (t == null || depth > MaxDepth) return;
			string path;
			try { path = prefix.Length == 0 ? t.name : prefix + "/" + t.name; } catch { return; }

			try
			{
				string n = t.name ?? "";
				bool looksClickable = n.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0
					|| n.IndexOf("Toggle", StringComparison.OrdinalIgnoreCase) >= 0
					|| n.IndexOf("Wing", StringComparison.OrdinalIgnoreCase) >= 0
					|| n.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) >= 0
					|| n.IndexOf("Page", StringComparison.OrdinalIgnoreCase) >= 0;
				if (looksClickable && seen.Add(path))
				{
					string comps = Components(t);
					sb.Append(path);
					if (comps.Length > 0) sb.Append("   <").Append(comps).Append('>');
					sb.Append('\n');
				}
			}
			catch { }

			int cn;
			try { cn = t.childCount; } catch { return; }
			for (int i = 0; i < cn; i++)
			{
				Transform c = null;
				try { c = t.GetChild(i); } catch { }
				if (c != null) Candidates(sb, c, path, seen, depth + 1);
			}
		}

		// NOT "Path": System.IO.Path is in scope here and the collision is a compile error.
		private static string FullPath(Transform t)
		{
			var parts = new List<string>();
			int guard = 0;
			try
			{
				while (t != null && guard++ < 40) { parts.Insert(0, t.name); t = t.parent; }
			}
			catch { }
			return string.Join("/", parts.ToArray());
		}
	}
}
