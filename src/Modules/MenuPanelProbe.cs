using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MENU PANEL PROBE — dumps the live content panels of WHATEVER VRChat menu is open.
	//
	// The avatar favourites work needed the runtime type of the avatar panel, its category
	// observable and its content-section view — none of which can be read from the interop
	// assemblies, only off a running game. The same is true for the Worlds and Social menus, whose
	// architecture is DIFFERENT from avatars (no WorldContentSection, no IWorld).
	//
	// PERFORMANCE IS PART OF THE DESIGN HERE. The first version of this froze VRChat hard enough
	// that Windows offered to kill it, and the reason is worth writing down: it walked every
	// MonoBehaviour in the process (tens of thousands), and for each one it searched every loaded
	// assembly and every type inside it for a class of the right name. That is a product of two
	// huge numbers, run on the main thread, per object. Two rules keep it instant now:
	//
	//   1. SCOPE. Only the menu canvases are walked, not the whole process — the answer was never
	//      going to be inside an avatar's bones or a world's props.
	//   2. CACHE, AND FILTER FIRST. The name→Type map is built once, and a component is only
	//      re-proxied after its class name says it is worth looking at.
	//
	// Read-only: it constructs nothing and calls no setter — property getters only, on objects the
	// menu already built and is already showing.
	public static class MenuPanelProbe
	{
		// ONE PRESS, EVERY MENU — AND NOTHING IS TOUCHED.
		//
		// Capturing the Worlds page, then Social, then Avatars used to mean three trips into the
		// game and three presses, each easy to get wrong. The obvious fix — switch each page on,
		// read it, switch it back — CRASHED VRChat: waking a page runs its OnEnable, and the game's
		// menu controllers are not built to start out of sequence with the menu closed.
		//
		// It was also never necessary. A component exists on a disabled object, and its CLASS and
		// FIELDS are exactly what this probe is for; only live VALUES would need an opened page,
		// and those are not what names an injection anchor. So the sweep reads every page where it
		// lies, active or not, and changes nothing at all.
		public static string RunAll()
		{
			// DISABLED. Even without activating anything, sweeping thirty-seven pages walks every
			// transform under each of them — and the pages nest, so the same subtree is walked over
			// and over — with an il2cpp field read per component. That is minutes of main-thread
			// work, which the game experiences as a hang. The remaining information this was after
			// is obtained OFFLINE from the interop assemblies instead, where it costs the running
			// game nothing at all.
			return "The full sweep is disabled: it cost too much main-thread time and hung the game.\n"
				+ "Use F9 (single page probe) with a menu open, or read the interop assemblies offline.\n";

			var sb = new System.Text.StringBuilder(256 * 1024);
			sb.Append("MENU PANEL PROBE — FULL SWEEP. Every menu page, activated in turn and restored.\n");
			sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
			sb.Append(new string('=', 100)).Append("\n\n");

			var roots = MenuRoots(includeInactive: true);
			if (roots.Count == 0)
			{
				sb.Append("!! Neither menu canvas could be found. Is the game past the loading screen?\n");
				return sb.ToString();
			}

			// Every page-like object under either canvas.
			var pages = new List<Transform>();
			foreach (var root in roots)
			{
				Transform[] all;
				try { all = root.GetComponentsInChildren<Transform>(true); }
				catch { continue; }
				if (all == null) continue;
				foreach (var t in all)
				{
					if (t == null) continue;
					string nm;
					try { nm = t.name ?? ""; } catch { continue; }
					if (!nm.StartsWith("Menu_", StringComparison.Ordinal)) continue;
					pages.Add(t);
					if (pages.Count >= 40) break;
				}
			}

			sb.Append("pages discovered: ").Append(pages.Count).Append("\n\n");

			foreach (var page in pages)
			{
				string nm = "?";
				try { nm = page.name; } catch { }

				sb.Append('\n').Append(new string('#', 100)).Append('\n');
				sb.Append("PAGE: ").Append(nm).Append('\n');
				try { sb.Append("path: ").Append(PathOf(page)).Append('\n'); } catch { }
				sb.Append(new string('#', 100)).Append("\n");

				// NOTHING IS SWITCHED ON. The first version of this activated each page, read it,
				// and restored it — and it CRASHED VRChat. Waking a page runs its OnEnable, and the
				// game's own menu controllers are not built to be started out of sequence, with no
				// selection, while the menu is closed. Restoring the flag afterwards does not undo
				// whatever the controller did in between.
				//
				// It was also unnecessary, which is the part worth remembering: a component exists
				// on a disabled object, and its CLASS and FIELDS are what this probe is after. Only
				// the live VALUES need a page that has been opened, and those are not what names the
				// injection anchor. So the sweep reads every page exactly where it lies, touching
				// nothing.
				try
				{
					var one = new List<Transform> { page };
					Section(sb, "GRIDS AND LISTS", b => DumpByObjectName(b, one, GridHints));
					Section(sb, "PANELS HOLDING COLLECTIONS", b => DumpPanels(b, one));
				}
				catch (Exception e)
				{
					sb.Append("  !! probing this page threw: ").Append(e.Message).Append('\n');
				}
			}

			sb.Append("\nAll pages restored to the state they were found in. Read-only: nothing was modified or sent.\n");
			return sb.ToString();
		}

		// TARGETED, BOUNDED, AND IT RUNS ITSELF.
		//
		// This is the version that survived. The two earlier attempts failed for the same reason in
		// two different disguises: unbounded work on the main thread. The first walked every
		// MonoBehaviour in the process; the second walked thirty-seven nested pages. Both were
		// "correct" and both hung the game.
		//
		// So this one is bounded three ways, and gives up rather than overrun:
		//   * ONLY the three pages that matter, found by name, not every Menu_*.
		//   * A hard cap on objects inspected and components read per object.
		//   * A STOPWATCH BUDGET. When it is spent the walk stops where it is and the report says
		//     so. A partial answer costs nothing; a frozen game costs the user their session.
		//
		// Nothing is activated: a component exists on a disabled object and its class and fields
		// are exactly what we are after.
		private const int BudgetMs = 120;
		private const int MaxObjects = 60;

		public static string RunTargeted()
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var sb = new System.Text.StringBuilder(48 * 1024);
			sb.Append("MENU PANEL PROBE — TARGETED. Worlds / Social / Avatars only. Nothing is activated.\n");
			sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
			sb.Append(new string('=', 100)).Append("\n\n");

			var roots = MenuRoots(includeInactive: true);
			if (roots.Count == 0)
			{
				sb.Append("!! Neither menu canvas exists yet.\n");
				return sb.ToString();
			}

			string[] wanted = { "Menu_MM_Worlds", "Menu_Social", "Menu_MM_Avatars" };
			int objects = 0;

			foreach (var root in roots)
			{
				Transform[] all;
				try { all = root.GetComponentsInChildren<Transform>(true); }
				catch { continue; }
				if (all == null) continue;

				foreach (var page in all)
				{
					if (page == null) continue;
					if (sw.ElapsedMilliseconds > BudgetMs) break;

					string nm;
					try { nm = page.name ?? ""; } catch { continue; }
					bool want = false;
					for (int i = 0; i < wanted.Length && !want; i++)
						want = nm.StartsWith(wanted[i], StringComparison.Ordinal);
					if (!want) continue;

					sb.Append('\n').Append(new string('#', 90)).Append('\n');
					sb.Append("PAGE: ").Append(nm).Append('\n');
					sb.Append(new string('#', 90)).Append('\n');

					Transform[] kids;
					try { kids = page.GetComponentsInChildren<Transform>(true); }
					catch { continue; }
					if (kids == null) continue;

					foreach (var t in kids)
					{
						if (t == null || objects >= MaxObjects) break;
						if (sw.ElapsedMilliseconds > BudgetMs) break;

						string on;
						try { on = t.name ?? ""; } catch { continue; }
						// Only the handful of objects that could hold the list we want to feed.
						if (on.IndexOf("CellGrid", StringComparison.Ordinal) < 0
							&& on.IndexOf("Playlists", StringComparison.Ordinal) < 0
							&& on.IndexOf("Panel_SectionList", StringComparison.Ordinal) < 0
							&& on.IndexOf("Content_World", StringComparison.Ordinal) < 0
							&& on.IndexOf("ScrollRect_Content", StringComparison.Ordinal) < 0) continue;

						objects++;
						sb.Append("\n  === ").Append(on).Append('\n');
						try { sb.Append("      path: ").Append(PathOf(t)).Append('\n'); } catch { }

						Component[] comps;
						try { comps = t.GetComponents<Component>(); }
						catch { continue; }
						if (comps == null) continue;

						int shown = 0;
						foreach (var c in comps)
						{
							if (c == null || shown >= 8) break;
							if (sw.ElapsedMilliseconds > BudgetMs) break;

							// UniverseLib first: it returns the MANAGED wrapper type, whose name is a
							// legal C# identifier that can be pasted straight into source. The
							// il2cpp reader stays as the fallback for when UnityExplorer is absent.
							Type managed = ActualTypeOf(c);
							var it = managed == null ? Il2CppTypeOf(c) : null;

							string cls = "";
							if (managed != null) { try { cls = managed.Name ?? ""; } catch { } }
							else if (it != null) { try { cls = it.Name ?? ""; } catch { } }
							if (cls.Length == 0) continue;

							if (cls == "RectTransform" || cls == "CanvasRenderer" || cls == "Transform"
								|| cls == "StyleElement" || cls == "Canvas" || cls == "GraphicRaycaster"
								|| cls == "GridLayoutGroup" || cls == "VerticalLayoutGroup"
								|| cls == "ContentSizeFitter" || cls == "ScrollRect"
								|| cls == "Image" || cls == "ImageEx" || cls == "TextMeshProUGUI") continue;

							shown++;
							if (managed != null)
							{
								sb.Append("      + ").Append(managed.FullName ?? cls).Append("   [managed — paste-able]\n");
								DumpManagedMembers(sb, managed, "          ");
							}
							else
							{
								sb.Append("      + ").Append(Ascii(cls)).Append('\n');
								DumpIl2CppFields(sb, it, "          ");
							}
						}
					}
				}
			}

			sb.Append("\nobjects inspected: ").Append(objects);
			sb.Append("   elapsed: ").Append(sw.ElapsedMilliseconds).Append(" ms");
			if (sw.ElapsedMilliseconds > BudgetMs) sb.Append("   [BUDGET SPENT — report is partial by design]");
			sb.Append("\nRead-only: no object was activated, modified or sent.\n");
			return sb.ToString();
		}

		private static readonly string[] GridHints =
		{
			"CellGrid", "Content_World", "Panel_Results", "Playlists", "MyWorldsContent",
			"ScrollRect_Content", "Cell_MM_", "Panel_MM_", "List", "Grid",
		};

		public static string Run()
		{
			var sb = new System.Text.StringBuilder(64 * 1024);
			sb.Append("MENU PANEL PROBE — read-only. Open a VRChat menu (Worlds / Social / Avatars) then press F9.\n");
			sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
			sb.Append(new string('=', 100)).Append("\n\n");

			var roots = MenuRoots();
			sb.Append("menu canvases walked: ").Append(roots.Count).Append('\n');
			foreach (var r in roots) sb.Append("  ").Append(PathOf(r)).Append('\n');
			sb.Append('\n');

			if (roots.Count == 0)
			{
				sb.Append("!! No VRChat menu canvas is active.\n");
				sb.Append("   Open VRChat's own menu (Worlds / Social / Avatars) and press F9 with OUR menu CLOSED —\n");
				sb.Append("   the mod hides VRChat's menu while its own is showing, so there is nothing to read.\n");
				return sb.ToString();
			}

			// MATCHED ON THE GAMEOBJECT NAME, NOT THE CLASS NAME. VRChat's menu classes are
			// obfuscated to things like MonoBehaviourPublicOb_aGa_c_aOb_sGa_e_lUnique, so the only
			// readable identity in the whole hierarchy is what the object is CALLED — CellGrid_MM_Content,
			// Playlists, Panel_Results. Filtering on the class name found nothing at all, which is
			// exactly what the first run of this reported.
			Section(sb, "GRIDS AND LISTS (matched by object name)", b => DumpByObjectName(b, roots,
				new[] { "CellGrid", "Content_World", "Panel_Results", "Playlists", "MyWorldsContent",
						"ScrollRect_Content", "Cell_MM_World" }));
			Section(sb, "PANELS HOLDING COLLECTIONS (sidebar + category observables)", b => DumpPanels(b, roots));
			Section(sb, "PAGE OBJECTS", b => DumpPages(b, roots));

			sb.Append("\nRead-only: nothing was created, modified or sent.\n");
			return sb.ToString();
		}

		// The menu canvases, and only those. Everything this probe is looking for lives under one
		// of them, and scoping here is what turns a process-wide walk into a handful of objects.
		private static List<Transform> MenuRoots(bool includeInactive = false)
		{
			var outp = new List<Transform>();
			try
			{
				foreach (var t in new[] { Core.QuickMenu.Main(), Core.QuickMenu.Root() })
				{
					if (t == null) continue;
					// The full sweep wants the canvases even while they are closed — it switches
					// each page on itself. The single-page probe wants only what is on screen.
					if (!includeInactive)
					{
						try { if (!t.gameObject.activeInHierarchy) continue; } catch { continue; }
					}
					outp.Add(t);
				}
			}
			catch { }
			return outp;
		}

		private static void Section(System.Text.StringBuilder sb, string title, Action<System.Text.StringBuilder> body)
		{
			sb.Append("######## ").Append(title).Append('\n');
			try { body(sb); }
			catch (Exception e) { sb.Append("  !! threw: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n'); }
			sb.Append('\n');
		}

		// Objects whose NAME matches a hint, and EVERY component riding on them. This is the useful
		// direction: we know what the thing is called from the hierarchy dump, and what we need is
		// the obfuscated class that drives it plus the members we can push data through.
		private static void DumpByObjectName(System.Text.StringBuilder sb, List<Transform> roots, string[] hints)
		{
			int found = 0;
			var seen = new HashSet<string>(StringComparer.Ordinal);

			foreach (var root in roots)
			{
				Transform[] all;
				try { all = root.GetComponentsInChildren<Transform>(true); }   // inactive included: nothing is woken
				catch { continue; }
				if (all == null) continue;

				foreach (var t in all)
				{
					if (t == null || found >= 40) continue;

					string nm;
					try { nm = t.name ?? ""; } catch { continue; }
					if (nm.Length == 0) continue;

					bool hit = false;
					for (int i = 0; i < hints.Length && !hit; i++)
						hit = nm.IndexOf(hints[i], StringComparison.OrdinalIgnoreCase) >= 0;
					if (!hit) continue;

					string path;
					try { path = PathOf(t); } catch { continue; }
					if (!seen.Add(path)) continue;

					found++;
					sb.Append("\n  === ").Append(nm).Append('\n');
					sb.Append("      path: ").Append(path).Append('\n');

					Component[] comps;
					try { comps = t.GetComponents<Component>(); }
					catch { continue; }
					if (comps == null) continue;

					foreach (var c in comps)
					{
						if (c == null) continue;

						// TWO NAMES, AND THE USABLE ONE IS THE MANAGED TYPE.
						//
						// VRChat obfuscates its class names with NON-ASCII characters, so the raw
						// il2cpp name is unreadable in a text file and cannot be written in C#
						// source — which is why the first pass of this printed mojibake and could
						// resolve nothing. Il2CppInterop generates a managed wrapper whose name IS a
						// legal identifier (MonoBehaviourPublicObGaObUnique and friends), and the
						// Component handed to us is already an instance of it. So the answer is its
						// own type: no lookup by name, no re-proxy, and the name printed is the one
						// that can be pasted straight into a `using` alias.
						// Read on the IL2CPP side. No managed wrapper is asked for, so nothing is
						// resolved and nothing is logged — see Il2CppTypeOf.
						var it = Il2CppTypeOf(c);
						if (it == null) continue;

						string cls = "";
						try { cls = it.Name ?? ""; } catch { }
						if (cls.Length == 0) continue;

						// Unity's own components tell us nothing we do not already know.
						if (cls == "RectTransform" || cls == "CanvasRenderer" || cls == "Transform"
							|| cls == "Image" || cls == "ImageEx" || cls == "TextMeshProUGUI"
							|| cls == "StyleElement" || cls == "GridLayoutGroup"
							|| cls == "VerticalLayoutGroup" || cls == "HorizontalLayoutGroup"
							|| cls == "ContentSizeFitter" || cls == "Canvas" || cls == "GraphicRaycaster") continue;

						sb.Append("      + ").Append(Ascii(cls)).Append('\n');
						DumpIl2CppFields(sb, it, "          ");
					}
				}
			}
			sb.Append("\n  matches: ").Append(found).Append('\n');
		}

		// Panels that own a list/observable — the injection anchors. Same rule: cheap name test
		// first, re-proxy only what survives it.
		private static void DumpPanels(System.Text.StringBuilder sb, List<Transform> roots)
		{
			int found = 0;
			var seen = new HashSet<string>(StringComparer.Ordinal);

			foreach (var root in roots)
			{
				Component[] comps;
				try { comps = root.GetComponentsInChildren<Component>(true); }  // inactive included: nothing is woken
				catch { continue; }
				if (comps == null) continue;

				foreach (var c in comps)
				{
					if (c == null || found >= 40) continue;

					// Read on the IL2CPP side, like everything else here — Reproxy-by-name forced
					// Il2CppInterop to resolve types that do not exist in this build and flooded the
					// console with "not found" errors.
					var it = Il2CppTypeOf(c);
					if (it == null) continue;

					string cls = "";
					try { cls = it.Name ?? ""; } catch { }
					if (cls.Length == 0) continue;
					if (cls == "RectTransform" || cls == "CanvasRenderer" || cls == "Transform"
						|| cls == "StyleElement" || cls == "Canvas" || cls == "GraphicRaycaster") continue;

					// Anything that looks like it CARRIES a collection — that is the injection anchor.
					var hits = new List<string>();
					try
					{
						var fields = it.GetFields((Il2CppSystem.Reflection.BindingFlags)(
							(int)Il2CppSystem.Reflection.BindingFlags.Public
							| (int)Il2CppSystem.Reflection.BindingFlags.NonPublic
							| (int)Il2CppSystem.Reflection.BindingFlags.Instance));
						if (fields != null)
							for (int fi = 0; fi < fields.Length && hits.Count < 20; fi++)
							{
								var f = fields[fi];
								if (f == null) continue;
								string fn = "", tn = "";
								try { fn = f.Name ?? ""; } catch { }
								try { tn = f.FieldType?.Name ?? ""; } catch { }
								if (fn.Length == 0) continue;
								if (tn.IndexOf("List", StringComparison.Ordinal) >= 0
									|| tn.IndexOf("IEnumerable", StringComparison.Ordinal) >= 0
									|| tn.IndexOf("IReadOnly", StringComparison.Ordinal) >= 0
									|| tn.IndexOf("Observable", StringComparison.Ordinal) >= 0
									|| fn.IndexOf("List", StringComparison.OrdinalIgnoreCase) >= 0)
									hits.Add(Ascii(tn) + "  " + Ascii(fn));
							}
					}
					catch { }
					if (hits.Count == 0) continue;

					string path;
					try { path = PathOf(c.transform); } catch { continue; }
					if (!seen.Add(cls + "@" + path)) continue;

					found++;
					sb.Append("\n  --- ").Append(cls).Append('\n');
					sb.Append("      path: ").Append(path).Append('\n');
					foreach (string h in hits) sb.Append("      ").Append(h).Append('\n');
				}
			}
			sb.Append("\n  collection panels: ").Append(found).Append('\n');
		}

		private static void DumpPages(System.Text.StringBuilder sb, List<Transform> roots)
		{
			int n = 0;
			foreach (var root in roots)
			{
				Transform[] all;
				try { all = root.GetComponentsInChildren<Transform>(true); }   // inactive included: nothing is woken
				catch { continue; }
				if (all == null) continue;

				foreach (var t in all)
				{
					if (t == null || n >= 80) continue;
					string nm;
					try { nm = t.name ?? ""; } catch { continue; }
					if (!nm.StartsWith("Page_", StringComparison.Ordinal)
						&& !nm.StartsWith("Menu_", StringComparison.Ordinal)
						&& !nm.StartsWith("Panel_", StringComparison.Ordinal)) continue;
					n++;
					sb.Append("  ").Append(PathOf(t)).Append('\n');
				}
			}
			sb.Append("\n  pages/panels: ").Append(n).Append('\n');
		}

		// ------------------------------------------------------------------ helpers

		private static void DumpMembers(System.Text.StringBuilder sb, object o, string pad)
		{
			try
			{
				// StyleElement carries fifty style-engine members that are identical on every card
				// and say nothing about the data a panel holds.
				if (o.GetType().Name == "StyleElement") { sb.Append(pad).Append("(style only)\n"); return; }

				foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (p.GetIndexParameters().Length > 0) continue;
					string n = p.Name;
					bool keep = n.StartsWith("field_", StringComparison.Ordinal)
						|| n.StartsWith("prop_", StringComparison.Ordinal)
						|| n.IndexOf("List", StringComparison.Ordinal) >= 0
						|| n.IndexOf("Item", StringComparison.Ordinal) >= 0;
					if (!keep) continue;
					sb.Append(pad).Append("prop ").Append(n).Append(" : ").Append(Short(p.PropertyType)).Append('\n');
				}
			}
			catch { }
		}

		// RUNTIME CLASS POINTER → MANAGED WRAPPER TYPE.
		//
		// This is the piece that makes the whole probe work, and it took three wrong turns to find.
		//   * Matching by NAME fails: VRChat's il2cpp class names contain non-ASCII characters,
		//     while Il2CppInterop's generated wrapper carries a sanitised name. The two never match.
		//   * Calling c.GetType() fails too, and silently: the object handed back by
		//     GetComponents<Component>() is a proxy whose MANAGED type is the static type asked for
		//     — UnityEngine.Component — not the runtime class. That is why the first full sweep
		//     listed thirty-seven pages and not a single component: everything answered
		//     "UnityEngine.Component" and was filtered out as a Unity built-in.
		//
		// The identity that IS reliable is the native class pointer. Every generated wrapper stores
		// its own in Il2CppClassPointerStore<T>.NativeClassPtr, so one pass over the loaded types
		// builds an exact pointer→type map, and a component's class pointer then names it precisely.

		// DO NOT BUILD A CLASS-POINTER MAP. The obvious version of this reads
		// Il2CppClassPointerStore<T>.NativeClassPtr for every loaded type — and touching that static
		// field RUNS Il2CppInterop's initialiser for T, which tries to resolve T inside il2cpp.
		// For the thousands of generated types that do not exist in this particular game build that
		// resolution fails and LOGS, so a single probe press flooded the console with hundreds of
		// "Nested type ... not found!" and "Assembly __Generated.dll is not registered in il2cpp"
		// errors. Discovery must never force resolution of types nobody asked for.
		//
		// So the members are read on the IL2CPP side instead, through the runtime's own reflection.
		// It needs no managed wrapper, resolves nothing, and reports the class exactly as the game
		// defines it.
		// BORROWED FROM UNITYEXPLORER — the one piece we could not do ourselves.
		//
		// Naming an il2cpp component's real class defeated three attempts here: by name (VRChat's
		// are non-ASCII), by c.GetType() (answers UnityEngine.Component), and by building a
		// class-pointer map (forced Il2CppInterop to resolve thousands of absent types and flooded
		// the console). UniverseLib — which ships with UnityExplorer and is already loaded in this
		// process — solves it properly in ReflectionUtility.GetActualType, and returns the MANAGED
		// wrapper type, whose name is a legal C# identifier that can be pasted straight into source.
		//
		// Resolved by reflection and cached, so nothing breaks when UnityExplorer is not installed:
		// the il2cpp-side reader below is still the fallback.
		private static MethodInfo _getActualType;
		private static object _actualTypeTarget;   // null when the method is static
		private static bool _actualTypeTried;

		// Exposed so AutoProbe can use the same resolution rather than duplicating it.
		public static bool HasActualType { get { ActualTypeOf(null); return _getActualType != null; } }
		public static Type ActualTypeOfPublic(object o) => ActualTypeOf(o);

		private static Type ActualTypeOf(object c)
		{
			try
			{
				if (!_actualTypeTried)
				{
					_actualTypeTried = true;
					// FOUND BY SHAPE, NOT BY EXACT NAME. The first version asked for
					// UniverseLib.ReflectionUtility.GetActualType and reported "absent" while
					// UnityExplorer was demonstrably loaded — because in this build the method is
					// called Internal_GetActualType and hangs off the provider instance, not a
					// static. Guessing one exact name is how that failed; matching any single-object
					// method whose name contains GetActualType finds it whatever the version calls
					// it, and the instance is resolved when the method is not static.
					foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
					{
						string an = "";
						try { an = asm.GetName().Name ?? ""; } catch { }
						if (an.IndexOf("UniverseLib", StringComparison.OrdinalIgnoreCase) < 0) continue;

						Type[] types;
						try { types = asm.GetTypes(); }
						catch (ReflectionTypeLoadException ex) { types = ex.Types ?? new Type[0]; }
						catch { continue; }

						foreach (var t in types)
						{
							if (t == null) continue;
							string tn = t.Name ?? "";
							if (tn.IndexOf("Reflection", StringComparison.Ordinal) < 0) continue;

							foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
														 | BindingFlags.Static | BindingFlags.Instance))
							{
								if (m.Name.IndexOf("GetActualType", StringComparison.Ordinal) < 0) continue;
								var ps = m.GetParameters();
								if (ps.Length != 1 || ps[0].ParameterType != typeof(object)) continue;
								if (m.ReturnType != typeof(Type)) continue;

								if (!m.IsStatic)
								{
									// The provider singleton: a static Instance-like member on the type.
									object inst = null;
									foreach (var pn in new[] { "Instance", "Current", "Provider" })
									{
										try
										{
											var pi = t.GetProperty(pn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
											if (pi != null) inst = pi.GetValue(null);
											if (inst == null)
											{
												var fi = t.GetField(pn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
												if (fi != null) inst = fi.GetValue(null);
											}
										}
										catch { }
										if (inst != null) break;
									}
									if (inst == null) continue;
									_actualTypeTarget = inst;
								}

								_getActualType = m;
								break;
							}
							if (_getActualType != null) break;
						}

						if (_getActualType != null)
						{
							VRChatArchiveModPlugin.Logger.LogInfo(
								"[MenuPanelProbe] type resolution via UniverseLib: "
								+ _getActualType.DeclaringType?.Name + "." + _getActualType.Name);
							break;
						}
					}
				}

				if (_getActualType == null || c == null) return null;
				return _getActualType.Invoke(_actualTypeTarget, new[] { c }) as Type;
			}
			catch { return null; }
		}

		private static Il2CppSystem.Type Il2CppTypeOf(Il2CppObjectBase o)
		{
			try
			{
				if (o == null || o.Pointer == IntPtr.Zero) return null;
				IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(o.Pointer);
				if (klass == IntPtr.Zero) return null;
				IntPtr t = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_type(klass);
				if (t == IntPtr.Zero) return null;

				// il2cpp_type_get_object already hands back the System.Type OBJECT. Feeding it to
				// internal_from_handle, which wants an Il2CppType* HANDLE, made the runtime read an
				// object as a type structure -- an access violation, and the process is gone. Wrapping
				// the pointer is both correct and one icall cheaper.
				IntPtr obj = Il2CppInterop.Runtime.IL2CPP.il2cpp_type_get_object(t);
				if (!Core.NativeGuard.IsLiveObject(obj)) return null;
				return new Il2CppSystem.Type(obj);
			}
			catch { return null; }
		}

		// The collection-shaped members of a MANAGED wrapper type — the injection anchors. A grid
		// renders from a list, so the list living on one of these is what a world or user feed has
		// to be pushed into. Names here are paste-able C#.
		private static void DumpManagedMembers(System.Text.StringBuilder sb, Type t, string pad)
		{
			try
			{
				if (t == null) return;
				const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
				int n = 0;

				foreach (var f in t.GetFields(F))
				{
					if (n >= 30) break;
					string ft = Short(f.FieldType);
					if (ft.IndexOf("List", StringComparison.Ordinal) < 0
						&& ft.IndexOf("IEnumerable", StringComparison.Ordinal) < 0
						&& ft.IndexOf("IReadOnly", StringComparison.Ordinal) < 0
						&& ft.IndexOf("Observable", StringComparison.Ordinal) < 0
						&& f.Name.IndexOf("List", StringComparison.OrdinalIgnoreCase) < 0) continue;
					sb.Append(pad).Append("field ").Append(ft).Append("  ").Append(f.Name).Append('\n');
					n++;
				}

				foreach (var p in t.GetProperties(F))
				{
					if (n >= 30) break;
					if (p.GetIndexParameters().Length > 0) continue;
					string pt = Short(p.PropertyType);
					if (pt.IndexOf("List", StringComparison.Ordinal) < 0
						&& pt.IndexOf("IEnumerable", StringComparison.Ordinal) < 0
						&& pt.IndexOf("IReadOnly", StringComparison.Ordinal) < 0
						&& pt.IndexOf("Observable", StringComparison.Ordinal) < 0) continue;
					sb.Append(pad).Append("prop  ").Append(pt).Append("  ").Append(p.Name).Append('\n');
					n++;
				}

				if (n == 0) sb.Append(pad).Append("(no collection-shaped members)\n");
			}
			catch (Exception e) { sb.Append(pad).Append("!! members threw: ").Append(e.Message).Append('\n'); }
		}

		// The fields a component actually carries, straight off the il2cpp class. These are the
		// injection anchors: a List<...> field on a panel is what the menu renders from.
		private static void DumpIl2CppFields(System.Text.StringBuilder sb, Il2CppSystem.Type t, string pad)
		{
			try
			{
				if (t == null) return;
				var fields = t.GetFields((Il2CppSystem.Reflection.BindingFlags)(
					(int)Il2CppSystem.Reflection.BindingFlags.Public
					| (int)Il2CppSystem.Reflection.BindingFlags.NonPublic
					| (int)Il2CppSystem.Reflection.BindingFlags.Instance));
				if (fields == null) return;

				int n = 0;
				for (int i = 0; i < fields.Length && n < 40; i++)
				{
					var f = fields[i];
					if (f == null) continue;
					string fn = "", ft = "";
					try { fn = f.Name ?? ""; } catch { }
					try { ft = f.FieldType?.Name ?? ""; } catch { }
					if (fn.Length == 0) continue;

					// Only what could hold the menu's data. A panel has dozens of layout and style
					// fields that are the same on every panel in the game.
					if (ft.IndexOf("List", StringComparison.Ordinal) < 0
						&& ft.IndexOf("Dictionary", StringComparison.Ordinal) < 0
						&& ft.IndexOf("IEnumerable", StringComparison.Ordinal) < 0
						&& ft.IndexOf("Observable", StringComparison.Ordinal) < 0
						&& fn.IndexOf("List", StringComparison.OrdinalIgnoreCase) < 0) continue;

					sb.Append(pad).Append(Ascii(ft)).Append("  ").Append(Ascii(fn)).Append('\n');
					n++;
				}
				if (n == 0) sb.Append(pad).Append("(no collection-shaped fields)\n");
			}
			catch (Exception e) { sb.Append(pad).Append("!! fields threw: ").Append(e.Message).Append('\n'); }
		}

		// VRChat's obfuscated identifiers contain characters that make a text file unreadable.
		// Keep them, but show them in a form that survives a copy-paste.
		private static string Ascii(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			bool clean = true;
			for (int i = 0; i < s.Length && clean; i++) if (s[i] > 126 || s[i] < 32) clean = false;
			if (clean) return s;
			var sb = new System.Text.StringBuilder(s.Length + 16);
			foreach (char c in s)
			{
				if (c > 126 || c < 32) sb.Append("\\u").Append(((int)c).ToString("X4"));
				else sb.Append(c);
			}
			return sb.ToString();
		}

		// name → managed wrapper type, built ONCE. Searching every assembly per component is what
		// made the first version hang the game.
		private static Dictionary<string, Type> _byName;

		private static Type TypeNamed(string cls)
		{
			if (_byName == null)
			{
				_byName = new Dictionary<string, Type>(StringComparer.Ordinal);
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					Type[] types;
					try { types = asm.GetTypes(); }
					catch (ReflectionTypeLoadException ex) { types = ex.Types ?? new Type[0]; }
					catch { continue; }
					foreach (var t in types)
					{
						if (t == null) continue;
						string n;
						try { n = t.Name; } catch { continue; }
						if (!_byName.ContainsKey(n)) _byName[n] = t;
					}
				}
			}
			return _byName.TryGetValue(cls, out var found) ? found : null;
		}

		private static object Reproxy(Il2CppObjectBase ob, string cls)
		{
			try
			{
				if (ob == null || string.IsNullOrEmpty(cls)) return null;
				var t = TypeNamed(cls);
				if (t == null) return null;
				var ctor = t.GetConstructor(new[] { typeof(IntPtr) });
				if (ctor == null) return null;
				return ctor.Invoke(new object[] { ob.Pointer });
			}
			catch { return null; }
		}

		private static string Short(Type t) { try { return t == null ? "?" : t.Name; } catch { return "?"; } }

		private static string PathOf(Transform t)
		{
			var sb = new System.Text.StringBuilder(128);
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
