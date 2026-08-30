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
	// MOD FEATURES on VRChat's per-user menu.
	//
	// When you click someone in the QuickMenu, VRChat opens Menu_SelectedUser_Local — the page
	// with Friend Request / Block / Vote to Kick and so on. This adds one more card to that page,
	// "MOD FEATURES", which opens our own PLAYERS tab with THAT user already selected, so TP,
	// CLONE and tag management apply to the person you just clicked.
	//
	// Paths come from a live capture (captures/ui_*.txt):
	//   page  : .../Window/QMParent/Body/Menu_SelectedUser_Local
	//   name  : <page>/ScrollRect/Viewport/VerticalLayoutGroup/UserProfile_Compact/UserDetails/Info/
	//           {Text_Username_Friend | Text_Username_NonFriend}
	//   grids : Buttons_UserActions / Buttons_PerUserInteraction / Buttons_AvatarActions
	//
	// The card is CLONED from one already on that page, so it inherits VRChat's resolved style —
	// cloning from a page the game never opens produces unstyled white cards.
	public class UserMenuModule : IModule
	{
		public override string Name => "UserMenu";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";
		private const string BodyPath = "CanvasGroup/Container/Window/QMParent/Body";
		// BOTH selected-user pages. VRChat uses a different page for yourself and for everyone
		// else, and this module only ever knew about the local one — so the cards were missing on
		// exactly the menu people open the most, another player's. Names are tried in order and
		// every page that exists gets its own set of cards.
		private static readonly string[] PagePaths =
		{
			"Menu_SelectedUser_Remote",
			"Menu_SelectedUser_Local",
		};
		private const string InfoPath = "ScrollRect/Viewport/VerticalLayoutGroup/UserProfile_Compact/UserDetails/Info";
		private const string CardName = "Button_VAModFeatures";
		private const string CloneCardName = "Button_VACloneAvatar";
		private const string CopyCardName = "Button_VACopyAvi";

		private Transform _page;
		private GameObject _card;
		private GameObject _cloneCard;
		private GameObject _copyCard;
		private float _nextTry;
		private int _fails;
		private string _lastFailReason = "";
		private bool _bodyDumped;
		private float _nextCardRefresh;
		private bool _popupOpen;
		private string _popupFor = "";
		private string _popupName = "";
		private string _cloneCardState = "";   // "public" / "private" / "" — what the card currently shows
		// Pages already carrying our cards, so a second pass does not rebuild them.
		private readonly HashSet<string> _injected = new HashSet<string>(StringComparer.Ordinal);

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.UserMenuEnabled.Value)
				{
					if (_card != null) { try { UnityEngine.Object.Destroy(_card); } catch { } _card = null; }
					if (_cloneCard != null) { try { UnityEngine.Object.Destroy(_cloneCard); } catch { } _cloneCard = null; }
					return;
				}
				// The card has to follow the SELECTED user, so this runs whether or not there is
				// still injecting left to do — it is the only thing that keeps the label honest as
				// you move from one person's page to the next.
				RefreshCloneCard();

				// A popup that outlived the menu it belongs to would float over the world with no
				// way back to it.
				if (_popupOpen)
				{
					bool qm = false;
					try { qm = Core.QuickMenu.MainVisible; } catch { }
					if (!qm) _popupOpen = false;
				}

				// Done only when EVERY page that exists has been handled.
				if (_injected.Count >= PagePaths.Length) return;

				float now = Time.realtimeSinceStartup;
				if (now < _nextTry) return;
				// Back off rather than stop. The old code gave up permanently after 40 tries (two
				// minutes) and said nothing, so opening a user menu later in a session found no
				// cards and no explanation anywhere.
				_nextTry = now + (_fails < 20 ? 3f : 15f);
				if (!TryInject()) _fails++;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[UserMenu] update threw: {e.Message}");
				_fails++;
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// The quick menu is rebuilt with the scene, so our cards are gone with it.
			_injected.Clear();
			_page = null; _card = null; _cloneCard = null; _copyCard = null;
			_fails = 0; _nextTry = 0f; _lastFailReason = "";
		}

		private bool TryInject()
		{
			Transform qm = Core.QuickMenu.Root();
			if (qm == null) return Fail("quick menu root not found");
			Transform body = qm.Find(BodyPath);
			if (body == null) return Fail("quick menu body path not found: " + BodyPath);

			// MATCH BY PATTERN, NOT BY EXACT NAME. The two names below were right for one build and
			// wrong for the next — VRChat renames these pages, and an exact Find() then reports
			// "no page" for a menu that is plainly on screen. Any direct child of Body whose name
			// looks like a per-user page is a candidate, so a rename no longer takes the feature
			// down; the known names simply get tried first.
			int built = 0;
			foreach (string pageName in PagePaths)
			{
				if (_injected.Contains(pageName)) { built++; continue; }
				Transform p = body.Find(pageName);
				if (p == null) continue;
				if (InjectInto(p, pageName)) { _injected.Add(pageName); built++; }
			}

			if (built == 0)
			{
				for (int i = 0; i < body.childCount; i++)
				{
					Transform c = body.GetChild(i);
					if (c == null) continue;
					string n = c.name;
					if (string.IsNullOrEmpty(n) || _injected.Contains(n)) continue;
					// "Menu_SelectedUser_Remote", "Menu_User", "UserDetails_Page"… all qualify;
					// the grid lookup inside InjectInto is what actually accepts or rejects it.
					if (n.IndexOf("User", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (n.IndexOf("Menu_", StringComparison.OrdinalIgnoreCase) < 0
						&& n.IndexOf("Page", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (InjectInto(c, n)) { _injected.Add(n); built++; }
				}
			}

			if (built == 0)
			{
				// Name every page Body actually holds, once — the only thing that can tell us what
				// these are called on this build instead of another round of guessing.
				if (!_bodyDumped)
				{
					_bodyDumped = true;
					var names = new List<string>();
					for (int i = 0; i < body.childCount; i++)
					{
						Transform c = body.GetChild(i);
						if (c != null) names.Add(c.name);
					}
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[UserMenu] pages under Body: " + (names.Count == 0 ? "(none)" : string.Join(", ", names)));
				}
				return Fail("no per-user page under Body (tried: " + string.Join(", ", PagePaths) + " + pattern match)");
			}
			return true;
		}

		// Says WHY, once per distinct reason. A feature that silently does not appear is the worst
		// kind of bug to chase from a screenshot.
		private bool Fail(string why)
		{
			if (!string.Equals(_lastFailReason, why, StringComparison.Ordinal))
			{
				_lastFailReason = why;
				VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] not injected: " + why);
			}
			return false;
		}

		// CLONE AVATAR SAYS WHETHER IT CAN WORK.
		//
		// A private avatar cannot be worn by anybody — VRChat does not serve it — so a card that
		// looks identical either way invites a press that can only fail. The label carries its own
		// colour (a TMP rich-text tag, which the game's own re-theming cannot overwrite), so one
		// write sets both the wording and the green/red.
		private const string CloneLabelPublic  = "<color=#7CFF9E>Clone Avatar</color>";
		private const string CloneLabelPrivate = "<color=#FF6B6B>AVI PRIVATE</color>";
		private const string CloneLabelUnknown = "Clone Avatar";

		private void RefreshCloneCard()
		{
			if (_cloneCard == null || _page == null) return;
			float now = Time.realtimeSinceStartup;
			if (now < _nextCardRefresh) return;
			_nextCardRefresh = now + 0.5f;

			// Only while the page is actually up: reading the roster and the avatar record for a
			// menu nobody is looking at is pure cost.
			bool visible = false;
			try { visible = _page.gameObject.activeInHierarchy; } catch { }
			if (!visible) return;

			string state = "";
			try
			{
				var entry = SelectedEntry(out _);
                if (entry != null) state = VaTagsModule.ReleaseStatusOf(entry);
			}
			catch { }

			if (string.Equals(state, _cloneCardState, StringComparison.Ordinal)) return;   // no change
			_cloneCardState = state;

			string label = state == "private" ? CloneLabelPrivate
				: state == "public" ? CloneLabelPublic
				: CloneLabelUnknown;
			try
			{
				var tmp = _cloneCard.transform.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (tmp != null) { tmp.richText = true; tmp.text = label; }
			}
			catch { }
		}

		private bool InjectInto(Transform page, string pageName)
		{
			_page = page;

			Transform grid = FindGrid(page);
			if (grid == null) return Fail("no button grid on page " + pageName);

			Transform donor = null;
			for (int i = 0; i < grid.childCount; i++)
			{
				var c = grid.GetChild(i);
				if (c == null) continue;
				if (c.name == CardName || c.name == CloneCardName || c.name == CopyCardName) continue;   // never clone ourselves
				if (c.name.StartsWith("Button_", StringComparison.Ordinal)) { donor = c; break; }
			}
			if (donor == null) return false;

			// Build while the page is ACTIVE, otherwise Unity never runs Awake on the clone and
			// VRChat's own text component is still uninitialised when we set the label.
			bool wasActive = page.gameObject.activeSelf;
			try { page.gameObject.SetActive(true); } catch { }

			_card = Ensure(grid, donor, CardName, "Mod Features", OnClick);
			// CLONE AVATAR right here. It used to mean: open our menu, find the PLAYERS tab, find the
			// row, press CLONE \u2014 four steps to do the one thing people open a user's page for.
			_cloneCard = Ensure(grid, donor, CloneCardName, "Clone Avatar", OnClone);
			_copyCard = Ensure(grid, donor, CopyCardName, "Copy Avatar Id", OnCopyAvi);

			try { page.gameObject.SetActive(wasActive); } catch { }

			VRChatArchiveModPlugin.Logger.LogInfo("[UserMenu] cards added to " + pageName
				+ " (Mod Features, Clone Avatar, Copy Avatar Id).");
			return _card != null && _cloneCard != null;
		}

		// One card, created once and reused on every later pass.
		private static GameObject Ensure(Transform grid, Transform donor, string name, string label, Action onClick)
		{
			try
			{
				Transform existing = grid.Find(name);
				if (existing != null) return existing.gameObject;

				var go = UnityEngine.Object.Instantiate(donor.gameObject, grid);
				go.name = name;
				go.SetActive(true);
				go.transform.SetAsLastSibling();
				// keepStyle: these are plain action cards with no lit/unlit state of ours to show, so
				// VRChat's own StyleElement is left on the root and the game themes them exactly like
				// the neighbours they sit between. Deleting it is what made MOD FEATURES render black.
				Core.MenuCard.Setup(go.transform, donor, label, onClick, lit: false, keepStyle: true);
				return go;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[UserMenu] could not add '{label}': {e.Message}");
				return null;
			}
		}

		private static Transform FindGrid(Transform page)
		{
			string[] names = { "Buttons_UserActions", "Buttons_PerUserInteraction", "Buttons_AvatarActions" };
			foreach (string n in names)
			{
				var t = FindDeep(page, n);
				if (t == null) continue;
				// The group may hold the cards directly or wrap them in a "Buttons" child.
				var inner = t.Find("Buttons");
				Transform g = inner != null ? inner : t;
				for (int i = 0; i < g.childCount; i++)
					if (g.GetChild(i).name.StartsWith("Button_", StringComparison.Ordinal)) return g;
			}
			return null;
		}

		private static Transform FindDeep(Transform root, string name, int depth = 8)
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

		// The roster entry for whoever this page is showing, or null. Both cards need it, and
		// neither can do anything useful without it.
		private VaTagsModule.PlayerEntry SelectedEntry(out string name)
		{
			name = SelectedUserName();
			if (string.IsNullOrEmpty(name)) return null;
			try
			{
				foreach (var p in VaTagsModule.Roster)
				{
					if (p == null) continue;
					if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
				}
			}
			catch { }
			return null;
		}

		// Open OUR menu on the PLAYERS tab with the user this page is showing already selected,
		// which is where TP / CLONE / tag management already live.
		// THE SUB-MENU. Drawn in the mod's own IMGUI layer rather than cloned out of VRChat's UI:
		// these are three switches that need to reflect live state, and a native page would have to
		// be rebuilt every time the selection changed. It closes on its own when the quick menu
		// goes away, so it can never be left floating over the world.
		public override void OnGui()
		{
			if (!_popupOpen) return;
			if (Event.current.type != EventType.Repaint && Event.current.type != EventType.MouseDown
				&& Event.current.type != EventType.MouseUp) { }

			VaTagsModule.PlayerEntry target = null;
			try
			{
				foreach (var p in VaTagsModule.Roster)
					if (p != null && string.Equals(p.UserId, _popupFor, StringComparison.OrdinalIgnoreCase))
					{ target = p; break; }
			}
			catch { }
			if (target == null) { _popupOpen = false; return; }

			float w = Core.Hud.S(320f), rowH = Core.Hud.S(38f);
			float h = Core.Hud.S(56f) + rowH * 4f;
			var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.30f, w, h);

			var body = Core.Hud.Panel(r, "MOD FEATURES", Trunc(_popupName, 18));
			float y = body.y + Core.Hud.S(4f);
			float pad = Core.Hud.S(8f);
			float bw = body.width - pad * 2f;

			bool orbiting = OrbitModule.Active && OrbitModule.Current == OrbitModule.Mode.Orbit
				&& string.Equals(OrbitModule.TargetUid, target.UserId, StringComparison.OrdinalIgnoreCase);
			bool sitting = OrbitModule.Active && OrbitModule.Current == OrbitModule.Mode.Sit
				&& string.Equals(OrbitModule.TargetUid, target.UserId, StringComparison.OrdinalIgnoreCase);
			bool ringing = ObjectOrbitModule.Active
				&& string.Equals(ObjectOrbitModule.CenterName, target.Name, StringComparison.Ordinal);

			bool o2 = GuiKit.Toggle(new Rect(body.x + pad, y, bw, rowH - 4f), "Orbit them", orbiting);
			if (o2 != orbiting) OrbitModule.Toggle(OrbitModule.Mode.Orbit, target);
			y += rowH;

			bool s2 = GuiKit.Toggle(new Rect(body.x + pad, y, bw, rowH - 4f), "Sit on them", sitting);
			if (s2 != sitting) OrbitModule.Toggle(OrbitModule.Mode.Sit, target);
			y += rowH;

			bool r2 = GuiKit.Toggle(new Rect(body.x + pad, y, bw, rowH - 4f), "Orbit objects around them", ringing);
			if (r2 != ringing) ObjectOrbitModule.ToggleOnPlayer(target);
			y += rowH + Core.Hud.S(4f);

			if (GuiKit.Button(new Rect(body.x + pad, y, bw, rowH - 6f), "CLOSE")) _popupOpen = false;
		}

		private static string Trunc(string s, int max)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, Math.Max(1, max - 1)) + "…");

		private void OnClick()
		{
			try
			{
				var entry = SelectedEntry(out string name);
				if (entry == null || string.IsNullOrEmpty(entry.UserId))
				{
					VaTagsModule.LastStatus = string.IsNullOrEmpty(name)
						? "could not read which user this menu is showing"
						: "'" + name + "' is not in the instance roster yet";
					return;
				}

				// A SUBMENU, NOT A DETOUR. This used to open the mod's whole menu on its PLAYERS
				// tab, which meant leaving the page you were already on and finding the person
				// again in a list. The per-user actions belong where the user is already selected.
				_popupFor = entry.UserId;
				_popupName = name ?? entry.Name;
				_popupOpen = true;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[UserMenu] click failed: {e.Message}"); }
		}

		// Clone the avatar of the user this page is showing, without a detour through our menu.
		// Same call the PLAYERS tab makes, so the strategies, the guards and the status line are
		// identical \u2014 there is no second implementation of cloning to keep in step.
		private void OnCopyAvi()
		{
			try
			{
				var entry = SelectedEntry(out string name);
				string id = entry != null ? entry.AvatarId : null;
				if (string.IsNullOrEmpty(id))
				{
					VaTagsModule.LastStatus = string.IsNullOrEmpty(name)
						? "copy id: could not read which user this menu is showing"
						: "copy id: '" + name + "' avatar not readable yet — wait for it to load";
					return;
				}
				GUIUtility.systemCopyBuffer = id;
				VaTagsModule.LastStatus = "copied avatar id: " + id;
				VRChatArchiveModPlugin.Logger.LogInfo("[UserMenu] copied avatar id " + id + " for " + (name ?? "?"));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] copy id failed: " + e.Message); }
		}

		private void OnClone()
		{
			try
			{
				var entry = SelectedEntry(out string name);
				if (entry != null && VaTagsModule.ReleaseStatusOf(entry) == "private")
				{
					VaTagsModule.LastStatus = "'" + (name ?? "that user") + "' has a PRIVATE avatar — nobody can wear it";
					return;
				}
				if (entry == null)
				{
					// Deliberately not silent: the card is on VRChat's page, so a failure here has no
					// console of its own to explain itself in.
					VaTagsModule.LastStatus = string.IsNullOrEmpty(name)
						? "clone failed: could not read which user this menu is showing"
						: "clone failed: '" + name + "' is not in the instance roster yet";
					VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] clone: no roster entry for " + (name ?? "?"));
					return;
				}
				VaTagsModule.CloneAvatar(entry);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[UserMenu] clone failed: {e.Message}"); }
		}

		// VRChat shows one of two labels depending on whether the user is a friend; only the
		// relevant one is active.
		private string SelectedUserName()
		{
			try
			{
				if (_page == null) return null;
				Transform info = _page.Find(InfoPath);
				if (info == null) return null;
				foreach (string n in new[] { "Text_Username_Friend", "Text_Username_NonFriend" })
				{
					var t = info.Find(n);
					if (t == null || !t.gameObject.activeInHierarchy) continue;
					var tmp = t.GetComponent<TMPro.TMP_Text>();
					string s = tmp != null ? tmp.text : null;
					if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
				}
				// Fall back to whichever carries text at all.
				foreach (string n in new[] { "Text_Username_Friend", "Text_Username_NonFriend" })
				{
					var t = info.Find(n);
					var tmp = t != null ? t.GetComponent<TMPro.TMP_Text>() : null;
					if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text)) return tmp.text.Trim();
				}

				// LAST RESORT: ASK THE ROSTER.
				//
				// InfoPath is a fixed hierarchy path, and this build moved it — the page itself is
				// now called "Menu_SelectedUser_Local(Clone)", so the inside changed too. Rather
				// than chase the next path, read EVERY label on the page and keep the one that
				// names somebody actually in the instance. The roster is the authority on who
				// exists, so a match cannot be a false positive, and it needs no path at all.
				try
				{
					var labels = _page.GetComponentsInChildren<TMPro.TMP_Text>(true);
					for (int i = 0; i < labels.Length; i++)
					{
						var l = labels[i];
						if (l == null) continue;
						string txt;
						try { txt = (l.text ?? "").Trim(); } catch { continue; }
						if (txt.Length == 0 || txt.Length > 64) continue;
						foreach (var pl in VaTagsModule.Roster)
						{
							if (pl == null || string.IsNullOrEmpty(pl.Name)) continue;
							if (string.Equals(pl.Name, txt, StringComparison.OrdinalIgnoreCase)) return pl.Name;
						}
					}
				}
				catch { }
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
