using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// NATIVE QUICKMENU CONSOLE — a VRChat-Archive console injected into VRChat's own QuickMenu,
	// KARMA-style, so it looks native (real TMP text, the menu's own font) and is themed in the
	// client's pink -> violet.
	//
	// Technique (client-side only, nothing networked, no exploit): VRChat's QuickMenu already
	// carries a small TMP text block for the FPS/ping debug readout. We CLONE that block — which
	// hands us a working TextMeshProUGUI with the correct font for free — repurpose it as our
	// console, drop a themed backdrop behind it, and feed it the filtered Udon events.
	//
	// Runtime paths come from observing the live menu; every lookup is by NAME (robust to VRChat
	// re-parenting things between updates) and every step is logged, so a broken build tells us
	// exactly which node it failed to find instead of silently doing nothing.
	public class QuickMenuConsoleModule : IModule
	{
		public override string Name => "QuickMenuConsole";

		private const float RetrySeconds = 1.5f;
		private const int Lines = 16;

		private float _nextTry;
		private GameObject _root;           // our backdrop
		private TMPro.TextMeshProUGUI _tmp; // the cloned console text
		private TMPro.TextMeshProUGUI _title;
		private Transform _window;          // the QuickMenu "Window" we live under
		private bool _loggedFail;

		public static string State { get; private set; } = "not injected";

		private static string Pink => string.IsNullOrEmpty(ModConfig.QMConsolePink.Value) ? "#FF6AD5" : ModConfig.QMConsolePink.Value;
		private static string Violet => string.IsNullOrEmpty(ModConfig.QMConsoleViolet.Value) ? "#8143E6" : ModConfig.QMConsoleViolet.Value;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.QMConsoleEnabled.Value)
				{
					if (_root != null) { Teardown(); State = "disabled"; }
					return;
				}

				// Alive and injected: just refresh the text.
				if (_root != null && _tmp != null)
				{
					if (_window == null || _root.transform.parent == null) { Teardown(); return; }
					Feed();
					return;
				}

				float now = Time.realtimeSinceStartup;
				if (now < _nextTry) return;
				_nextTry = now + RetrySeconds;
				TryInject();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[QMConsole] update threw: {e}"); }
		}

		public override void OnShutdown() => Teardown();
		public override void OnSceneLoaded(int buildIndex) { /* menu is rebuilt per session, our poll re-injects */ }

		private void TryInject()
		{
			// 1) The FPS/ping debug block: a native dark rounded panel that already contains a
			//    TMP. We clone the WHOLE thing, so its background renders for free (my own Image
			//    backdrop refused to draw over the menu) and the TMP keeps the menu font.
			Transform debug = FindByName("Panel_QM_DebugInfo") ?? FindByName("Panel_QM_DebugInfo(Clone)");
			if (debug == null) { Fail("Panel_QM_DebugInfo not found (QuickMenu not built yet?)"); return; }

			Transform panel = debug.Find("Panel") ?? debug;
			if (panel.GetComponentInChildren<TMPro.TextMeshProUGUI>(true) == null) { Fail("no TMP under Panel_QM_DebugInfo"); return; }

			// 2) Dock target: the Launchpad page's CONTENT viewport. Filling it makes the
			//    console occupy the exact page area (integrated, native), while the menu's own
			//    header ("Launch Pad") and the bottom Home/Respawn chin stay untouched.
			//    Reversed from the live tree: QMParent/Body/Menu_QM_Launchpad/ScrollRect/Viewport.
			Transform launchpad = FindByName("Menu_QM_Launchpad");
			Transform dock = launchpad != null ? FindDeepChild(launchpad, "Viewport") : null;
			if (dock == null) { Fail("Launchpad Viewport not found (open the QuickMenu on the Launch Pad page)"); return; }
			_window = dock;

			// 3) Clone the whole panel -> our console, docked to FILL the content viewport.
			var cloneGo = UnityEngine.Object.Instantiate(panel.gameObject, dock);
			cloneGo.name = "VAConsole";
			cloneGo.SetActive(true);
			var rt = cloneGo.GetComponent<RectTransform>();
			rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
			rt.pivot = new Vector2(0.5f, 0.5f);
			rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
			rt.localScale = Vector3.one;
			rt.SetAsLastSibling();                     // on top of the launchpad cards
			_root = cloneGo;

			// The FLOATING-TEXT bug: the cloned FPS-pill background was a small child rect. I
			// recoloured it but never STRETCHED it, so it stayed pill-sized while the text ran
			// past it onto the banner. Fix: stretch the background image to fill the whole box
			// and force it opaque so nothing shows through.
			Image bg = cloneGo.GetComponent<Image>();
			if (bg == null) bg = cloneGo.GetComponentInChildren<Image>(true);
			if (bg != null)
			{
				var brt = bg.rectTransform;
				brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
				brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
				bg.color = new Color(0.05f, 0.03f, 0.09f, 1f);
			}
			var mask = cloneGo.GetComponent<RectMask2D>(); if (mask == null) cloneGo.AddComponent<RectMask2D>();

			// 4) Body TMP: the cloned panel's own text, stretched to fill with padding.
			_tmp = cloneGo.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
			var trt = _tmp.rectTransform;
			trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f);
			trt.pivot = new Vector2(0.5f, 0.5f);
			trt.offsetMin = new Vector2(14f, 12f); trt.offsetMax = new Vector2(-14f, -42f);
			_tmp.enabled = true; _tmp.richText = true; _tmp.fontSize = 15f;
			_tmp.alignment = TMPro.TextAlignmentOptions.BottomLeft; _tmp.color = Color.white;
			try { _tmp.enableWordWrapping = false; } catch { }
			try { _tmp.overflowMode = TMPro.TextOverflowModes.Truncate; } catch { }
			_tmp.text = "";

			// 5) Title strip (its own TMP) + pink/violet edges.
			_title = CloneTmp(_tmp, rt, new Vector2(14f, -8f), new Vector2(572f, 28f), 19f, TMPro.TextAlignmentOptions.Left);
			_title.text = Gradient("VRCHAT  ARCHIVE  —  UDON");
			Bar(rt, new Vector2(0f, 1f), new Vector2(1f, 1f), 0f, 0f, 3f, ParseHex(Pink));
			Bar(rt, new Vector2(0f, 0f), new Vector2(1f, 0f), 0f, 0f, 3f, ParseHex(Violet));

			State = "injected";
			VRChatArchiveModPlugin.Logger.LogInfo("[QMConsole] injected (cloned native panel, themed).");
			DumpTreeOnce(dock);
			_loggedFail = false;
			Feed();
		}

		// Clone the source TMP (keeps the menu's font asset), reparent + reconfigure.
		private static TMPro.TextMeshProUGUI CloneTmp(TMPro.TextMeshProUGUI src, RectTransform parent, Vector2 topLeft, Vector2 size, float fontSize, TMPro.TextAlignmentOptions align)
		{
			var go = UnityEngine.Object.Instantiate(src.gameObject, parent);
			go.SetActive(true);
			var rt = go.GetComponent<RectTransform>();
			rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
			rt.pivot = new Vector2(0f, 1f);
			rt.anchoredPosition = topLeft;
			rt.sizeDelta = size;
			rt.localScale = Vector3.one;
			var tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
			tmp.enabled = true;
			tmp.richText = true;
			tmp.fontSize = fontSize;
			tmp.alignment = align;
			tmp.color = Color.white;
			try { tmp.enableWordWrapping = false; } catch { }
			try { tmp.overflowMode = TMPro.TextOverflowModes.Truncate; } catch { }
			return tmp;
		}

		private void Feed()
		{
			try
			{
				var rows = UdonLogModule.Visible();
				int start = Mathf.Max(0, rows.Count - Lines);
				var sb = new System.Text.StringBuilder(1024);
				sb.Append("<color=").Append(Pink).Append(">").Append(UdonLogModule.PerSecond).Append("/s</color>   ")
				  .Append("<color=#5A6B7A>").Append(UdonLogModule.TotalSeen).Append(" total</color>\n");
				for (int i = start; i < rows.Count; i++)
				{
					var e = rows[i];
					string who = string.IsNullOrEmpty(e.User) ? "<color=#5A6B7A>world</color>" : "<color=" + Violet + ">" + Short(e.User, 16) + "</color>";
					sb.Append("<color=#5A6B7A>").Append(e.Clock).Append("</color> ")
					  .Append(who).Append(" <color=#C58BFF>").Append(Short(e.Obj, 18)).Append("</color> ")
					  .Append("<color=#8FE9B0>").Append(Short(e.Event, 30)).Append("</color>");
					if (e.Repeats > 1) sb.Append(" <color=#FFB86B>x").Append(e.Repeats).Append("</color>");
					sb.Append('\n');
				}
				if (rows.Count == 0) sb.Append("<color=#5A6B7A>no event yet — join a world with interactive objects</color>");
				_tmp.text = sb.ToString();
				if (_title != null) _title.text = Gradient("VRCHAT  ARCHIVE  —  UDON");
			}
			catch { }
		}

		// One-time reverse-engineering dump of the LIVE QuickMenu, so the layout can be
		// docked properly instead of floated. Walks up to the menu root and writes the whole
		// subtree — names, component kinds, rect size/pos — to a file we can read back.
		private static bool _dumped;
		private static void DumpTreeOnce(Transform window)
		{
			if (_dumped) return;
			_dumped = true;
			try
			{
				Transform root = window;
				for (int i = 0; i < 4 && root.parent != null; i++) root = root.parent;   // Window -> Container -> CanvasGroup -> QuickMenu root

				var sb = new StringBuilder(64 * 1024);
				sb.Append("QUICKMENU TREE  (root='").Append(root.name).Append("')  ")
				  .Append(DateTime.Now.ToString("HH:mm:ss")).Append('\n');
				Walk(root, 0, sb);

				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "qm_tree.txt");
				File.WriteAllText(path, sb.ToString());
				VRChatArchiveModPlugin.Logger.LogInfo("[QMConsole] QuickMenu tree dumped -> " + path);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[QMConsole] tree dump failed: " + e.Message); }
		}

		private static void Walk(Transform t, int depth, StringBuilder sb)
		{
			if (t == null || depth > 10) return;
			for (int i = 0; i < depth; i++) sb.Append("  ");
			sb.Append(t.name);

			var kinds = new List<string>();
			try
			{
				foreach (var c in t.GetComponents<Component>())
				{
					if (c == null) continue;
					string n = c.GetType().Name;
					if (n == "RectTransform" || n == "Transform" || n == "CanvasRenderer") continue;
					kinds.Add(n);
				}
			}
			catch { }
			if (kinds.Count > 0) sb.Append("  [").Append(string.Join(",", kinds.ToArray())).Append("]");

			var rt = t?.TryCast<RectTransform>();
			if (rt != null)
			{
				sb.Append("  size=(").Append(Mathf.RoundToInt(rt.rect.width)).Append('x').Append(Mathf.RoundToInt(rt.rect.height)).Append(")");
				sb.Append(t.gameObject.activeInHierarchy ? "" : " [off]");
			}
			sb.Append('\n');

			for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
		}

		private void Teardown()
		{
			try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
			_root = null; _tmp = null; _title = null; _window = null;
			State = "not injected";
		}

		private void Fail(string why)
		{
			State = "waiting: " + why;
			if (!_loggedFail) { _loggedFail = true; VRChatArchiveModPlugin.Logger.LogInfo("[QMConsole] " + why + " — will retry."); }
		}

		// ---- helpers ----

		private static void Bar(RectTransform parent, Vector2 aMin, Vector2 aMax, float x, float y, float thick, Color c)
		{
			var go = new GameObject("edge");
			var rt = go.AddComponent<RectTransform>();
			rt.SetParent(parent, false);
			rt.anchorMin = aMin; rt.anchorMax = aMax;
			rt.pivot = new Vector2(0.5f, aMin.y);
			rt.sizeDelta = new Vector2(0f, thick);
			rt.anchoredPosition = new Vector2(x, y);
			go.AddComponent<Image>().color = c;
		}

		// Depth-first child search by name (the viewport is nested under ScrollRect).
		private static Transform FindDeepChild(Transform root, string name)
		{
			if (root == null) return null;
			for (int i = 0; i < root.childCount; i++)
			{
				var c = root.GetChild(i);
				if (c.name == name) return c;
				var r = FindDeepChild(c, name);
				if (r != null) return r;
			}
			return null;
		}

		private static Transform FindByName(string name)
		{
			try
			{
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.name != name) continue;
					if (t.hideFlags == HideFlags.HideAndDontSave) continue;
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { }
					return t;
				}
			}
			catch { }
			return null;
		}

		private static Color ParseHex(string hex)
			=> ColorUtility.TryParseHtmlString(hex, out Color c) ? c : Color.magenta;

		// Per-character pink -> violet gradient as TMP rich text.
		private static string Gradient(string text)
		{
			Color a = ParseHex(Pink), b = ParseHex(Violet);
			var sb = new System.Text.StringBuilder(text.Length * 20);
			for (int i = 0; i < text.Length; i++)
			{
				float k = text.Length == 1 ? 0f : (float)i / (text.Length - 1);
				sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Color.Lerp(a, b, k)))
				  .Append('>').Append(text[i]).Append("</color>");
			}
			return sb.ToString();
		}

		private static string Short(string s, int n)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, Mathf.Max(1, n - 1)) + "…");
	}
}
