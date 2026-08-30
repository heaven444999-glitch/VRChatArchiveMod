using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PLAYERS INSIDE THE QUICKMENU WING.
	//
	// The instance roster used to be a floating IMGUI window drawn over the game. This puts the
	// same data where it belongs: appended to VRChat's own left wing, so it opens with the wing,
	// scrolls with it, and looks like the rest of the menu instead of sitting on top of the world.
	//
	// Paths from a live capture (captures/ui_*.txt):
	//   wing    : Canvas_QuickMenu(Clone)/CanvasGroup/Container/Window/Wing_Left
	//   page    : <wing>/.../WingMenu
	//   content : <page>/ScrollRect/Viewport/VerticalLayoutGroup   (VerticalLayoutGroup + ContentSizeFitter)
	//   row     : one of that content's Button_* children — a 356x112 row carrying
	//             Container/{Background, Icon, Text_QM_H3, Icon_Arrow, Separator}
	//
	// Rows are CLONED from a live, already-styled row (cloning something VRChat never displays
	// yields unstyled white), POOLED (a clone per player is created once and then only its text is
	// rewritten), and rebuilt only when the roster actually changes — never per frame.
	public class WingPlayersModule : IModule
	{
		public override string Name => "WingPlayers";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";
		private const string WingPath = "CanvasGroup/Container/Window/Wing_Left";
		private const string ContainerName = "VA_PlayersSection";
		private const int MaxRows = 40;

		private Transform _content;      // the wing menu's scroll content
		private Transform _rowDonor;     // a live styled row to clone
		private Transform _section;      // our own container inside the content
		private readonly List<Transform> _rows = new List<Transform>();

		private float _nextTry;
		private int _fails;
		private int _lastSignature = -1;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.WingPlayersEnabled.Value)
				{
					if (_section != null) { try { UnityEngine.Object.Destroy(_section.gameObject); } catch { } _section = null; _rows.Clear(); }
					return;
				}

				float now = Time.realtimeSinceStartup;
				if (_section == null)
				{
					if (now < _nextTry) return;
					_nextTry = now + 3f;
					if (_fails > 40) return;
					if (!TryBuild()) { _fails++; return; }
				}

				// Only touch the UI when the roster really changed.
				int sig = RosterSignature();
				if (sig == _lastSignature) return;
				_lastSignature = sig;
				Refresh();
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[WingPlayers] update threw: {e.Message}");
				_fails++;
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			_lastSignature = -1;
			if (_section == null) { _content = null; _rowDonor = null; _fails = 0; _nextTry = 0f; }
		}

		public override void OnShutdown()
		{
			try { if (_section != null) UnityEngine.Object.Destroy(_section.gameObject); } catch { }
			_section = null; _rows.Clear();
		}

		// ---------------------------------------------------------------- build

		private bool TryBuild()
		{
			Transform qm = FindQuickMenuRoot();
			if (qm == null) return false;
			Transform wing = qm.Find(WingPath);
			if (wing == null) return false;

			Transform page = FindDeep(wing, "WingMenu", 6);
			if (page == null) return false;

			Transform content = FindContent(page);
			if (content == null) return false;
			_content = content;

			// A live row from this very list: it is on screen, so VRChat has already styled it.
			for (int i = 0; i < content.childCount; i++)
			{
				var c = content.GetChild(i);
				if (c == null || !c.name.StartsWith("Button_", StringComparison.Ordinal)) continue;
				if (c.name == ContainerName) continue;
				_rowDonor = c; break;
			}
			if (_rowDonor == null) return false;

			var go = new GameObject(ContainerName, new Il2CppSystem.Type[]
			{
				Il2CppType.Of<RectTransform>(), Il2CppType.Of<VerticalLayoutGroup>(), Il2CppType.Of<ContentSizeFitter>(),
			});
			go.transform.SetParent(content, false);
			go.transform.SetAsLastSibling();

			var vlg = go.GetComponent<VerticalLayoutGroup>();
			if (vlg != null) { vlg.childForceExpandHeight = false; vlg.childControlHeight = true; vlg.childForceExpandWidth = true; }
			var csf = go.GetComponent<ContentSizeFitter>();
			if (csf != null) csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			_section = go.transform;
			VRChatArchiveModPlugin.Logger.LogInfo("[WingPlayers] section added to the left wing menu.");
			return true;
		}

		private static Transform FindContent(Transform page)
		{
			string[] paths = { "ScrollRect/Viewport/VerticalLayoutGroup", "Scrollrect/Viewport/VerticalLayoutGroup" };
			foreach (string p in paths) { var t = page.Find(p); if (t != null) return t; }
			return null;
		}

		// ---------------------------------------------------------------- rows

		// Cheap change detector: player count plus a hash of the ids. Rebuilding the wing every
		// frame would be absurd for a list that changes on join/leave.
		private static int RosterSignature()
		{
			try
			{
				int h = 17;
				var r = VaTagsModule.Roster;
				h = h * 31 + r.Count;
				for (int i = 0; i < r.Count; i++)
				{
					var p = r[i];
					if (p == null) continue;
					h = h * 31 + (p.UserId != null ? p.UserId.GetHashCode() : 0);
					h = h * 31 + (p.Plus ? 1 : 0) + (p.Adult ? 2 : 0);
				}
				return h;
			}
			catch { return -1; }
		}

		private void Refresh()
		{
			if (_section == null || _rowDonor == null) return;
			var roster = VaTagsModule.Roster;
			int want = Mathf.Min(roster.Count, MaxRows);

			// Grow the pool only when a new high-water mark is reached; rows are reused after that.
			while (_rows.Count < want)
			{
				var clone = UnityEngine.Object.Instantiate(_rowDonor.gameObject, _section);
				clone.name = "VA_Row" + _rows.Count;
				StripRow(clone.transform);
				_rows.Add(clone.transform);
			}

			for (int i = 0; i < _rows.Count; i++)
			{
				var row = _rows[i];
				if (row == null) continue;
				bool used = i < want;
				if (row.gameObject.activeSelf != used) row.gameObject.SetActive(used);
				if (!used) continue;
				Fill(row, roster[i]);
			}
		}

		// VRChat's own row handler would still run the donor's navigation, so it goes; the children
		// keep their components, which is what gives the row its real font and sprites.
		private void StripRow(Transform row)
		{
			try
			{
				foreach (var c in row.GetComponents<Component>())
				{
					if (c == null) continue;
					string n = Il2CppName(c);
					if (n == "RectTransform" || n == "CanvasRenderer" || n == "LayoutElement" || n == "CanvasGroup"
						|| n == "Image" || n == "ImageEx" || n == "UIInvisibleGraphic"
						|| n == "HorizontalLayoutGroup" || n == "VerticalLayoutGroup" || n == "Button") continue;
					try { UnityEngine.Object.DestroyImmediate(c); } catch { }
				}
			}
			catch { }

			try
			{
				// Unity allows only ONE Graphic per object and these rows already carry a
				// UIInvisibleGraphic, so adding an Image here would return null.
				var g = row.GetComponent<Graphic>();
				if (g == null)
				{
					var img = row.gameObject.AddComponent<Image>();
					img.color = new Color(0f, 0f, 0f, 0f);
					g = img;
				}
				g.raycastTarget = true;

				var btn = row.GetComponent<Button>() ?? row.gameObject.AddComponent<Button>();
				btn.targetGraphic = g;
				btn.interactable = true;
				try { btn.onClick.RemoveAllListeners(); } catch { }

				int index = _rows.Count;   // captured: this row's slot
				Core.UiClick.AddClick(btn, () => OnRowClick(index));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[WingPlayers] row wiring failed: {e.Message}"); }
		}

		private static void Fill(Transform row, VaTagsModule.PlayerEntry p)
		{
			if (p == null) return;
			try
			{
				string badges = "";
				if (p.Plus)  badges += " <color=#FFD24A>VRC+</color>";
				if (p.Adult) badges += " <color=#FF7AB8>18+</color>";
				if (!string.IsNullOrEmpty(p.Platform))
					badges += p.Platform == "Quest" ? " <color=#7CFF9E>Quest</color>"
					        : p.Platform == "PC"    ? " <color=#7FB0FF>PC</color>"
					        : " <color=#8FA3B8>" + p.Platform + "</color>";

				string col = string.IsNullOrEmpty(p.TrustColor) ? "#EAF6FF" : p.TrustColor;
				string name = "<color=" + col + ">" + Trunc(p.Name, 22) + "</color>"
					+ (p.IsLocal ? " <color=#5A6B7A>(you)</color>" : "")
					+ (p.IsOwner ? " <color=#FFD24A>♛</color>" : "");

				var tmp = row.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (tmp != null) tmp.text = name + badges;
			}
			catch { }
		}

		private void OnRowClick(int index)
		{
			try
			{
				var roster = VaTagsModule.Roster;
				if (index < 0 || index >= roster.Count) return;
				var p = roster[index];
				if (p == null || string.IsNullOrEmpty(p.UserId)) return;
				Core.Menu.SelectPlayer(p.UserId, p.Name);
				Core.Menu.OpenPlayers();
			}
			catch { }
		}

		// ---------------------------------------------------------------- helpers

		private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

		private static Transform FindDeep(Transform root, string name, int depth)
		{
			if (root == null || depth < 0) return null;
			for (int i = 0; i < root.childCount; i++)
			{
				var c = root.GetChild(i);
				if (c == null) continue;
				if (c.name == name) return c;
				var hit = FindDeep(c, name, depth - 1);
				if (hit != null) return hit;
			}
			return null;
		}

		private static Transform FindQuickMenuRoot()
		{
			try
			{
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.name != QmRoot) continue;
					try
					{
						if (!t.gameObject.scene.IsValid()) continue;
						if (t.Find("CanvasGroup/Container/Window") == null) continue;
						return t;
					}
					catch { }
				}
			}
			catch { }
			return null;
		}

		private static string Il2CppName(Il2CppObjectBase o)
		{
			try
			{
				IntPtr klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
				string n = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
				return string.IsNullOrEmpty(n) ? "?" : n;
			}
			catch { return "?"; }
		}
	}
}
