using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// CAPTURE — an in-game, read-only reverse-engineering dumper. It writes rich snapshots of
	// VRChat's live objects to files so we can integrate against the REAL structure instead of
	// guessing: the QuickMenu/MainMenu tree, the loading-screen popup, every AudioSource + clip,
	// and every UdonBehaviour + its program's entry points (the "6/7" event names).
	//
	// Strictly local and passive: it only READS objects your client already has in memory and
	// writes them to disk. Nothing is ever sent anywhere. UnityExplorer stays available for live
	// interactive inspection; this gives us repeatable, mailable text dumps on demand.
	//
	// Output: BepInEx/VRChatArchiveMod/captures/<what>_<timestamp>.txt
	public class CaptureModule : IModule
	{
		public override string Name => "Capture";

		public enum Job { None, Menu, Loading, Audio, Udon, AllUI, Metadata, UiApi, Favorites, AvatarList, MenuPanels, MenuSweep, MenuTargeted, AutoAll }
		private static readonly Queue<Job> Pending = new Queue<Job>();
		public static string LastFile = "(none yet)";

		public static void Request(Job j) { lock (Pending) Pending.Enqueue(j); }

		public override void OnUpdate()
		{
			Job j = Job.None;
			lock (Pending) { if (Pending.Count > 0) j = Pending.Dequeue(); }
			if (j == Job.None) return;
			try { Run(j); }
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[Capture] {j} threw: {e}"); }
		}

		private static void Run(Job j)
		{
			switch (j)
			{
				case Job.Menu:    Dump("menu", () => DumpMenu()); break;
				case Job.Loading: Dump("loading", () => DumpByRootName("LoadingPopup", 12)); break;
				case Job.Audio:   Dump("audio", DumpAudio); break;
				case Job.Udon:    Dump("udon", DumpUdon); break;
				case Job.AllUI:   Dump("ui", () => DumpByRootName("UserInterface", 14)); break;
				case Job.Metadata: Dump("metadata", DumpMetadata); break;
				case Job.UiApi:   Dump("uiapi", DumpUiApi); break;
				case Job.Favorites: Dump("favorites", DumpFavorites); break;
				case Job.AvatarList: Dump("avatarlist", AvatarListProbe.Run); break;
				case Job.MenuPanels: Dump("menupanels", MenuPanelProbe.Run); break;
				case Job.MenuSweep:  Dump("menusweep", MenuPanelProbe.RunAll); break;
				case Job.MenuTargeted: Dump("menuauto", MenuPanelProbe.RunTargeted); break;
				case Job.AutoAll:     Dump("autoprobe", AutoProbe.Run); break;
			}
		}

		private static void Dump(string what, Func<string> body)
		{
			try
			{
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "captures");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, what + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
				File.WriteAllText(path, body());
				LastFile = path;
				VRChatArchiveModPlugin.Logger.LogInfo($"[Capture] {what} -> {path}");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[Capture] {what} failed: {e.Message}"); }
		}

		// ---------------------------------------------------------------- subsystem dumps

		private static string DumpMenu()
		{
			var sb = new StringBuilder(256 * 1024);
			foreach (string root in new[] { "UserInterface", "Canvas_MainMenu(Clone)" })
			{
				Transform t = FindRoot(root);
				sb.Append("######## ROOT: ").Append(root).Append(t == null ? "  (NOT FOUND)" : "").Append('\n');
				if (t != null) Walk(t, 0, sb, 14);
				sb.Append('\n');
			}
			return sb.ToString();
		}

		private static string DumpByRootName(string name, int depth)
		{
			var sb = new StringBuilder(256 * 1024);
			int n = 0;
			foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
			{
				if (t == null || t.name != name) continue;
				if (t.hideFlags == HideFlags.HideAndDontSave) continue;
				try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
				sb.Append("######## ").Append(name).Append("  #").Append(++n)
				  .Append(t.gameObject.activeInHierarchy ? "  (active)" : "  (inactive)").Append('\n');
				Walk(t, 0, sb, depth);
				sb.Append('\n');
			}
			if (n == 0) sb.Append("(no live '").Append(name).Append("' found)\n");
			return sb.ToString();
		}

		private static string DumpAudio()
		{
			var sb = new StringBuilder(64 * 1024);
			sb.Append("AUDIO SOURCES (name  ->  clip  [len, freq, ch]  playOnAwake/loop)\n\n");
			int n = 0;
			foreach (var a in Resources.FindObjectsOfTypeAll<AudioSource>())
			{
				if (a == null) continue;
				try
				{
					if (a.hideFlags == HideFlags.HideAndDontSave) continue;
					string path = PathOf(a.transform);
					AudioClip c = a.clip;
					string clip = c != null ? $"{c.name} [{c.length:F1}s, {c.frequency}Hz, {c.channels}ch]" : "(none)";
					sb.Append(path).Append("\n    -> ").Append(clip)
					  .Append("   playOnAwake=").Append(a.playOnAwake).Append(" loop=").Append(a.loop)
					  .Append(" playing=").Append(a.isPlaying).Append('\n');
					n++;
				}
				catch { }
			}
			sb.Append("\ntotal AudioSources: ").Append(n).Append('\n');
			return sb.ToString();
		}

		// Every UdonBehaviour + the program entry points it can run — this is where the "6/7"
		// event names come from (the compiled Udon graph's exported symbols).
		private static string DumpUdon()
		{
			var sb = new StringBuilder(128 * 1024);
			Type ub = Type.GetType("VRC.Udon.UdonBehaviour, VRC.Udon", false) ?? FindType("VRC.Udon.UdonBehaviour");
			if (ub == null) return "UdonBehaviour type not found.\n";

			var il2 = Il2CppType.From(ub);
			var found = Resources.FindObjectsOfTypeAll(il2);
			sb.Append("UDON BEHAVIOURS (object  ->  entry points)\n\n");
			int n = 0, total = 0;
			// ONE LINE PER PROGRAM, not per object.
			//
			// A world spawns the same Udon program hundreds of times: the LateNight dump spent its
			// entire budget on LatencyManager/LatencyRecord(0..N) and video-player list items, all
			// identical, and the 400-object cap cut it off long before anything interesting. What
			// matters for building per-world features is the SET of programs and their entry points,
			// so copies collapse into a count and an example path.
			var byProgram = new Dictionary<string, (int count, string first, string events)>(StringComparer.Ordinal);
			var order = new List<string>();

			if (found != null)
				for (int i = 0; i < found.Length; i++)
				{
					try
					{
						var comp = found[i]?.TryCast<Component>();
						if (comp == null) continue;
						if (comp.hideFlags == HideFlags.HideAndDontSave) continue;
						total++;

						// ENTRY POINTS. GetPrograms() returns an ImmutableArray<string>, and this used
						// to call ToString() on it — which prints the TYPE NAME, not the contents.
						// That is why every dump so far listed object paths and not one event.
						var ev = new StringBuilder(512);
						AppendEntryPoints(ev, ub, found[i]);
						string events = ev.ToString();

						// The entry-point set IS the program's identity here: two behaviours running
						// the same compiled graph expose exactly the same exported symbols.
						if (byProgram.TryGetValue(events, out var slot))
						{
							byProgram[events] = (slot.count + 1, slot.first, slot.events);
							continue;
						}
						if (n >= 400) { continue; }
						string path = PathOf(comp.transform);
						byProgram[events] = (1, path, events);
						order.Add(events);
						n++;
					}
					catch { }
				}

			foreach (string key in order)
			{
				var slot = byProgram[key];
				sb.Append(slot.first);
				if (slot.count > 1) sb.Append("      [x").Append(slot.count).Append(" copies in this world]");
				sb.Append('\n').Append(slot.events);
			}
			if (n >= 400) sb.Append("... (capped at 400 distinct programs)\n");
			sb.Append("\ndistinct programs: ").Append(n).Append("   ·   total UdonBehaviours: ").Append(total).Append('\n');
			sb.Append("\nNote: the live event names (interact/pickup/custom, and numeric jumps like '6','7')\n");
			sb.Append("are captured live by the UDON console; this list is the objects that raise them.\n");
			return sb.ToString();
		}


		// Writes an UdonBehaviour's entry points, and marks the ones VRChat itself considers
		// NETWORK-CALLABLE. That second list is the useful one: those are the events that do
		// something for everybody when fired, as opposed to the ones that only run locally. For
		// building anything per-world, that distinction is the whole point of the dump.
		private static void AppendEntryPoints(StringBuilder sb, Type ub, object behaviour)
		{
			// --- every entry point ---------------------------------------------------------
			try
			{
				var mi = ub.GetMethod("GetPrograms");
				object arr = mi?.Invoke(behaviour, null);
				if (arr != null)
				{
					var at = arr.GetType();
					var lenP = at.GetProperty("Length") ?? at.GetProperty("Count");
					var item = at.GetProperty("Item");
					if (lenP?.GetValue(arr) is int len && len > 0 && item != null)
					{
						sb.Append("    events (").Append(len).Append("): ");
						int written = 0;
						for (int k = 0; k < len && k < 60; k++)
						{
							try
							{
								string name = item.GetValue(arr, new object[] { k }) as string;
								if (string.IsNullOrEmpty(name)) continue;
								if (written++ > 0) sb.Append(", ");
								sb.Append(name);
							}
							catch { }
						}
						if (len > 60) sb.Append(", … +").Append(len - 60);
						sb.Append('\n');
					}
				}
			}
			catch { }

			// --- the network-callable subset ------------------------------------------------
			try
			{
				var mi = ub.GetMethod("GetNetworkCallingMetadata", Type.EmptyTypes);
				object arr = mi?.Invoke(behaviour, null);
				if (arr == null) return;
				var at = arr.GetType();
				var lenP = at.GetProperty("Length") ?? at.GetProperty("Count");
				var item = at.GetProperty("Item");
				if (!(lenP?.GetValue(arr) is int len) || len <= 0 || item == null) return;

				sb.Append("    NETWORKED: ");
				int w = 0;
				for (int k = 0; k < len && k < 40; k++)
				{
					try
					{
						object meta = item.GetValue(arr, new object[] { k });
						if (meta == null) continue;
						var mt = meta.GetType();
						string name = (mt.GetProperty("EntrypointName") ?? mt.GetProperty("Name"))
							?.GetValue(meta) as string;
						if (string.IsNullOrEmpty(name)) name = meta.ToString();
						if (w++ > 0) sb.Append(", ");
						sb.Append(name);
					}
					catch { }
				}
				sb.Append('\n');
			}
			catch { }
		}

		// FAVOURITES — what VRChat's own favourite system looks like AT RUNTIME.
		//
		// Written to answer one question that static inspection could not: a favourite is only a
		// content id (VRC.Core.FavoriteModel is {type, contentId, tags}), and the lists that hold
		// them are VRC.Core.FavoriteListModel — but WHO owns those lists is obfuscated
		// (MonoBehaviourPublicFaVo0/1, ObjectPublicAbstractSealedIR1FaUnique), so there is no way to
		// tell from the assemblies whether a list added at runtime would show up in the menu.
		//
		// This prints every live FavoriteListModel with its name, type and contents, and every field
		// anywhere that points at one. That is the difference between designing this feature and
		// guessing at it.
		private static string DumpFavorites()
		{
			var sb = new StringBuilder(64 * 1024);
			sb.Append("VRCHAT FAVOURITES — live objects\n\n");

			Type listT = FindType("VRC.Core.FavoriteListModel");
			Type favT = FindType("VRC.Core.FavoriteModel");
			sb.Append("FavoriteListModel : ").Append(listT?.FullName ?? "NOT FOUND").Append('\n');
			sb.Append("FavoriteModel     : ").Append(favT?.FullName ?? "NOT FOUND").Append("\n\n");
			if (listT == null) return sb.ToString();

			// A FavoriteListModel is a plain C# class, NOT a UnityEngine.Object, so
			// Resources.FindObjectsOfTypeAll can never return one — an earlier version of this dump
			// looked for them that way and reported zero, which said nothing at all. The lists have
			// to be reached through the MonoBehaviours that hold them.
			sb.Append("HOLDERS — every type with a field or property of this type:\n");
			var holders = new List<Type>();
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				foreach (var t in SafeTypes(asm))
				{
					try
					{
						bool hit = false;
						foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
						{
							if ((f.FieldType.FullName ?? "").IndexOf("FavoriteListModel", StringComparison.Ordinal) < 0) continue;
							sb.Append("  field  ").Append(t.FullName).Append('.').Append(f.Name)
							  .Append(" : ").Append(f.FieldType.Name).Append('\n');
							hit = true;
						}
						foreach (var pr in t.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
						{
							if ((pr.PropertyType.FullName ?? "").IndexOf("FavoriteListModel", StringComparison.Ordinal) < 0) continue;
							sb.Append("  prop   ").Append(t.FullName).Append('.').Append(pr.Name)
							  .Append(" : ").Append(pr.PropertyType.Name).Append('\n');
							hit = true;
						}
						if (hit) holders.Add(t);
					}
					catch { }
				}
			}
			if (holders.Count == 0) sb.Append("  (none — the reference is probably behind a generic collection)\n");

			sb.Append("\nLIVE HOLDERS:\n");
			foreach (var t in holders)
			{
				try
				{
					// Only Components can be found in the scene; the rest are still reported so the
					// candidate list stays complete even when no instance is reachable.
					if (!IsIl2CppComponent(t))
					{
						sb.Append("  ").Append(t.FullName).Append("  (not a Component — not scannable)\n");
						continue;
					}
					var il2 = Il2CppType.From(t);
					var found = Resources.FindObjectsOfTypeAll(il2);
					sb.Append("  ").Append(t.FullName).Append("  instances: ").Append(found?.Length ?? 0).Append('\n');
					if (found == null) continue;
					for (int i = 0; i < found.Length && i < 4; i++)
					{
						try
						{
							var c = found[i]?.TryCast<Component>();
							if (c != null) sb.Append("      at ").Append(PathOf(c.transform)).Append('\n');
							DumpHolderFields(sb, t, found[i]);
						}
						catch { }
					}
				}
				catch (Exception e) { sb.Append("  ").Append(t.FullName).Append("  scan failed: ").Append(e.Message).Append('\n'); }
			}

			// ---- THE STORE ----------------------------------------------------------------
			// VRC.Core.FavoriteArea is the one non-obfuscated name in the whole chain, it holds
			// List<FavoriteListModel> for avatars / worlds / friends, and NOTHING anywhere holds a
			// FavoriteArea — which points at it being static. If it is, the avatar categories in the
			// menu are a list we can read, and adding to it is the whole feature.
			sb.Append("\nFAVORITE AREA (the store):\n");
			try
			{
				Type area = FindType("VRC.Core.FavoriteArea");
				if (area == null) sb.Append("  VRC.Core.FavoriteArea NOT FOUND\n");
				else
				{
					foreach (string pn in new[] { "MAX_FAVORITES_LISTS", "MAX_FAVORITES_PER_LIST", "MIN_FAVORITES_LISTS" })
					{
						try
						{
							var pi = area.GetProperty(pn, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
							sb.Append("  ").Append(pn).Append(" = ").Append(pi == null ? "(no static prop)" : (pi.GetValue(null)?.ToString() ?? "null")).Append('\n');
						}
						catch (Exception e) { sb.Append("  ").Append(pn).Append(" -> ").Append(e.GetType().Name).Append('\n'); }
					}

					foreach (string pn in new[] { "_avatars", "Avatars", "_worlds", "_friends" })
					{
						try
						{
							var pi = area.GetProperty(pn, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
							if (pi == null) { sb.Append("  ").Append(pn).Append(" : no STATIC property (instance only?)\n"); continue; }
							object v = pi.GetValue(null);
							if (v == null) { sb.Append("  ").Append(pn).Append(" = null\n"); continue; }
							var cp = v.GetType().GetProperty("Count");
							int n = 0;
							try { n = (int)(cp?.GetValue(v) ?? 0); } catch { }
							sb.Append("  ").Append(pn).Append(" = ").Append(v.GetType().Name).Append("  count=").Append(n).Append('\n');

							// Name every category so the dump can be checked against the menu.
							var item = v.GetType().GetProperty("Item");
							for (int i = 0; i < n && i < 20 && item != null; i++)
							{
								try
								{
									object m = item.GetValue(v, new object[] { i });
									if (m == null) continue;
									sb.Append("      [").Append(i).Append("] ");
									foreach (string f in new[] { "name", "displayName", "type" })
									{
										try
										{
											var fp = m.GetType().GetProperty(f);
											object fv = fp?.GetValue(m);
											if (fv != null) sb.Append(f).Append('=').Append(fv).Append("  ");
										}
										catch { }
									}
									sb.Append('\n');
								}
								catch { }
							}
						}
						catch (Exception e) { sb.Append("  ").Append(pn).Append(" -> ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n'); }
					}
				}
			}
			catch (Exception e) { sb.Append("  area scan failed: ").Append(e.Message).Append('\n'); }

			// ---- LIVE SWEEP ----------------------------------------------------------------
			// Round two. The first version asked every live MonoBehaviour for its managed type and
			// looked for a FavoriteListModel field: 0 hits out of 68557, which is not credible when
			// the menu is visibly rendering those very lists. The reason is that under Il2CppInterop
			// mb.GetType() commonly hands back the BASE UnityEngine.MonoBehaviour proxy rather than
			// the real obfuscated subclass — so the field scan was running against the wrong type
			// every single time and could never hit.
			//
			// This asks IL2CPP itself for the object's real class name instead, and reports the
			// distribution of what it saw. If nearly all 68k objects report one or two class names,
			// that is the proxy problem; if they report thousands of distinct names, the type info is
			// fine and the absence is real.
			sb.Append("\nLIVE SWEEP v2 (real IL2CPP class names):\n");
			try
			{
				var names = new Dictionary<string, int>(StringComparer.Ordinal);
				var interesting = new List<string>();
				int seen = 0;

				foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
				{
					if (mb == null) continue;
					try
					{
						if (!mb.gameObject.scene.IsValid()) continue;
						seen++;
						string cls = Core.MenuCard.Il2CppNameOf(mb);
						if (string.IsNullOrEmpty(cls)) cls = "?";
						names.TryGetValue(cls, out int c);
						names[cls] = c + 1;

						// Anything whose class name hints at favourites is worth its path.
						if (cls.IndexOf("Fa", StringComparison.Ordinal) >= 0
							&& (cls.IndexOf("Favorite", StringComparison.OrdinalIgnoreCase) >= 0
								|| cls.StartsWith("MonoBehaviourPublicFaVo", StringComparison.Ordinal)))
						{
							if (interesting.Count < 40)
								interesting.Add("  " + cls + "   at " + PathOf(mb.transform));
						}
					}
					catch { }
				}

				sb.Append("  swept ").Append(seen).Append(" behaviours, ")
				  .Append(names.Count).Append(" DISTINCT class names\n");
				if (names.Count <= 3)
					sb.Append("  >>> only a handful of distinct names: IL2CPP type info is NOT resolving,\n")
					  .Append("      so any field scan here is meaningless.\n");

				sb.Append("\n  favourite-looking classes:\n");
				if (interesting.Count == 0) sb.Append("    (none)\n");
				foreach (var l in interesting) sb.Append(l).Append('\n');

				// The 25 most common classes, as a sanity check on what the sweep is actually seeing.
				var top = new List<KeyValuePair<string, int>>(names);
				top.Sort((x, y) => y.Value.CompareTo(x.Value));
				sb.Append("\n  most common classes seen:\n");
				for (int i = 0; i < top.Count && i < 25; i++)
					sb.Append("    ").Append(top[i].Value).Append("  ").Append(top[i].Key).Append('\n');
			}
			catch (Exception e) { sb.Append("  sweep failed: ").Append(e.Message).Append('\n'); }

			sb.Append("\nRead-only: this looks at objects the game already built. Nothing was created,\n");
			sb.Append("modified or sent.\n");
			return sb.ToString();
		}

		// GetTypes() throws on assemblies holding types it cannot load — Assembly-CSharp does, every
		// time. Swallowing that drops the WHOLE assembly, which is how the first version of this dump
		// silently skipped the one place the answer lives. The partial list is exactly what we want.
		private static IEnumerable<Type> SafeTypes(System.Reflection.Assembly asm)
		{
			try { return asm.GetTypes(); }
			catch (System.Reflection.ReflectionTypeLoadException ex)
			{
				var outp = new List<Type>();
				if (ex.Types != null)
					foreach (var t in ex.Types) if (t != null) outp.Add(t);
				return outp;
			}
			catch { return Array.Empty<Type>(); }
		}

		private static bool IsIl2CppComponent(Type t)
		{
			try
			{
				for (Type b = t; b != null; b = b.BaseType)
					if (b.Name == "MonoBehaviour" || b.Name == "Component" || b.Name == "Behaviour") return true;
			}
			catch { }
			return false;
		}

		private static void DumpHolderFields(StringBuilder sb, Type t, object inst)
		{
			foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				try
				{
					if ((f.FieldType.FullName ?? "").IndexOf("FavoriteListModel", StringComparison.Ordinal) < 0) continue;
					object v = f.GetValue(f.IsStatic ? null : inst);
					sb.Append("        ").Append(f.Name).Append(" = ");
					if (v == null) { sb.Append("null\n"); continue; }
					var cp = v.GetType().GetProperty("Count") ?? v.GetType().GetProperty("Length");
					object cnt = null;
					try { cnt = cp?.GetValue(v); } catch { }
					sb.Append(v.GetType().Name);
					if (cnt != null) sb.Append("  count=").Append(cnt);
					sb.Append('\n');
				}
				catch { }
			}
		}

		// METADATA — everything the client ALREADY knows about the room and the people in it,
		// written out as text. No network request is made: every field below is read from the
		// APIUser / ApiAvatar / ApiWorldInstance objects VRChat has already downloaded, which is
		// why this is effectively free.
		private static string DumpMetadata()
		{
			var sb = new StringBuilder(64 * 1024);
			sb.AppendLine("VRCHAT ARCHIVE MOD — instance + player metadata");
			sb.AppendLine("generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			sb.AppendLine("read-only: taken from objects already in memory, nothing was requested.");
			sb.AppendLine();

			try
			{
				var inst = VaTagsModule.CurrentInstance();
				sb.AppendLine("=== INSTANCE ===");
				sb.AppendLine("  world       : " + inst.WorldName);
				sb.AppendLine("  world id    : " + inst.WorldId);
				sb.AppendLine("  instance    : #" + inst.InstanceId);
				sb.AppendLine("  region      : " + inst.Region);
				sb.AppendLine("  access      : " + inst.Access);
				sb.AppendLine("  present     : " + inst.Present + (inst.Capacity > 0 ? " / " + inst.Capacity : ""));
				string owner = VaTagsModule.CurrentInstanceOwnerId();
				sb.AppendLine("  owner       : " + (string.IsNullOrEmpty(owner) ? "(public / none)" : owner));
			}
			catch (Exception e) { sb.AppendLine("  (instance read failed: " + e.Message + ")"); }

			sb.AppendLine();
			sb.AppendLine("=== PLAYERS ===");
			int n = 0;
			try
			{
				foreach (var p in VaTagsModule.Roster)
				{
					if (p == null) continue;
					n++;
					sb.AppendLine();
					sb.AppendLine(n + ". " + (p.Name ?? "?") + (p.IsLocal ? "   (you)" : "") + (p.IsOwner ? "   (instance owner)" : ""));
					sb.AppendLine("     user id   : " + (p.UserId ?? ""));
					sb.AppendLine("     player id : " + p.PlayerId);
					sb.AppendLine("     trust     : " + (p.TrustColor ?? ""));
					sb.AppendLine("     platform  : " + (string.IsNullOrEmpty(p.Platform) ? "?" : p.Platform));
					sb.AppendLine("     vrc+      : " + (p.Plus ? "yes" : "no"));
					sb.AppendLine("     18+       : " + (p.Adult ? "verified" : "no"));
					sb.AppendLine("     avatar    : " + (p.AvatarName ?? ""));
					sb.AppendLine("     avatar id : " + (p.AvatarId ?? ""));
					try
					{
						var tags = VaTagsModule.TagsOf(p.UserId);
						if (tags != null && tags.Length > 0)
						{
							var line = new StringBuilder("     va tags   : ");
							for (int i = 0; i < tags.Length; i++)
							{
								if (i > 0) line.Append(" | ");
								line.Append(tags[i].Text);
								if (!string.IsNullOrEmpty(tags[i].Fx) && tags[i].Fx != "none") line.Append(" [" + tags[i].Fx + "]");
							}
							sb.AppendLine(line.ToString());
						}
					}
					catch { }
					try
					{
						Vector3 pos = p.Position;
						sb.AppendLine("     position  : X " + pos.x.ToString("F1") + "  Y " + pos.y.ToString("F1") + "  Z " + pos.z.ToString("F1"));
					}
					catch { }
				}
			}
			catch (Exception e) { sb.AppendLine("  (roster read failed: " + e.Message + ")"); }

			sb.AppendLine();
			sb.AppendLine("total players: " + n);
			return sb.ToString();
		}

		// UI API — finds the LIVE objects that carry VRChat's own UI-building components, so we can
		// call the game's API instead of cloning and hand-painting cards. Static metadata told us
		// these types exist; only a runtime dump says WHICH object holds one and what its prefab
		// fields point at. This is what the debug build is for.
		private static readonly string[] UiTypes =
		{
			"MonoBehaviour1PublicBuclObhesuObGacabuGaUnique",   // page builder: AddButton/AddToggle/AddCategory
			"MonoBehaviour1PublicSebuImicObtoteObLoSpUnique",   // the component ON a button card
		};

		private static string DumpUiApi()
		{
			var sb = new StringBuilder(64 * 1024);
			sb.AppendLine("VRCHAT ARCHIVE MOD — live VRChat UI API objects");
			sb.AppendLine("generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			sb.AppendLine();

			int total = 0;
			foreach (string want in UiTypes)
			{
				sb.AppendLine("=== " + want + " ===");
				int n = 0;
				try
				{
					foreach (var c in Resources.FindObjectsOfTypeAll<Component>())
					{
						if (c == null) continue;
						string tn;
						try { tn = Il2CppName(c); } catch { continue; }
						if (tn != want) continue;
						try { if (!c.gameObject.scene.IsValid()) continue; } catch { continue; }

						sb.AppendLine("  " + PathOf(c.transform) + (c.gameObject.activeInHierarchy ? "" : "   [off]"));
						DescribeMembers(c, sb);
						n++; total++;
						if (n >= 12) { sb.AppendLine("  ... (capped at 12)"); break; }
					}
				}
				catch (Exception e) { sb.AppendLine("  (scan failed: " + e.Message + ")"); }
				if (n == 0) sb.AppendLine("  (no live instance found)");
				sb.AppendLine();
			}

			sb.AppendLine("total live instances: " + total);
			return sb.ToString();
		}

		// Prints the object-typed members so we can see what buttonPrefab / togglePrefab and the
		// icon/text/tooltip references actually point at on a live instance.
		private static void DescribeMembers(Component c, StringBuilder sb)
		{
			try
			{
				Type t = c.GetType();
				foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
				{
					string pn = prop.Name;
					if (pn.StartsWith("field_", StringComparison.Ordinal) || pn.StartsWith("Method_", StringComparison.Ordinal)) continue;
					object v = null;
					try { v = prop.GetValue(c); } catch { continue; }
					if (v == null) { sb.AppendLine("      " + pn + " = null"); continue; }
					string shown;
					try { shown = v is UnityEngine.Object uo ? (uo.name + " <" + Il2CppName((Il2CppObjectBase)v) + ">") : v.ToString(); }
					catch { shown = "?"; }
					sb.AppendLine("      " + pn + " = " + Trunc(shown, 90));
				}
			}
			catch { }
		}
		// ---------------------------------------------------------------- rich tree walker

		private static void Walk(Transform t, int depth, StringBuilder sb, int maxDepth)
		{
			if (t == null || depth > maxDepth) return;
			for (int i = 0; i < depth; i++) sb.Append("  ");
			sb.Append(t.name);

			// real Il2Cpp component type names (GetType().Name only ever said "Component").
			var kinds = new List<string>();
			try
			{
				foreach (var c in t.GetComponents<Component>())
				{
					if (c == null) continue;
					string n = Il2CppName(c);
					if (n == "RectTransform" || n == "Transform" || n == "CanvasRenderer") continue;
					kinds.Add(n + Extra(c, n));
				}
			}
			catch { }
			if (kinds.Count > 0) sb.Append("  [").Append(string.Join(", ", kinds.ToArray())).Append(']');

			var rt = t.TryCast<RectTransform>();
			if (rt != null)
			{
				Rect r = rt.rect;
				sb.Append("  {").Append(Mathf.RoundToInt(r.width)).Append('x').Append(Mathf.RoundToInt(r.height))
				  .Append(" pos(").Append(Mathf.RoundToInt(rt.anchoredPosition.x)).Append(',').Append(Mathf.RoundToInt(rt.anchoredPosition.y)).Append(')')
				  .Append(" a(").Append(F(rt.anchorMin.x)).Append(',').Append(F(rt.anchorMin.y)).Append('-').Append(F(rt.anchorMax.x)).Append(',').Append(F(rt.anchorMax.y)).Append(')')
				  .Append('}');
			}
			if (!t.gameObject.activeSelf) sb.Append(" [off]");
			sb.Append('\n');

			for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb, maxDepth);
		}

		// Extra per-type detail so the dump is actually useful for cloning.
		private static string Extra(Component c, string typeName)
		{
			try
			{
				if (typeName == "TextMeshProUGUI" || typeName == "TextMeshPro")
				{
					var tmp = c.TryCast<TMPro.TMP_Text>();
					if (tmp != null) return $"(\"{Trunc(tmp.text, 24)}\" f={Safe(() => tmp.font != null ? tmp.font.name : "?")} sz={tmp.fontSize:F0})";
				}
				if (typeName == "Image")
				{
					var img = c.TryCast<Image>();
					if (img != null) return $"(sprite={SafeName(img.sprite)} col={Hex(img.color)})";
				}
				if (typeName == "RawImage")
				{
					var ri = c.TryCast<RawImage>();
					if (ri != null) return $"(tex={SafeName(ri.mainTexture)})";
				}
				if (typeName == "AudioSource")
				{
					var a = c.TryCast<AudioSource>();
					if (a != null) return $"(clip={SafeName(a.clip)})";
				}
			}
			catch { }
			return "";
		}

		// ---------------------------------------------------------------- il2cpp helpers

		private static string Il2CppName(Il2CppObjectBase o)
		{
			try
			{
				IntPtr klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
				IntPtr namePtr = IL2CPP.il2cpp_class_get_name(klass);
				string n = Marshal.PtrToStringAnsi(namePtr);
				return string.IsNullOrEmpty(n) ? "?" : n;
			}
			catch { return "?"; }
		}

		private static Transform FindRoot(string name)
		{
			foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
			{
				try
				{
					if (t == null || t.name != name) continue;
					if (t.parent != null) continue;              // root only
					if (!t.gameObject.scene.IsValid()) continue;
					return t;
				}
				catch { }
			}
			// fall back to any live match
			foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
			{
				try { if (t != null && t.name == name && t.gameObject.scene.IsValid()) return t; } catch { }
			}
			return null;
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{ try { var t = asm.GetType(full, false); if (t != null) return t; } catch { } }
			return null;
		}

		private static string PathOf(Transform t)
		{
			var parts = new List<string>();
			int guard = 0;
			while (t != null && guard++ < 40) { parts.Add(t.name); t = t.parent; }
			parts.Reverse();
			return string.Join("/", parts.ToArray());
		}

		private static string F(float v) => v.ToString("0.##");
		private static string SafeName(UnityEngine.Object o) { try { return o != null ? o.name : "null"; } catch { return "?"; } }
		private static string Safe(Func<string> f) { try { return f() ?? "?"; } catch { return "?"; } }
		private static string Hex(Color c) { try { return "#" + ColorUtility.ToHtmlStringRGBA(c); } catch { return "?"; } }
		private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
	}
}
