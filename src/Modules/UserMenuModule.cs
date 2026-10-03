using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// OUR CARDS on VRChat's per-user menu.
	//
	// When you click someone in the QuickMenu, VRChat opens Menu_SelectedUser_Remote (or _Local
	// for yourself) � the page with Friend Request / Block / Vote to Kick and so on. This adds five
	// cards to that page, acting on THAT user with no detour through our own menu:
	//   Clone Avatar / Copy Avatar Id  � one press, same code path as the PLAYERS tab;
	//   Orbit / Sit on / Ring objects  � toggles that light up while active.
	// No IMGUI popup anywhere: the headset cannot show one, and these cards must work in VR.
	//
	// Paths come from a live capture (captures/ui_*.txt):
	//   page  : .../Window/QMParent/Body/Menu_SelectedUser_{Remote|Local}
	//   name  : <page>/ScrollRect/Viewport/VerticalLayoutGroup/UserProfile_Compact/UserDetails/Info/
	//           {Text_Username_Friend | Text_Username_NonFriend}
	//   grids : Buttons_UserActions / Buttons_PerUserInteraction / Buttons_AvatarActions
	// The selected user is read from the page's name labels, matched against the roster
	// (SelectedEntry) � always from the page that is LIVE at click time.
	//
	// The cards are CLONED from one already on that page, so they inherit VRChat's resolved style �
	// cloning from a page the game never opens produces unstyled white cards.
	public class UserMenuModule : IModule
	{
		public override string Name => "UserMenu";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";
		private const string BodyPath = "CanvasGroup/Container/Window/QMParent/Body";
		// BOTH selected-user pages. VRChat uses a different page for yourself and for everyone
		// else, and this module only ever knew about the local one � so the cards were missing on
		// exactly the menu people open the most, another player's. Names are tried in order and
		// every page that exists gets its own set of cards.
		private static readonly string[] PagePaths =
		{
			"Menu_SelectedUser_Remote",
			"Menu_SelectedUser_Local",
		};
		private const string InfoPath = "ScrollRect/Viewport/VerticalLayoutGroup/UserProfile_Compact/UserDetails/Info";
		private const string CloneCardName = "Button_VACloneAvatar";
		private const string CopyCardName = "Button_VACopyAvi";
		private const string OrbitCardName = "Button_VAOrbit";
		private const string SitCardName = "Button_VASit";
		private const string RingCardName = "Button_VARing";
		private const string VoiceMimicCardName = "Button_VAVoiceMimic";
		private static readonly string[] OurCardNames = { CloneCardName, CopyCardName, OrbitCardName, SitCardName, RingCardName, VoiceMimicCardName };

		// Everything we put on ONE per-user page. There are two pages (yours / everyone else's)
		// and each carries its own set of cards, so the clone label and the lit states are cached
		// PER PAGE � a single set of fields could only ever describe the page injected last.
		private sealed class PageCards
		{
			public Transform Page;
			public GameObject Clone, Copy, Orbit, Sit, Ring, VoiceMimic;
			public string CloneState = "";   // "public" / "private" / "" � what the clone card currently shows
			public bool LitOrbit, LitSit, LitRing, LitVoiceMimic;
			// Unity-null once the game tears the page down: the cards go with it.
			public bool Alive => Clone != null && Orbit != null;
		}
		// Pages carrying our cards, keyed by the page's instance id. A rebuilt menu gets new ids,
		// so a stale entry can never claim a page that no longer carries our cards.
		private readonly Dictionary<int, PageCards> _injected = new Dictionary<int, PageCards>();
		private readonly List<int> _dead = new List<int>();
		private float _nextTry;
		private int _fails;
		private string _lastFailReason = "";
		private bool _bodyDumped;
		private float _nextCardRefresh;
		// Pages whose labels were already dumped for diagnosis, plus a floor between dumps.
		private readonly HashSet<int> _labelsDumped = new HashSet<int>();
		private float _nextLabelDump;

		public override void OnUpdate()
		{
			try
			{
				// A page torn down by the game (world change, menu rebuild) takes our cards with
				// it. Forget it here so the "done" test below sends TryInject back for the new one.
				_dead.Clear();
				foreach (var kv in _injected) if (kv.Value == null || !kv.Value.Alive) _dead.Add(kv.Key);
				for (int i = 0; i < _dead.Count; i++) _injected.Remove(_dead[i]);

				if (!ModConfig.UserMenuEnabled.Value)
				{
					// Every card on every page, not just the last two: turning the setting off used
					// to leave "Copy Avatar Id" behind, and turning it back on never re-added
					// anything because the pages still counted as injected.
					if (_injected.Count > 0) RemoveAllCards();
					return;
				}
				// Labels and lit states follow the SELECTED user, so this runs whether or not there
				// is still injecting left to do � it is the only thing that keeps the cards honest
				// as you move from one person's page to the next.
				RefreshCards();

				// Done while at least one live page carries our cards. The old test waited for
				// EVERY name in a fixed list, which this build never satisfies, so TryInject re-ran
				// on every tick forever (977 ms/s in the 08-29 log).
				if (_injected.Count > 0) return;

				float now = Time.realtimeSinceStartup;
				if (now < _nextTry) return;

				// THE PAGE DOES NOT EXIST UNTIL YOU OPEN IT, AND THAT IS THE WHOLE BUG (2026-09-08).
				//
				// This module has failed on every world load for weeks with "no per-user page under
				// Body", and its own diagnostic printed the answer without anyone reading it: the
				// list of pages Body actually holds — Menu_QM_Launchpad, Menu_Notifications,
				// Menu_Here, Menu_Camera and two dozen more — contains NO Menu_SelectedUser_*. Yet a
				// UI tree dump taken while a user menu was open shows
				//     .../Window/QMParent/Body/Menu_SelectedUser_Local(Clone)
				// sitting exactly where this code looks. So the page is created ON DEMAND and lives
				// only while it is open, and a probe on a 3-to-15 second timer misses that window
				// essentially every time. The name list and the pattern fallback were never the
				// problem; the CLOCK was.
				//
				// So: poll fast while the QuickMenu is actually open, and barely at all when it is
				// not. A closed QuickMenu cannot be showing a user page, so the slow path costs
				// nothing and the fast path only runs while somebody is looking at the menu.
				bool qmOpen = false;
				try { qmOpen = Core.QuickMenu.Visible; } catch { }
				_nextTry = now + (qmOpen ? 0.25f : (_fails < 20 ? 3f : 15f));
				if (!TryInject()) _fails++;
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogWarning($"[UserMenu] update threw: {e.Message}");
				_fails++;
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// The quick menu is rebuilt with the scene, so our cards are gone with it � and so is
			// the page whose selected-user host we cached.
			_injected.Clear();
			_labelsDumped.Clear();
			_fails = 0; _nextTry = 0f; _lastFailReason = "";
		}

		private bool TryInject()
		{
			Transform qm = Core.QuickMenu.Root();
			if (qm == null) return Fail("quick menu root not found");
			Transform body = qm.Find(BodyPath);
			if (body == null) return Fail("quick menu body path not found: " + BodyPath);

			// MATCH BY PATTERN, NOT BY EXACT NAME. The two names below were right for one build and
			// wrong for the next � VRChat renames these pages, and an exact Find() then reports
			// "no page" for a menu that is plainly on screen. Any direct child of Body whose name
			// looks like a per-user page is a candidate, so a rename no longer takes the feature
			// down; the known names simply get tried first.
			int built = 0;
			foreach (string pageName in PagePaths)
			{
				Transform p = body.Find(pageName);
				if (p == null) continue;
				if (_injected.ContainsKey(p.GetInstanceID())) { built++; continue; }
				if (InjectInto(p, pageName)) built++;   // InjectInto records the page on success
			}

			// BY TYPE, BEFORE ANY NAME GUESS.
			//
			// This module's whole failure is that Menu_SelectedUser_Remote / _Local do not exist on this
			// build: its own diagnostic listed every page under Body and not one of them mentions
			// SelectedUser. Names moved; the TYPE did not. VRChat still ships SelectedUserMenuQM in
			// VRC.UI.Elements, and measuring a 2021-era client against this build showed that pattern
			// holds generally — the hand-written VRChat type names survive years of updates while the
			// GameObject paths keyed on them are all dead. So ask for the component and take whatever
			// object it is on, wherever VRChat decided to put it this month.
			//
			// FindObjectsOfTypeAll, not FindObjectsOfType: the per-user page is INACTIVE until someone
			// is selected, and the active-only query would never see it. Non-generic with
			// Il2CppType.Of, because the generic overload returns an empty array for il2cpp classes.
			if (built == 0)
			{
				try
				{
					var il2 = Il2CppInterop.Runtime.Il2CppType.Of<SelectedUserMenuQM>();
					var found = Resources.FindObjectsOfTypeAll(il2);
					for (int i = 0; found != null && i < found.Length; i++)
					{
						var menu = found[i] != null ? found[i].TryCast<SelectedUserMenuQM>() : null;
						if (menu == null) continue;
						Transform page = null;
						try { page = menu.transform; } catch { }
						if (page == null || _injected.ContainsKey(page.GetInstanceID())) continue;
						try { if (!page.gameObject.scene.IsValid()) continue; } catch { continue; }
						if (InjectInto(page, page.name))
						{
							built++;
							Killiorim.Logger.LogInfo(
								"[UserMenu] per-user page found by TYPE (SelectedUserMenuQM) on '" + page.name + "'.");
						}
					}
				}
				catch (Exception e)
				{
					Killiorim.Logger.LogWarning("[UserMenu] SelectedUserMenuQM lookup: " + e.Message);
				}
			}

			if (built == 0)
			{
				for (int i = 0; i < body.childCount; i++)
				{
					Transform c = body.GetChild(i);
					if (c == null) continue;
					string n = c.name;
					if (string.IsNullOrEmpty(n) || _injected.ContainsKey(c.GetInstanceID())) continue;
					// "Menu_SelectedUser_Remote", "Menu_User", "UserDetails_Page"� all qualify;
					// the grid lookup inside InjectInto is what actually accepts or rejects it.
					if (n.IndexOf("User", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (n.IndexOf("Menu_", StringComparison.OrdinalIgnoreCase) < 0
						&& n.IndexOf("Page", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (InjectInto(c, n)) built++;
				}
			}

			if (built == 0)
			{
				// Name every page Body actually holds, once � the only thing that can tell us what
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
					Killiorim.Logger.LogWarning(
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
				Killiorim.Logger.LogWarning("[UserMenu] not injected: " + why);
			}
			return false;
		}

		// CLONE AVATAR SAYS WHETHER IT CAN WORK.
		//
		// A private avatar cannot be worn by anybody � VRChat does not serve it � so a card that
		// looks identical either way invites a press that can only fail. The label carries its own
		// colour (a TMP rich-text tag, which the game's own re-theming cannot overwrite), so one
		// write sets both the wording and the green/red.
		private const string CloneLabelPublic  = "<color=#7CFF9E>Clone Avatar</color>";
		private const string CloneLabelPrivate = "<color=#FF6B6B>AVI PRIVATE</color>";
		private const string CloneLabelUnknown = "Clone Avatar";

		private void RefreshCards()
		{
			if (_injected.Count == 0) return;
			float now = Time.realtimeSinceStartup;
			if (now < _nextCardRefresh) return;
			_nextCardRefresh = now + 0.5f;

			// Only the page actually on screen: reading the roster and the avatar record for a
			// menu nobody is looking at is pure cost, and cards on a hidden page cannot be seen.
			Transform page = LivePage();
			if (page == null) return;
			PageCards pc;
			if (!_injected.TryGetValue(page.GetInstanceID(), out pc) || pc == null || !pc.Alive) return;

			VaTagsModule.PlayerEntry entry = null;
			string state = "";
			try
			{
				entry = SelectedEntry(out _, diagnose: false);   // never dumps: this is the 0.5 s loop
				if (entry != null) state = VaTagsModule.ReleaseStatusOf(entry);
			}
			catch { }

			if (!string.Equals(state, pc.CloneState, StringComparison.Ordinal))
			{
				pc.CloneState = state;
				string label = state == "private" ? CloneLabelPrivate
					: state == "public" ? CloneLabelPublic
					: CloneLabelUnknown;
				try
				{
					var tmp = pc.Clone.transform.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (tmp != null) { tmp.richText = true; tmp.text = label; }
				}
				catch { }
			}

			// The three toggles light up like the QuickMenu tab tiles. Cached per card so the aura
			// is only rewritten on an actual change, never on every pass.
			bool orbiting = false, sitting = false, ringing = false, voiceMimicking = false;
			if (entry != null)
			{
				orbiting = OrbitModule.Active && OrbitModule.Current == OrbitModule.Mode.Orbit
					&& string.Equals(OrbitModule.TargetUid, entry.UserId, StringComparison.OrdinalIgnoreCase);
				sitting = OrbitModule.Active && OrbitModule.Current == OrbitModule.Mode.Sit
					&& string.Equals(OrbitModule.TargetUid, entry.UserId, StringComparison.OrdinalIgnoreCase);
				ringing = ObjectOrbitModule.Active
					&& string.Equals(ObjectOrbitModule.CenterName, entry.Name, StringComparison.Ordinal);
				voiceMimicking = VoiceMimicModule.Active
					&& string.Equals(VoiceMimicModule.TargetUid, entry.UserId, StringComparison.OrdinalIgnoreCase);
			}
			if (orbiting != pc.LitOrbit) { pc.LitOrbit = orbiting; Lit(pc.Orbit, orbiting); }
			if (sitting != pc.LitSit) { pc.LitSit = sitting; Lit(pc.Sit, sitting); }
			if (ringing != pc.LitRing) { pc.LitRing = ringing; Lit(pc.Ring, ringing); }
			if (voiceMimicking != pc.LitVoiceMimic) { pc.LitVoiceMimic = voiceMimicking; Lit(pc.VoiceMimic, voiceMimicking); }
		}

		// keepStyle: only the aura changes; VRChat keeps owning the card's background.
		private static void Lit(GameObject card, bool on)
		{
			try { if (card != null) Core.MenuCard.SetLit(card.transform, on, keepStyle: true); } catch { }
		}

		// Every card on every injected page, then a clean slate so re-enabling the setting rebuilds
		// them. The cards' grid is their parent, so no page lookup is needed to find them.
		private void RemoveAllCards()
		{
			foreach (var kv in _injected)
			{
				var pc = kv.Value;
				if (pc == null) continue;
				Transform grid = null;
				try
				{
					if (pc.Clone != null) grid = pc.Clone.transform.parent;
					else if (pc.Page != null) grid = FindGrid(pc.Page);
				}
				catch { }
				if (grid == null) continue;
				for (int i = 0; i < OurCardNames.Length; i++)
				{
					try
					{
						var t = grid.Find(OurCardNames[i]);
						if (t != null) UnityEngine.Object.Destroy(t.gameObject);
					}
					catch { }
				}
			}
			_injected.Clear();
			_fails = 0; _nextTry = 0f; _lastFailReason = "";
		}

		private bool InjectInto(Transform page, string pageName)
		{
			Transform grid = FindGrid(page);
			if (grid == null) return Fail("no button grid on page " + pageName);

			// A HEALTHY DONOR, NOT MERELY THE FIRST ONE.
			//
			// This used to take the first child named Button_*, and on this page that is
			// Button_FriendRequest — which VRChat keeps INACTIVE and DISABLED, with its CanvasGroup at
			// 0.25. Measured, clone beside donor:
			//   ours   Button_VACloneAvatar active=True  cg=1.00 interactable=True
			//   donor  Button_FriendRequest active=False cg=0.25 interactable=False
			// MenuCard.Setup does force our own alpha and interactable back, but the clone still
			// carries the greyed-out look it was made from — which is exactly the "ghost buttons" the
			// owner has been reporting.
			//
			// So the donor has to be one VRChat is actually SHOWING: active in the hierarchy, and not
			// faded by a CanvasGroup. The first Button_* is kept as a last resort, because a faded
			// card is still better than no card.
			Transform donor = null, fallback = null;
			for (int i = 0; i < grid.childCount; i++)
			{
				var c = grid.GetChild(i);
				if (c == null) continue;
				if (IsOurs(c.name)) continue;   // never clone ourselves
				if (!c.name.StartsWith("Button_", StringComparison.Ordinal)) continue;
				if (fallback == null) fallback = c;

				// JUDGED ON THE BUTTON'S OWN STATE, NEVER ON WHAT IT INHERITS.
				//
				// The first version asked for activeInHierarchy, and it never passed once. Proof, from
				// the owner's log at the moment the cards were built:
				//   [UserMenu] no fully-enabled button on Menu_SelectedUser_Local(Clone) to clone —
				//              falling back to 'Button_FriendRequest', cards may look faded.
				//   ours   Button_VACloneAvatar active=False cg[Button_VACloneAvatar]=1.00 cg[page]=0.00
				//   donor  Button_FriendRequest active=False cg[Button_FriendRequest]=0.25 cg[page]=0.00
				//              interactable=False disabled=C8C8C880
				// The PAGE is hidden while we build — normal, it is the page of a user nobody has opened
				// yet — so activeInHierarchy is false for every candidate on it and the test could not be
				// satisfied by any button, ever. The fallback then handed us the one button VRChat
				// deliberately keeps disabled, and all six cards were cloned from a greyed-out one.
				// That IS the ghosting, and the warning above it said so every single time.
				//
				// activeSelf, the button's OWN CanvasGroup and its OWN interactable describe what VRChat
				// did to THIS button — which is the only thing being copied.
				bool healthy = false;
				try
				{
					var cg = c.GetComponent<CanvasGroup>();
					var sel = c.GetComponent<UnityEngine.UI.Selectable>();
					healthy = c.gameObject.activeSelf
						&& (cg == null || cg.alpha > 0.9f)
						&& (sel == null || sel.interactable)
						// SHAPE, not just health. Two cards on this page are built differently, and
						// both break the clone: Button_Boop has no Icons/Icon, so MenuCard.LayoutCard
						// bails and the card is never laid out; Button_FavoriteFriend hangs its Text_H4
						// straight off the button root instead of a TextLayoutParent, which used to make
						// LayoutCard treat the CARD as its own label — the giant plate across the page.
						// MenuCard refuses that now, but a canonically-shaped donor gives a card that is
						// right rather than merely not broken.
						&& c.Find("Icons/Icon") != null
						&& c.Find("TextLayoutParent") != null;
				}
				catch { }
				if (healthy) { donor = c; break; }
			}
			if (donor == null && fallback != null)
			{
				Killiorim.Logger.LogWarning("[UserMenu] no fully-enabled button on " + pageName
					+ " to clone — falling back to '" + fallback.name + "', cards may look faded.");
				donor = fallback;
			}
			if (donor == null) return false;
			Killiorim.Logger.LogInfo("[UserMenu] donor: " + donor.name);

			// Build while the page is ACTIVE, otherwise Unity never runs Awake on the clone and
			// VRChat's own text component is still uninitialised when we set the label.
			bool wasActive = page.gameObject.activeSelf;
			try { page.gameObject.SetActive(true); } catch { }

			var pc = new PageCards { Page = page };
			// CLONE AVATAR right here. It used to mean: open our menu, find the PLAYERS tab, find the
			// row, press CLONE \u2014 four steps to do the one thing people open a user's page for.
			pc.Clone = Ensure(grid, donor, CloneCardName, "Clone Avatar", OnClone);
			pc.Copy = Ensure(grid, donor, CopyCardName, "Copy Avatar Id", OnCopyAvi);
			// ORBIT / SIT / RING as cards of their own. They were three IMGUI switches in a popup
			// that the headset could not show at all, and that the desktop closed the same frame it
			// opened (gated on the MAIN menu while living in the QUICK menu). Real cards on the
			// page work in VR and on desktop alike, and light up like the QuickMenu tab tiles.
			pc.Orbit = Ensure(grid, donor, OrbitCardName, "Orbit", OnOrbit);
			pc.Sit = Ensure(grid, donor, SitCardName, "Sit on", OnSit);
			pc.Ring = Ensure(grid, donor, RingCardName, "Ring objects", OnRing);
			pc.VoiceMimic = Ensure(grid, donor, VoiceMimicCardName, "Voice Mimic", OnVoiceMimic);

			try { page.gameObject.SetActive(wasActive); } catch { }

			// All or nothing: a half-built page is retried on the next pass, where Ensure
			// reuses the cards that did come up.
			if (!pc.Alive || pc.Copy == null || pc.Sit == null || pc.Ring == null || pc.VoiceMimic == null) return false;
			_injected[page.GetInstanceID()] = pc;
			Killiorim.Logger.LogInfo("[UserMenu] cards added to " + pageName
				+ " (Clone Avatar, Copy Avatar Id, Orbit, Sit on, Ring objects, Voice Mimic).");
			// Get them themed NOW. Our cards are named Button_VA* precisely so MenuThemeModule's
			// filter picks them up, but that filter only runs on a full scan, and a full scan
			// alternates canvases every 5 s — so a card built just after one ran sat flat black next
			// to its themed neighbours for up to ten seconds.
			MenuThemeModule.Invalidate();
			Compare(pc.Clone != null ? pc.Clone.transform : null, donor);
			return true;
		}

		// WHY OUR CARDS RENDER FADED, ANSWERED WITH NUMBERS RATHER THAN A GUESS.
		//
		// They are clones of a live VRChat card and they still come out ghosted beside the real ones.
		// Several things could do that — a CanvasGroup anywhere up the chain, the Background image's
		// own colour, a Selectable stuck on its disabled tint, or VRChat's StyleElement painting the
		// disabled variant — and picking between them by intuition has already cost this mod several
		// wrong builds. So the clone and the donor it was made from are printed side by side, once.
		// Whichever pair of numbers differs IS the cause.
		private static void Compare(Transform ours, Transform donor)
		{
			if (ours == null || donor == null) return;
			try
			{
				Killiorim.Logger.LogInfo("[UserMenu] ours   " + Describe(ours));
				Killiorim.Logger.LogInfo("[UserMenu] donor  " + Describe(donor));
			}
			catch (Exception e) { Killiorim.Logger.LogWarning("[UserMenu] compare: " + e.Message); }
		}

		private static string Describe(Transform t)
		{
			var sb = new System.Text.StringBuilder(t.name);
			try { sb.Append(" active=").Append(t.gameObject.activeInHierarchy); } catch { }
			// Every CanvasGroup between here and Window: one sitting at 0.4 anywhere up the chain
			// fades everything beneath it, and it need not be on the card itself.
			try
			{
				for (Transform p = t; p != null; p = p.parent)
				{
					var cg = p.GetComponent<CanvasGroup>();
					if (cg != null) sb.Append(" cg[").Append(p.name).Append("]=").Append(cg.alpha.ToString("F2"));
					if (p.name == "Window") break;
				}
			}
			catch { }
			try
			{
				var sel = t.GetComponent<UnityEngine.UI.Selectable>();
				if (sel != null)
					sb.Append(" interactable=").Append(sel.interactable)
					  .Append(" normal=").Append(ColorUtility.ToHtmlStringRGBA(sel.colors.normalColor))
					  .Append(" disabled=").Append(ColorUtility.ToHtmlStringRGBA(sel.colors.disabledColor));
			}
			catch { }
			try
			{
				var bg = t.Find("Background") ?? t.Find("Container/Background");
				var img = bg != null ? bg.GetComponent<UnityEngine.UI.Image>() : null;
				if (img != null)
					sb.Append(" bg=").Append(ColorUtility.ToHtmlStringRGBA(img.color))
					  .Append(" sprite=").Append(img.sprite != null ? img.sprite.name : "-");
			}
			catch { }
			try
			{
				int style = 0;
				foreach (var c in t.GetComponents<Component>())
				{
					if (c == null) continue;
					// The runtime class name, not the proxy's: VRChat's own components are obfuscated,
					// so a managed GetType() here would report the base class for every one of them.
					IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(c.Pointer);
					string n = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(
						Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(klass));
					if (n == "StyleElement") style++;
				}
				sb.Append(" styleElements=").Append(style);
			}
			catch { }
			return sb.ToString();
		}

		private static bool IsOurs(string cardName)
		{
			for (int i = 0; i < OurCardNames.Length; i++)
				if (string.Equals(cardName, OurCardNames[i], StringComparison.Ordinal)) return true;
			return false;
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
				Killiorim.Logger.LogWarning($"[UserMenu] could not add '{label}': {e.Message}");
				return null;
			}
		}

		// A GRID, NOT MERELY A CONTAINER WITH BUTTONS IN IT.
		//
		// This used to take the first container whose name matched and whose children included a
		// Button_*, and that is not the same thing. The live dump of this build shows why:
		//   Buttons_UserActions        <CanvasRenderer, GridLayoutGroup>       {1024x176}
		//   Buttons_UserActions        <CanvasRenderer, [obfuscated layout]>   {1024x184}
		//   Buttons_PerUserInteraction <CanvasRenderer, VerticalLayoutGroup>   {920x184}
		// Land a cloned card in either of the last two and it is not a tile any more — a vertical
		// layout stretches it to the full 920/1024 width, which is exactly the enormous plate lying
		// across the top of the page in the owner's screenshot.
		//
		// So a container with a GridLayoutGroup is taken over one without, and the name order only
		// decides between equals. The old behaviour is still the last resort: a mis-shaped card beats
		// no card, and it is logged so the next report says which one it settled for.
		private static Transform FindGrid(Transform page)
		{
			string[] names = { "Buttons_UserActions", "Buttons_PerUserInteraction", "Buttons_AvatarActions" };
			Transform loose = null;
			foreach (string n in names)
			{
				var t = FindDeep(page, n);
				if (t == null) continue;
				// The group may hold the cards directly or wrap them in a "Buttons" child.
				var inner = t.Find("Buttons");
				Transform g = inner != null ? inner : t;

				bool hasButton = false;
				for (int i = 0; i < g.childCount && !hasButton; i++)
					if (g.GetChild(i).name.StartsWith("Button_", StringComparison.Ordinal)) hasButton = true;
				if (!hasButton) continue;

				bool isGrid = false;
				try { isGrid = g.GetComponent<UnityEngine.UI.GridLayoutGroup>() != null; } catch { }
				if (isGrid) return g;
				if (loose == null) loose = g;
			}
			if (loose != null)
				Killiorim.Logger.LogWarning("[UserMenu] no GridLayoutGroup under " + page.name
					+ " — using '" + loose.name + "', cards may come out full width.");
			return loose;
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

		// ORBIT / SIT / RING. Three toggles that share one shape: resolve who the page shows,
		// refuse OUT LOUD when that fails (the card sits on VRChat's page, so a silent failure has
		// no console of its own to explain itself in), otherwise hand the roster entry to the
		// module that owns the behaviour. Each is a toggle: press again to stop.
		private void OnOrbit() => WithSelected("orbit", e => OrbitModule.Toggle(OrbitModule.Mode.Orbit, e));
		private void OnSit() => WithSelected("sit", e => OrbitModule.Toggle(OrbitModule.Mode.Sit, e));
		private void OnRing() => WithSelected("ring", e => ObjectOrbitModule.ToggleOnPlayer(e));
		private void OnVoiceMimic() => WithSelected("voice mimic", e => VoiceMimicModule.Toggle(e));

		private void WithSelected(string what, Action<VaTagsModule.PlayerEntry> act)
		{
			try
			{
				var e = SelectedEntry(out string name);
				if (e == null)
				{
					Say(string.IsNullOrEmpty(name)
						? what + ": could not read which user this menu is showing"
						: what + ": '" + name + "' is not in the instance roster yet");
					return;
				}
				act(e);
			}
			catch (Exception ex) { Killiorim.Logger.LogWarning($"[UserMenu] {what} failed: {Unwrap.Describe(ex)}"); }
		}

		// Status line AND toast. The status line is only drawn inside the mod's own menu, which
		// is closed while VRChat's page is up � so on its own, every message from these cards
		// went somewhere the user was not looking.
		private static void Say(string s)
		{
			VaTagsModule.LastStatus = s;
			try { Core.Toast.Show(s); } catch { }
		}

		// Copy the avatar id of the user this page is showing to the clipboard.
		private void OnCopyAvi()
		{
			try
			{
				var entry = SelectedEntry(out string name);
				string id = entry != null ? entry.AvatarId : null;
				if (string.IsNullOrEmpty(id))
				{
					Say(string.IsNullOrEmpty(name)
						? "copy id: could not read which user this menu is showing"
						: "copy id: '" + name + "' avatar not readable yet � wait for it to load");
					return;
				}
				GUIUtility.systemCopyBuffer = id;
				Say("copied avatar id: " + id);
				Killiorim.Logger.LogInfo("[UserMenu] copied avatar id " + id + " for " + (name ?? "?"));
			}
			catch (Exception e) { Killiorim.Logger.LogWarning("[UserMenu] copy id failed: " + Unwrap.Describe(e)); }
		}

		// Clone the avatar of the user this page is showing, without a detour through our menu.
		// Same call the PLAYERS tab makes, so the strategies, the guards and the status line are
		// identical � there is no second implementation of cloning to keep in step.
		private void OnClone()
		{
			try
			{
				var entry = SelectedEntry(out string name);
				if (entry != null && VaTagsModule.ReleaseStatusOf(entry) == "private")
				{
					Say("'" + (name ?? "that user") + "' has a PRIVATE avatar � nobody can wear it");
					return;
				}
				if (entry == null)
				{
					// Deliberately not silent: the card is on VRChat's page, so a failure here has no
					// console of its own to explain itself in.
					Say(string.IsNullOrEmpty(name)
						? "clone failed: could not read which user this menu is showing"
						: "clone failed: '" + name + "' is not in the instance roster yet");
					Killiorim.Logger.LogWarning("[UserMenu] clone: no roster entry for " + (name ?? "?"));
					return;
				}
				VaTagsModule.CloneAvatar(entry);
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[UserMenu] clone failed: {Unwrap.Describe(e)}"); }
		}

		// THE PAGE THE USER IS LOOKING AT, resolved at call time.
		//
		// The page cached at injection time is the wrong one whenever the game shows the OTHER of
		// its two per-user pages, and that is how "could not read which user this menu is showing"
		// came out of a menu that was plainly open on somebody. Injected pages are tried first
		// (they are known per-user pages whatever this build calls them), then any active child
		// of Body whose name says SelectedUser.
		private Transform LivePage()
		{
			try
			{
				foreach (var kv in _injected)
				{
					var pc = kv.Value;
					if (pc == null || !pc.Alive || pc.Page == null) continue;
					if (pc.Page.gameObject.activeInHierarchy) return pc.Page;
				}
				Transform qm = Core.QuickMenu.Root();
				Transform body = qm != null ? qm.Find(BodyPath) : null;
				if (body == null) return null;
				for (int i = 0; i < body.childCount; i++)
				{
					Transform c = body.GetChild(i);
					if (c == null) continue;
					string n = c.name;
					if (string.IsNullOrEmpty(n) || n.IndexOf("SelectedUser", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (c.gameObject.activeInHierarchy) return c;
				}
			}
			catch { }
			return null;
		}

		// The roster entry for whoever the LIVE page is showing, or null. `name` carries whatever
		// could be read even when no roster entry matched, so a refusal can still name the person.
		// `diagnose` logs the page's labels once when nothing resolves � only from a click, never
		// from the 0.5 s refresh loop, which would turn one unreadable page into a log flood.
		private VaTagsModule.PlayerEntry SelectedEntry(out string name, bool diagnose = true)
		{
			name = null;
			Transform page = LivePage();
			if (page == null) return null;

			// LABELS ONLY. An earlier version asked the page's own components for their APIUser
			// first: it re-proxied every MonoBehaviour on the page to a VRC.Core wrapper found by
			// scanning the whole assembly (forcing the static constructor of every candidate type)
			// and then read a field by offset on an obfuscated page controller. That is the one
			// thing this mod must never do -- a field read on a proxy of the wrong shape is an
			// access violation no try/catch survives -- and it took the game down the moment a
			// player's page opened, three sessions in a row on 2026-09-01. TMP labels are Unity
			// objects read through their own API, and the roster decides whether a name is real.
			string label = SelectedUserName(page);
			if (!string.IsNullOrEmpty(label)) name = label;
			if (string.IsNullOrEmpty(name))
			{
				if (diagnose) DumpLabelsOnce(page);
				return null;
			}
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

		// THE LABEL ROUTE. VRChat shows one of two name labels depending on whether the user is a
		// friend; only the relevant one is active. When InfoPath has moved again (this build
		// already renamed the page once), every label on the page is read and the one that names
		// somebody actually in the instance wins � the roster is the authority on who exists, so a
		// match cannot be a false positive, and it needs no path at all.
		private static string SelectedUserName(Transform page)
		{
			try
			{
				Transform info = page.Find(InfoPath);
				if (info != null)
				{
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
				}

				var labels = page.GetComponentsInChildren<TMPro.TMP_Text>(true);
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
			return null;
		}

		// Once per page instance, and never more than every 10 s: the (object, text) of every
		// label on the live page. This is the only thing that can say what the name label is
		// called on a build that moved it, instead of another round of path guessing.
		private void DumpLabelsOnce(Transform page)
		{
			try
			{
				int pid = page.GetInstanceID();
				float now = Time.realtimeSinceStartup;
				if (_labelsDumped.Contains(pid) || now < _nextLabelDump) return;
				_labelsDumped.Add(pid);
				_nextLabelDump = now + 10f;

				var sb = new System.Text.StringBuilder();
				var labels = page.GetComponentsInChildren<TMPro.TMP_Text>(true);
				int n = 0;
				for (int i = 0; i < labels.Length && n < 80; i++)
				{
					var l = labels[i];
					if (l == null) continue;
					string txt;
					try { txt = (l.text ?? "").Trim(); } catch { continue; }
					if (txt.Length == 0) continue;
					if (txt.Length > 48) txt = txt.Substring(0, 47) + "�";
					sb.Append("\n    ").Append(l.name).Append(l.gameObject.activeInHierarchy ? " = " : " (off) = ")
						.Append(txt.Replace('\n', ' '));
					n++;
				}
				Killiorim.Logger.LogWarning("[UserMenu] could not read the selected user on " + page.name
					+ "; labels on the page (" + n + "):" + sb);
			}
			catch { }
		}
	}
}

