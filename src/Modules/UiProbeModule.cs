using System;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Recon tool. VRChat's live menu ("Voyager") is built from obfuscated types whose
	// names rotate every game build, so there is no stable path we can hard-code to
	// clone a native button. This module dumps the *running* UI tree to disk on demand,
	// so the real button/tab paths + labels can be read off and used to build a
	// native-looking menu that mirrors VRChat's own controls.
	//
	// Trigger: press F8 in-game, or the "Dump VRChat UI tree" button in the menu.
	// Output:  <BepInEx>/VRChatArchiveMod/ui_dump_<n>.txt
	//
	// Purely read-only — it never modifies the scene. Every walk is guarded so a weird
	// object can't take the game down.
	public class UiProbeModule : IModule
	{
		public override string Name => "UiProbe";

		private const int MaxTreeLines = 20000;   // hard cap so a huge world UI can't write a gigabyte
		private const int MaxSelectables = 6000;   // controls listed in the summary section

		private string _outDir;
		private bool _autoDone;    // set once the probe has actually captured the favourites data
		private int _autoTries;    // attempts so far (capped, so it never probes forever)
		private float _nextAuto;   // earliest realtime for the next attempt

		public override void OnInitialize()
		{
			try
			{
				_outDir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod");
				Directory.CreateDirectory(_outDir);
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[UiProbe] cannot create output dir: {e.Message}");
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[UiProbe] ready — press F8 to dump the live VRChat UI tree.");
		}

		public override void OnUpdate()
		{
			bool hotkey = false;
			try { hotkey = Input.GetKeyDown(KeyCode.F8); } catch { }

			if (hotkey || Menu.ConsumeUiDumpRequest())
				Dump();

			// AUTOMATIC ONE-SHOT. The owner's ask: "load the mod and boom, magic" — no hotkey, no
			// opening the right menu, no timing. It fires once, forty seconds in, by which point
			// VRChat has built its menu canvases (they do not exist at plugin load).
			//
			// This is only safe because RunTargeted is bounded: three named pages, sixty objects,
			// and a 120 ms stopwatch that abandons the walk rather than overrun it. The unbounded
			// versions of this probe hung the game twice, so nothing here is allowed to run long.
			try
			{
				// RETRIES UNTIL THE DATA EXISTS. Firing once at 40 s was not enough: VRChat may not
				// have logged in or built its favourites by then, and a report saying "null" would
				// mean relaunching the game — the exact manual step this exists to remove. So it
				// tries again every 45 s until the favourites are actually there, and gives up after
				// eight attempts rather than probing forever.
				// DEBUG BUILDS ONLY. This had no gate at all, so it ran in every session: eight
				// rounds of UI-walking capture jobs, each writing files on the main thread, for a
				// diagnostic nobody asked for. It is a tool for working out what VRChat's menus
				// contain — the same reason MenuCapture is off by default now.
				if (!DiagnosticsModule.Debug) return;

				float now = Time.realtimeSinceStartup;
				if (!_autoDone && now > 40f && now >= _nextAuto && _autoTries < 8)
				{
					_autoTries++;
					_nextAuto = now + 45f;

					// Both halves of the answer. CaptureModule runs one job per frame, so they never
					// share a frame's budget with each other.
					if (_autoTries == 1) CaptureModule.Request(CaptureModule.Job.MenuTargeted);  // menu grids: once is enough
					CaptureModule.Request(CaptureModule.Job.AutoAll);                            // favourites + udon

					VRChatArchiveModPlugin.Logger.LogInfo(
						$"[UiProbe] automatic probe queued (attempt {_autoTries}/8).");
				}
				else if (!_autoDone && AutoProbe.GotData)
				{
					_autoDone = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[UiProbe] automatic probe captured the favourites data — done.");
				}
			}
			catch { }

			// F9 — MENU PANEL PROBE, on a hotkey rather than only on a button in our own menu.
			// It has to be a hotkey: the probe reads the panels of whatever VRChat menu is OPEN,
			// and MenuExclusiveModule switches VRChat's menu off while ours is showing. Pressing
			// the in-menu button therefore always probed an empty screen ("no live instance
			// found"). With F9 you open VRChat's Worlds/Social page and capture it in place.
			try
			{
				if (Input.GetKeyDown(KeyCode.F9))
				{
					CaptureModule.Request(CaptureModule.Job.MenuPanels);
					VaTagsModule.LastStatus = "menu panels captured — see BepInEx/VRChatArchiveMod/captures";
					VRChatArchiveModPlugin.Logger.LogInfo("[UiProbe] F9 — menu panel probe requested.");
				}
			}
			catch { }
		}

		private void Dump()
		{
			try
			{
				string path = NextFreePath();
				var sb = new StringBuilder(1 << 20);

				sb.AppendLine("VRCHAT ARCHIVE MOD — UI PROBE DUMP");
				sb.AppendLine("Purpose: locate native VRChat (Voyager) buttons/tabs to clone.");
				sb.AppendLine("Generated by pressing F8 / menu button. Times are real-time seconds since launch: "
					+ SafeTime());
				sb.AppendLine(new string('=', 90));
				sb.AppendLine();

				WriteSelectableSummary(sb);
				sb.AppendLine();
				WriteCanvasTrees(sb);

				File.WriteAllText(path, sb.ToString());
				VRChatArchiveModPlugin.Logger.LogWarning($"[UiProbe] UI tree written to: {path}");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[UiProbe] dump failed: {e}");
			}
		}

		// Section 1: every interactable control (Button / Toggle / Slider / …) currently
		// in a loaded scene, with its full hierarchy path, concrete type and label text.
		// This is the actionable part — pick a Button row and its path is the clone source.
		private static void WriteSelectableSummary(StringBuilder sb)
		{
			sb.AppendLine("### INTERACTABLE CONTROLS (Selectables in loaded scenes) ###");
			int count = 0;
			try
			{
				Il2CppArrayBase<Selectable> all = Resources.FindObjectsOfTypeAll<Selectable>();
				if (all == null) { sb.AppendLine("(none found)"); return; }

				for (int i = 0; i < all.Length && count < MaxSelectables; i++)
				{
					Selectable s = all[i];
					if (s == null) continue;

					GameObject go;
					try { go = s.gameObject; } catch { continue; }
					if (go == null) continue;

					// Skip prefabs / imported assets — only report things actually in a scene.
					try { if (!go.scene.IsValid()) continue; } catch { continue; }

					string type = SafeTypeName(s);
					string label = FindLabel(go);
					string state = go.activeInHierarchy ? "" : " [inactive]";
					sb.Append('[').Append(type).Append("] ").Append(FullPath(go.transform));
					if (label.Length > 0) sb.Append("  →  \"").Append(label).Append('"');
					sb.Append(state).AppendLine();
					count++;
				}
			}
			catch (Exception e) { sb.AppendLine("(enumeration threw: " + e.Message + ")"); }

			sb.AppendLine("---");
			sb.AppendLine("controls listed: " + count + (count >= MaxSelectables ? " (capped)" : ""));
		}

		// Section 2: full transform tree of every Canvas, with component type names and
		// label text, so the structure around a button (containers, icons, text) is visible.
		private static void WriteCanvasTrees(StringBuilder sb)
		{
			sb.AppendLine("### CANVAS TREES ###");
			int lines = 0;
			try
			{
				Il2CppArrayBase<Canvas> canvases = Resources.FindObjectsOfTypeAll<Canvas>();
				if (canvases == null) { sb.AppendLine("(no canvases)"); return; }

				for (int i = 0; i < canvases.Length; i++)
				{
					Canvas c = canvases[i];
					if (c == null) continue;

					GameObject go;
					try { go = c.gameObject; } catch { continue; }
					if (go == null) continue;
					try { if (!go.scene.IsValid()) continue; } catch { continue; }

					sb.AppendLine();
					sb.AppendLine(">>> CANVAS: " + FullPath(go.transform) + (go.activeInHierarchy ? "" : " [inactive]"));
					WalkTransform(go.transform, 0, sb, ref lines);
					if (lines >= MaxTreeLines)
					{
						sb.AppendLine("... (tree cap reached — increase MaxTreeLines to see more)");
						break;
					}
				}
			}
			catch (Exception e) { sb.AppendLine("(canvas walk threw: " + e.Message + ")"); }
		}

		private static void WalkTransform(Transform t, int depth, StringBuilder sb, ref int lines)
		{
			if (t == null || lines >= MaxTreeLines) return;

			try
			{
				string indent = depth <= 0 ? "" : new string(' ', depth * 2);
				GameObject go = t.gameObject;
				string comps = ComponentSummary(go);
				string label = FindLabelSelf(go);

				sb.Append(indent).Append(SafeName(go));
				if (!go.activeSelf) sb.Append(" [off]");
				if (comps.Length > 0) sb.Append("  {").Append(comps).Append('}');
				if (label.Length > 0) sb.Append("  \"").Append(label).Append('"');
				sb.AppendLine();
				lines++;

				int n = t.childCount;
				for (int i = 0; i < n && lines < MaxTreeLines; i++)
					WalkTransform(t.GetChild(i), depth + 1, sb, ref lines);
			}
			catch { /* one bad node must not abort the whole dump */ }
		}

		// --- helpers ---

		private static string ComponentSummary(GameObject go)
		{
			try
			{
				Il2CppArrayBase<Component> comps = go.GetComponents<Component>();
				if (comps == null || comps.Length == 0) return "";
				var sb = new StringBuilder();
				for (int i = 0; i < comps.Length; i++)
				{
					Component c = comps[i];
					if (c == null) continue;
					if (sb.Length > 0) sb.Append(", ");
					sb.Append(SafeTypeName(c));
				}
				return sb.ToString();
			}
			catch { return ""; }
		}

		// Label text on this object OR its descendants (for a button whose text is a child).
		private static string FindLabel(GameObject go)
		{
			try
			{
				var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
				if (tmp != null && !string.IsNullOrEmpty(((TMP_Text)tmp).text)) return Clean(((TMP_Text)tmp).text);
			}
			catch { }
			try
			{
				var txt = go.GetComponentInChildren<Text>(true);
				if (txt != null && !string.IsNullOrEmpty(txt.text)) return Clean(txt.text);
			}
			catch { }
			return "";
		}

		// Label text on this object only (used in the tree so text isn't attributed to a parent).
		private static string FindLabelSelf(GameObject go)
		{
			try
			{
				var tmp = go.GetComponent<TextMeshProUGUI>();
				if (tmp != null && !string.IsNullOrEmpty(((TMP_Text)tmp).text)) return Clean(((TMP_Text)tmp).text);
			}
			catch { }
			try
			{
				var txt = go.GetComponent<Text>();
				if (txt != null && !string.IsNullOrEmpty(txt.text)) return Clean(txt.text);
			}
			catch { }
			return "";
		}

		private static string FullPath(Transform t)
		{
			try
			{
				var sb = new StringBuilder(SafeName(t.gameObject));
				Transform p = t.parent;
				int guard = 0;
				while (p != null && guard++ < 64)
				{
					sb.Insert(0, SafeName(p.gameObject) + "/");
					p = p.parent;
				}
				return sb.ToString();
			}
			catch { return "?"; }
		}

		private static string SafeName(GameObject go)
		{
			try { return go.name; } catch { return "?"; }
		}

		private static string SafeTypeName(Il2CppSystem.Object o)
		{
			try { return o.GetIl2CppType().Name; }
			catch
			{
				try { return o.GetType().Name; } catch { return "?"; }
			}
		}

		private static string Clean(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			s = s.Replace("\r", " ").Replace("\n", " ").Trim();
			return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
		}

		private static string SafeTime()
		{
			try { return Time.realtimeSinceStartup.ToString("0.0"); } catch { return "?"; }
		}

		private string NextFreePath()
		{
			string dir = _outDir ?? ".";
			for (int i = 1; i < 1000; i++)
			{
				string p = Path.Combine(dir, "ui_dump_" + i + ".txt");
				if (!File.Exists(p)) return p;
			}
			return Path.Combine(dir, "ui_dump_overflow.txt");
		}
	}
}
