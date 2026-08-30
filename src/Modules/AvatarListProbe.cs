using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// AVATAR LIST PROBE — the one thing that has to happen before any native-injection code exists.
	//
	// Every method body in the Il2Cpp interop assemblies is a native-invoke shim, so NOTHING about
	// how the avatar menu behaves can be settled by reading them. Static analysis got us as far as
	// "MonoBehaviour1PublicGr_lGa_pCa_fILBoInBoUnique is the seam"; everything after that — what its
	// item list actually holds, which obfuscated property carries an avatar id, whether our list
	// would be truncated — can only be answered by looking at a running game.
	//
	// So this looks, and only looks. It constructs nothing, calls no setter, and touches no native
	// collection. Property GETTERS are read on objects VRChat already built and is already
	// displaying, which is exactly what the UI itself does every frame.
	public static class AvatarListProbe
	{
		public static string Run()
		{
			var sb = new StringBuilder(96 * 1024);
			sb.Append("AVATAR LIST PROBE — read-only\n");
			sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
			sb.Append(new string('=', 100)).Append("\n\n");

			Section(sb, "OUR OWN CARDS (icon/label geometry)", OurCards);
			Section(sb, "LIVE AvatarContentSection INSTANCES", LiveSections);
			Section(sb, "IAvatar SHAPE (read off VRChat's OWN populated list)", ItemShape);
			Section(sb, "DataModel<ApiAvatar> CANDIDATES", Candidates);
			Section(sb, "FAVOURITES API SURFACE", FavouritesSurface);
			Section(sb, "LIVE FavoriteArea (VRC.Core.API.Favorites)", LiveFavorites);
			Section(sb, "AVATAR MENU TREE (sidebar rows + the star button)", MenuTree);

			sb.Append("\nRead-only: nothing was created, modified or sent.\n");
			return sb.ToString();
		}

		private static void Section(StringBuilder sb, string title, Action<StringBuilder> body)
		{
			sb.Append("######## ").Append(title).Append('\n');
			try { body(sb); }
			catch (Exception e) { sb.Append("  !! threw: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n'); }
			sb.Append('\n');
		}

		// ------------------------------------------------------------------ sections

		// Held across calls so ItemShape can look at the same objects LiveSections found.
		private static readonly List<object> Found = new List<object>();

		private static void LiveSections(StringBuilder sb)
		{
			Found.Clear();
			Type t = FindType("MonoBehaviour1PublicGr_lGa_pCa_fILBoInBoUnique");
			if (t == null) { sb.Append("  AvatarContentSection: TYPE NOT FOUND — the seam is gone, stop here.\n"); return; }
			sb.Append("  type      : ").Append(t.FullName).Append("\n  assembly  : ")
			  .Append(t.Assembly.GetName().Name).Append("\n  base      : ")
			  .Append(t.BaseType != null ? t.BaseType.FullName : "(none)").Append("\n\n");

			var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(t));
			int n = arr != null ? arr.Length : 0;
			sb.Append("  live instances: ").Append(n).Append('\n');
			if (n == 0)
			{
				sb.Append("  >>> none. Open the AVATARS page in the big menu, THEN run this dump.\n");
				return;
			}

			for (int i = 0; i < n && i < 12; i++)
			{
				// CAST FIRST. Resources hands back objects typed as UnityEngine.Object; reflecting
				// AvatarContentSection's members against that proxy throws TargetException on every
				// single one — which is exactly what the first run of this probe did, and it made an
				// unreadable list look like an EMPTY list.
				object inst = null;
				try { inst = AsType(arr[i], t); } catch { }
				if (inst == null) continue;
				Found.Add(inst);

				sb.Append("\n  --- [").Append(i).Append("] ");
				var comp = arr[i].TryCast<Component>();
				if (comp != null)
				{
					sb.Append(PathOf(comp.transform));
					try { sb.Append(comp.gameObject.activeInHierarchy ? "   [active]" : "   [off]"); } catch { }
				}
				sb.Append('\n');

				// Every field and property, declared and inherited. The readable ones are the
				// contract we can code against; the obfuscated ones are what we have to identify
				// by their VALUES, which is why the value is printed and not just the type.
				DumpMembers(sb, t, inst, "      ");
			}
		}

		private static void ItemShape(StringBuilder sb)
		{
			if (Found.Count == 0) { sb.Append("  (no live section — nothing to read)\n"); return; }

			foreach (var inst in Found)
			{
				// Il2CppInterop exposes native FIELDS as C# PROPERTIES, so the property lookup has to
				// come first — going straight to GetField finds nothing.
				object list = null;
				string via = "";
				foreach (string fname in new[] { "field_Private_IList_0", "_listBinding" })
				{
					object v = Member(inst, fname);
					if (v == null) continue;
					v = Concrete(v);          // the field's static type is an INTERFACE proxy
					if (CountOf(v) >= 0) { list = v; via = fname; break; }
					if (list == null) { list = v; via = fname; }
				}
				if (list == null) continue;

				int count = -1;
				try
				{
					var cp = list.GetType().GetProperty("Count");
					if (cp != null) count = Convert.ToInt32(cp.GetValue(list));
				}
				catch { }
				sb.Append("  ").Append(via).Append(" -> ").Append(list.GetType().FullName)
				  .Append("   Count=").Append(count).Append('\n');
				if (count <= 0) continue;

				var getItem = list.GetType().GetMethod("get_Item", new[] { typeof(int) });
				if (getItem == null) { sb.Append("    (no get_Item(int) — cannot enumerate)\n"); continue; }

				// Three is enough to tell which property is which, and keeps the dump readable.
				for (int k = 0; k < count && k < 3; k++)
				{
					object item = null;
					try { item = getItem.Invoke(list, new object[] { k }); } catch { }
					if (item == null) continue;

					string cls = "?";
					try { if (item is Il2CppObjectBase ob) cls = MenuCard.Il2CppNameOf(ob); } catch { }
					sb.Append("\n    item[").Append(k).Append("] runtime class: ").Append(cls).Append('\n');

					// Re-wrap through the proxy for that runtime class, otherwise the item is only
					// an Il2CppSystem.Object and none of its real properties are reachable.
					object proxy = Reproxy(item, cls);
					if (proxy == null) { sb.Append("      (no managed proxy for that class)\n"); continue; }
					sb.Append("      proxy: ").Append(proxy.GetType().FullName).Append('\n');
					DumpValues(sb, proxy, "      ");
				}
				sb.Append('\n');
				return;    // one populated list is all we need
			}
			sb.Append("  (found sections, but none had a populated item list — open a category with avatars in it)\n");
		}

		// The plan's structural selector was wrong twice, so this prints EVERY candidate with its
		// assembly instead of asserting one:
		//   * ApiAvatar lives in VRCCore-Standalone, NOT Assembly-CSharp, so scanning
		//     typeof(ApiAvatar).Assembly looks in the wrong place
		//   * ApiAvatar : ApiContentModel<ApiAvatar> is CRTP, so "base generic over ApiAvatar"
		//     matches ApiAvatar itself
		private static void Candidates(StringBuilder sb)
		{
			Type api = FindType("VRC.Core.ApiAvatar");
			if (api == null) { sb.Append("  VRC.Core.ApiAvatar NOT FOUND\n"); return; }
			sb.Append("  VRC.Core.ApiAvatar in ").Append(api.Assembly.GetName().Name)
			  .Append("   base=").Append(api.BaseType != null ? api.BaseType.FullName : "(none)").Append("\n\n");

			int hits = 0;
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				foreach (var t in SafeTypes(asm))
				{
					if (t == null || t == api) continue;              // CRTP self-match
					Type b = null;
					try { b = t.BaseType; } catch { }
					if (b == null || !b.IsGenericType) continue;
					Type[] ga = null;
					try { ga = b.GetGenericArguments(); } catch { }
					if (ga == null || ga.Length == 0 || ga[0] != api) continue;

					hits++;
					sb.Append("  ").Append(t.FullName).Append("\n      assembly : ")
					  .Append(asm.GetName().Name).Append("\n      base     : ").Append(b.FullName)
					  .Append("\n      abstract : ").Append(t.IsAbstract)
					  .Append("   public ctor(): ").Append(t.GetConstructor(Type.EmptyTypes) != null).Append('\n');

					// The one-ApiAvatar-parameter setter the plan wants to call.
					foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
					{
						var ps = m.GetParameters();
						if (ps.Length == 1 && ps[0].ParameterType == api && m.ReturnType == typeof(void))
							sb.Append("      setter   : ").Append(m.Name).Append("(ApiAvatar)\n");
					}
				}
			}
			if (hits == 0) sb.Append("  (no type derives from a base generic over ApiAvatar)\n");
			sb.Append("\n  total candidates: ").Append(hits).Append('\n');

			Type iav = FindType("VRC.DataModel.IAvatar");
			sb.Append("  VRC.DataModel.IAvatar: ").Append(iav == null ? "NOT FOUND" : iav.Assembly.GetName().Name).Append('\n');
		}

		private static void FavouritesSurface(StringBuilder sb)
		{
			foreach (string name in new[] { "VRC.Core.API", "VRC.Core.FavoriteArea", "VRC.Core.FavoriteListModel", "VRC.Core.FavoriteModel" })
			{
				Type t = FindType(name);
				sb.Append("  ").Append(name).Append(": ")
				  .Append(t == null ? "NOT FOUND" : t.Assembly.GetName().Name).Append('\n');
				if (t == null) continue;

				foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
				{
					object v = "(threw)";
					try { v = p.GetValue(null); } catch { }
					sb.Append("      static prop ").Append(p.Name).Append(" : ").Append(p.PropertyType.Name)
					  .Append("  = ").Append(v == null ? "null" : "<live>").Append('\n');
				}
				foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
					sb.Append("      static field ").Append(f.Name).Append(" : ").Append(f.FieldType.Name).Append('\n');
				foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
					sb.Append("      static method ").Append(m.Name).Append('\n');
			}
		}

		// The earlier live scan concluded there was "no static route" to the favourites store. That
		// was wrong: VRC.Core.API.Favorites is a live static property returning a FavoriteArea. It
		// was invisible to a Resources scan only because FavoriteArea is not a Component.
		private static void LiveFavorites(StringBuilder sb)
		{
			Type api = FindType("VRC.Core.API");
			if (api == null) { sb.Append("  VRC.Core.API not found\n"); return; }
			var p = api.GetProperty("Favorites", BindingFlags.Public | BindingFlags.Static);
			if (p == null) { sb.Append("  no static Favorites property\n"); return; }

			object area = null;
			try { area = p.GetValue(null); } catch (Exception e) { sb.Append("  getter threw: ").Append(e.Message).Append('\n'); }
			if (area == null) { sb.Append("  Favorites == null (not fetched yet?)\n"); return; }
			sb.Append("  FavoriteArea: ").Append(area.GetType().FullName).Append("\n\n");

			foreach (string listField in new[] { "_avatars", "_worlds", "_friends", "_vrcPlusWorlds" })
			{
				object lst = Member(area, listField);
				int c = CountOf(lst);
				sb.Append("  ").Append(listField).Append(" : ")
				  .Append(lst == null ? "null" : lst.GetType().Name).Append("  Count=").Append(c).Append('\n');
				if (lst == null || c <= 0 || listField != "_avatars") continue;

				var gi = lst.GetType().GetMethod("get_Item", new[] { typeof(int) });
				if (gi == null) continue;
				for (int k = 0; k < c && k < 8; k++)
				{
					object m = null;
					try { m = gi.Invoke(lst, new object[] { k }); } catch { }
					if (m == null) continue;
					sb.Append("      [").Append(k).Append("] ").Append(m.GetType().Name).Append('\n');
					DumpValues(sb, m, "        ");
				}
			}
		}

		// The sidebar rows ("avatars1", "VRC+ Favorites 2"...) and the star button in the detail
		// pane both live in this subtree, and both are targets: one for a VRCHAT ARCHIVE FAV
		// category, one for an ADD TO ARCHIVE FAVORITES button next to VRChat's own star.
		private static void MenuTree(StringBuilder sb)
		{
			Transform root = null;
			foreach (var inst in Found)
			{
				try
				{
					var c = (inst as Il2CppObjectBase)?.TryCast<Component>();
					for (Transform up = c != null ? c.transform : null; up != null; up = up.parent)
						if ((up.name ?? "").StartsWith("Menu_MM_Avatars", StringComparison.Ordinal)) { root = up; break; }
				}
				catch { }
				if (root != null && root.gameObject.activeInHierarchy) break;
			}
			if (root == null) { sb.Append("  Menu_MM_Avatars not reachable — open the AVATARS page first\n"); return; }

			sb.Append("  root: ").Append(PathOf(root)).Append("\n\n");
			Walk(sb, root, 0);
		}

		private static void Walk(StringBuilder sb, Transform t, int depth)
		{
			if (t == null || depth > 12) return;
			try
			{
				sb.Append(new string(' ', 2 + depth * 2)).Append(t.name);
				var comps = t.GetComponents<Component>();
				if (comps != null && comps.Length > 0)
				{
					sb.Append("  [");
					bool first = true;
					foreach (var c in comps)
					{
						if (c == null) continue;
						if (!first) sb.Append(", ");
						first = false;
						sb.Append(MenuCard.Il2CppNameOf(c));
					}
					sb.Append(']');
				}
				try
				{
					var tmp = t.GetComponent<TMPro.TMP_Text>();
					if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
						sb.Append("  \"").Append(tmp.text.Replace('\n', ' ')).Append('"');
				}
				catch { }
				if (!t.gameObject.activeInHierarchy) sb.Append("  [off]");
				sb.Append('\n');
			}
			catch { }
			for (int i = 0; i < t.childCount; i++)
			{
				try { Walk(sb, t.GetChild(i), depth + 1); } catch { }
			}
		}

		// ------------------------------------------------------------------ helpers

		// Property first, then field: Il2CppInterop surfaces native fields as properties.
		private static object Member(object inst, string name)
		{
			const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
			try
			{
				var p = inst.GetType().GetProperty(name, F);
				if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(inst);
			}
			catch { }
			try
			{
				var f = inst.GetType().GetField(name, F);
				if (f != null) return f.GetValue(inst);
			}
			catch { }
			return null;
		}

		private static int CountOf(object lst)
		{
			if (lst == null) return -1;
			try
			{
				var cp = lst.GetType().GetProperty("Count");
				if (cp != null) return Convert.ToInt32(cp.GetValue(lst));
			}
			catch { }
			// The static type is often the INTERFACE proxy (Il2CppSystem.Collections.IList), which
			// carries no Count property — same trap as reflecting a component off UnityEngine.Object.
			try
			{
				var gm = lst.GetType().GetMethod("get_Count", Type.EmptyTypes);
				if (gm != null) return Convert.ToInt32(gm.Invoke(lst, null));
			}
			catch { }
			return -1;
		}

		// Re-wrap through the CONCRETE runtime class, so Count/get_Item exist.
		private static object Concrete(object o)
		{
			try
			{
				if (!(o is Il2CppObjectBase ob)) return o;
				string cls = MenuCard.Il2CppNameOf(ob);
				object p = Reproxy(o, cls);
				return p ?? o;
			}
			catch { return o; }
		}

		// OUR cards, measured rather than assumed. Two attempts to place the icon and the label
		// changed nothing on screen, and a screenshot cannot say whether the code ran at all.
		private static void OurCards(StringBuilder sb)
		{
			Transform grid = null;
			try
			{
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.name != "Buttons_Archive") continue;
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
					grid = t; break;
				}
			}
			catch { }
			if (grid == null) { sb.Append("  Buttons_Archive not found — open the Archive tab in the QuickMenu first\n"); return; }

			sb.Append("  grid: ").Append(PathOf(grid)).Append('\n');
			try
			{
				var g = grid.GetComponent<UnityEngine.UI.GridLayoutGroup>();
				if (g != null) sb.Append("  cellSize=").Append(g.cellSize.x).Append('x').Append(g.cellSize.y)
					.Append("  spacing=").Append(g.spacing.x).Append(',').Append(g.spacing.y).Append('\n');
				else sb.Append("  (no GridLayoutGroup on the grid!)\n");
			}
			catch { }

			for (int i = 0; i < grid.childCount && i < 12; i++)
			{
				var card = grid.GetChild(i);
				if (card == null) continue;
				string label = "";
				try
				{
					var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (tmp != null) label = tmp.text;
				}
				catch { }
				sb.Append("\n  [").Append(i).Append("] ").Append(card.name).Append("  \"").Append(label).Append("\"\n");
				Rt(sb, "card ", card?.TryCast<RectTransform>());
				Rt(sb, "Icons", card.Find("Icons")?.TryCast<RectTransform>());
				Rt(sb, "Icon ", card.Find("Icons/Icon")?.TryCast<RectTransform>());
				try
				{
					var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (tmp != null)
					{
						Rt(sb, "TextP", tmp.transform.parent?.TryCast<RectTransform>());
						Rt(sb, "Text ", tmp.transform?.TryCast<RectTransform>());
					}
				}
				catch { }
				try
				{
					var vlg = card.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
					sb.Append("        VerticalLayoutGroup on card: ").Append(vlg != null ? "YES" : "no").Append('\n');
				}
				catch { }
			}
		}

		private static void Rt(StringBuilder sb, string tag, RectTransform rt)
		{
			if (rt == null) { sb.Append("        ").Append(tag).Append(" : MISSING\n"); return; }
			try
			{
				bool ign = false;
				var le = rt.GetComponent<UnityEngine.UI.LayoutElement>();
				if (le != null) ign = le.ignoreLayout;
				sb.Append("        ").Append(tag)
				  .Append(" rect=").Append(rt.rect.width.ToString("F0")).Append('x').Append(rt.rect.height.ToString("F0"))
				  .Append("  pos=").Append(rt.anchoredPosition.x.ToString("F0")).Append(',').Append(rt.anchoredPosition.y.ToString("F0"))
				  .Append("  size=").Append(rt.sizeDelta.x.ToString("F0")).Append('x').Append(rt.sizeDelta.y.ToString("F0"))
				  .Append("  anch=").Append(rt.anchorMin.x.ToString("F2")).Append(',').Append(rt.anchorMin.y.ToString("F2"))
				  .Append('-').Append(rt.anchorMax.x.ToString("F2")).Append(',').Append(rt.anchorMax.y.ToString("F2"))
				  .Append("  piv=").Append(rt.pivot.x.ToString("F2")).Append(',').Append(rt.pivot.y.ToString("F2"))
				  .Append("  LE=").Append(le == null ? "none" : (ign ? "ignoreLayout" : "inLayout"))
				  .Append('\n');
			}
			catch (Exception e) { sb.Append("        ").Append(tag).Append(" : threw ").Append(e.Message).Append('\n'); }
		}

		// Re-wrap an Il2Cpp object as a specific proxy type, through the (IntPtr) constructor every
		// Il2CppInterop proxy has.
		private static object AsType(object o, Type t)
		{
			if (!(o is Il2CppObjectBase ob) || t == null) return null;
			var ctor = t.GetConstructor(new[] { typeof(IntPtr) });
			if (ctor == null) return null;
			try { return ctor.Invoke(new object[] { ob.Pointer }); }
			catch { return null; }
		}

		private static void DumpMembers(StringBuilder sb, Type t, object inst, string pad)
		{
			foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				string v = Safe(() => f.GetValue(inst));
				sb.Append(pad).Append("field ").Append(f.Name).Append(" : ").Append(Short(f.FieldType))
				  .Append("  = ").Append(v).Append('\n');
			}
			foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				if (p.GetIndexParameters().Length > 0) continue;
				string v = Safe(() => p.GetValue(inst));
				sb.Append(pad).Append("prop  ").Append(p.Name).Append(" : ").Append(Short(p.PropertyType))
				  .Append("  = ").Append(v).Append('\n');
			}
		}

		// Values only — this is what identifies an obfuscated property: prop_String_3 means nothing,
		// prop_String_3 = "avtr_1234..." means everything.
		private static void DumpValues(StringBuilder sb, object inst, string pad)
		{
			foreach (var p in inst.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (p.GetIndexParameters().Length > 0) continue;
				Type pt = p.PropertyType;
				if (pt != typeof(string) && pt != typeof(bool) && pt != typeof(int) && pt != typeof(float)) continue;
				string v = Safe(() => p.GetValue(inst));
				if (v == "null" || v == "") continue;
				sb.Append(pad).Append("  ").Append(p.Name).Append(" = ").Append(v).Append('\n');
			}
		}

		private static object Reproxy(object item, string runtimeClass)
		{
			try
			{
				if (!(item is Il2CppObjectBase ob)) return null;
				if (string.IsNullOrEmpty(runtimeClass) || runtimeClass == "?") return null;
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					foreach (var t in SafeTypes(asm))
					{
						if (t == null || t.Name != runtimeClass) continue;
						var ctor = t.GetConstructor(new[] { typeof(IntPtr) });
						if (ctor == null) continue;
						try { return ctor.Invoke(new object[] { ob.Pointer }); } catch { }
					}
				}
			}
			catch { }
			return null;
		}

		private static string Safe(Func<object> get)
		{
			try
			{
				object v = get();
				if (v == null) return "null";
				if (v is string s) return "\"" + (s.Length > 120 ? s.Substring(0, 120) + "…" : s) + "\"";
				if (v is UnityEngine.Object uo) { try { return uo.name; } catch { return "<UnityObject>"; } }
				if (v is bool || v is int || v is float || v is double) return v.ToString();
				return "<" + v.GetType().Name + ">";
			}
			catch (Exception e) { return "(threw " + e.GetType().Name + ")"; }
		}

		private static string Short(Type t)
		{
			try { return t == null ? "?" : t.Name; }
			catch { return "?"; }
		}

		private static IEnumerable<Type> SafeTypes(Assembly asm)
		{
			try { return asm.GetTypes(); }
			catch (ReflectionTypeLoadException ex) { return ex.Types ?? new Type[0]; }
			catch { return new Type[0]; }
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
			var sb = new StringBuilder(128);
			try
			{
				var stack = new List<string>();
				for (Transform p = t; p != null; p = p.parent) stack.Add(p.name);
				for (int i = stack.Count - 1; i >= 0; i--) { sb.Append(stack[i]); if (i > 0) sb.Append('/'); }
			}
			catch { }
			return sb.ToString();
		}
	}
}
