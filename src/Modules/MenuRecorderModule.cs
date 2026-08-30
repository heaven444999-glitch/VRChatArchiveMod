using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MENU RECORDER — captures VRChat's menus AS YOU BROWSE THEM.
	//
	// A one-shot dump only ever contains the page that happened to be open, which is why the
	// favourites work stalled: the categories exist in a page nobody had dumped, and the earlier
	// dumps carried no TEXT at all, so nothing could be matched to what is actually on screen.
	//
	// This runs continuously instead. Every pass it walks the live menu canvases and writes any
	// object it has NOT SEEN BEFORE, so browsing the menu end to end builds one complete file. Each
	// line carries what a one-shot dump left out:
	//   * the label (TMP text), which is what lets an object be matched to what you clicked
	//   * the sprite name on an Image
	//   * the real component list, rect and active state
	//
	// Strictly read-only and deliberately TEMPORARY — a debugging aid for one round of work, not a
	// feature. It is off by default and costs nothing until switched on.
	public class MenuRecorderModule : IModule
	{
		public override string Name => "MenuRecorder";

		// Roots worth walking. QuickMenu is the wrist menu, the rest is the big menu.
		private static readonly string[] Roots =
		{
			"Canvas_QuickMenu(Clone)", "MenuContent", "UserInterface", "Canvas_MainMenu(Clone)",
		};

		private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
		private StreamWriter _sink;
		private string _path = "";
		private float _next;
		private bool _running;

		public static int Captured { get; private set; }
		public static string Status = "off";

		public override void OnUpdate()
		{
			try
			{
				bool want = ModConfig.MenuRecorder.Value;
				if (want && !_running) Open();
				else if (!want && _running) Close();
				if (!_running) return;

				float now = Time.realtimeSinceStartup;
				if (now < _next) return;
				// Twice a second: fast enough to catch a page you opened and closed again, slow
				// enough that walking the menu tree is not felt.
				_next = now + 0.5f;

				// UserInterface CONTAINS the other roots, so the same subtree is reachable twice.
				// The seen-set already stops duplicate writes; this stops the duplicate walking.
				_walked.Clear();
				foreach (string root in Roots)
				{
					foreach (var t in FindRoots(root))
					{
						try { Walk(t, 0); } catch { }
					}
				}
				try { _sink?.Flush(); } catch { }
				Status = Captured + " object(s) captured";
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuRecorder] {e.Message}"); }
		}

		public override void OnShutdown() => Close();

		private void Open()
		{
			try
			{
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "captures");
				Directory.CreateDirectory(dir);
				_path = Path.Combine(dir, "menurec_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
				_sink = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
				{ AutoFlush = false };
				_sink.WriteLine("VRCHAT MENU RECORDER — everything seen while browsing");
				_sink.WriteLine("started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
				_sink.WriteLine("path  |  components  |  rect  |  \"label\"  |  sprite");
				_sink.WriteLine(new string('-', 100));
				_seen.Clear();
				Captured = 0;
				_running = true;
				Status = "recording";
				VRChatArchiveModPlugin.Logger.LogInfo("[MenuRecorder] recording to " + _path);
			}
			catch (Exception e)
			{
				Status = "could not start: " + Core.Unwrap.Describe(e);
				VRChatArchiveModPlugin.Logger.LogWarning("[MenuRecorder] " + Status);
			}
		}

		private void Close()
		{
			try
			{
				if (_sink != null)
				{
					_sink.WriteLine();
					_sink.WriteLine("total: " + Captured + " object(s)");
					_sink.Flush();
					_sink.Dispose();
					VRChatArchiveModPlugin.Logger.LogInfo($"[MenuRecorder] stopped — {Captured} object(s) in {_path}");
				}
			}
			catch { }
			_sink = null;
			_running = false;
			Status = Captured > 0 ? Captured + " captured (stopped)" : "off";
		}

		public static string LastPath { get; private set; } = "(none yet)";

		private readonly HashSet<int> _walked = new HashSet<int>();

		private void Walk(Transform t, int depth)
		{
			if (t == null || depth > 24) return;
			try { if (!_walked.Add(t.GetInstanceID())) return; } catch { }
			try
			{
				// ACTIVE only: an inactive object is a page you have not opened, and recording those
				// would put the whole menu in the file on the first pass — which is exactly the
				// undifferentiated dump this is meant to replace.
				if (!t.gameObject.activeInHierarchy) return;

				string path = PathOf(t);
				if (_seen.Add(path))
				{
					Write(t, path);
					Captured++;
					LastPath = _path;
				}
			}
			catch { }

			for (int i = 0; i < t.childCount; i++)
			{
				try { Walk(t.GetChild(i), depth + 1); } catch { }
			}
		}

		private void Write(Transform t, string path)
		{
			var sb = new StringBuilder(256);
			sb.Append(path);

			// components
			try
			{
				sb.Append("  [");
				var comps = t.GetComponents<Component>();
				bool first = true;
				if (comps != null)
					foreach (var c in comps)
					{
						if (c == null) continue;
						if (!first) sb.Append(", ");
						first = false;
						sb.Append(Core.MenuCard.Il2CppNameOf(c));
					}
				sb.Append(']');
			}
			catch { }

			// rect
			try
			{
				if (t is RectTransform rt)
					sb.Append("  {").Append((int)rt.rect.width).Append('x').Append((int)rt.rect.height)
					  .Append(" @").Append((int)rt.anchoredPosition.x).Append(',').Append((int)rt.anchoredPosition.y).Append('}');
			}
			catch { }

			// THE LABEL — the piece every previous dump was missing.
			try
			{
				var tmp = t.GetComponent<TMPro.TMP_Text>();
				if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
					sb.Append("  \"").Append(Clean(tmp.text)).Append('"');
			}
			catch { }

			// sprite, for matching an icon to what is drawn
			try
			{
				var img = t.GetComponent<Image>();
				if (img != null && img.sprite != null) sb.Append("  sprite=").Append(img.sprite.name);
			}
			catch { }

			try { _sink?.WriteLine(sb.ToString()); } catch { }
		}

		private static string Clean(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
			return s.Length <= 120 ? s : s.Substring(0, 120) + "…";
		}

		private static List<Transform> FindRoots(string name)
		{
			var outp = new List<Transform>();
			try
			{
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.name != name) continue;
					try
					{
						if (!t.gameObject.scene.IsValid()) continue;
						// NOT "roots only": Canvas_QuickMenu(Clone) and MenuContent are both CHILDREN
						// of UserInterface, so requiring a null parent rejected every one of them and
						// the recorder captured nothing at all.
						outp.Add(t);
					}
					catch { }
				}
			}
			catch { }
			return outp;
		}

		private static string PathOf(Transform t)
		{
			var sb = new StringBuilder(128);
			try
			{
				var stack = new List<string>();
				for (Transform p = t; p != null; p = p.parent) stack.Add(p.name);
				for (int i = stack.Count - 1; i >= 0; i--)
				{
					sb.Append(stack[i]);
					if (i > 0) sb.Append('/');
				}
			}
			catch { }
			return sb.ToString();
		}
	}
}
