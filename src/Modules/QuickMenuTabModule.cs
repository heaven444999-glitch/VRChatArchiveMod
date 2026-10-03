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
	// NATIVE QUICKMENU TAB — adds a "VRChat Archive" tab to VRChat's own QuickMenu tab strip,
	// with its own page of buttons, so the mod lives inside the game's menu.
	//
	// Every path here comes from a real capture of the live menu (captures/ui_*.txt):
	//   tab strip : Canvas_QuickMenu(Clone)/CanvasGroup/Container/Window/Page_Buttons_QM/HorizontalLayoutGroup
	//               (HorizontalLayoutGroup + ContentSizeFitter -> a new child lays itself out)
	//   page host : Canvas_QuickMenu(Clone)/CanvasGroup/Container/Window/QMParent/Body
	//   donors    : Page_DevTools (tab) + Menu_DevTools (page) — both ship DISABLED, so cloning
	//               them disturbs nothing a normal user sees.
	//   buttons   : the cloned Menu_DevTools page brings its OWN button grid with it
//               (Scrollrect/Viewport/VerticalLayoutGroup/Buttons) — we re-purpose those cards
	//
	// Two rules keep this safe:
	//   1. We never modify VRChat's own objects — only our CLONES.
	//   2. VRChat's obfuscated controller components are stripped off the clone ROOTS (page root,
	//      tab root, card roots) so the game's page state-machine cannot fight us. Children keep
	//      their components, which is what gives the cloned buttons a real font and real sprites —
	//      building UI from scratch produced invisible text, because a fresh TextMeshProUGUI has
	//      no font asset assigned.
	public class QuickMenuTabModule : IModule
	{
		public override string Name => "QuickMenuTab";

		private const string TabName  = "Page_Killiorium";
		private const string PageName = "Menu_Killiorium";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";
		private const string StripPath = "CanvasGroup/Container/Window/Page_Buttons_QM/HorizontalLayoutGroup";
		private const string BodyPath  = "CanvasGroup/Container/Window/QMParent/Body";
		// The Launchpad's quick-link cards are the ones the player actually sees, so VRChat has
		// already applied its style to them. Cloning one copies those resolved colours. The
		// DevTools page's own tiles are never opened by the game, so they were never styled —
		// cloning THOSE is what produced plain white cards.
		private const string QuickLinksPath = "Menu_QM_Launchpad/ScrollRect/Viewport/VerticalLayoutGroup/Buttons_QuickLinks";

		private GameObject _tab, _page;
		private Transform _body;
		private Transform _archiveGrid, _archiveContent;
		private float _nextTry;
		private int _fails;
		private bool _loggedOnce;
		private bool _tabWasActive, _pageWasActive;
		private static Sprite _logoSprite;

		// Unity components worth keeping on a clone ROOT. Everything else (VRChat's obfuscated
		// tab / page / style / tooltip controllers) is destroyed so the game cannot drive it.
		private static readonly HashSet<string> Keep = new HashSet<string>(StringComparer.Ordinal)
		{
			"RectTransform", "Transform", "CanvasRenderer", "Canvas", "CanvasGroup", "GraphicRaycaster",
			"Image", "ImageEx", "RawImage", "RawImageEx",
			"LayoutElement", "HorizontalLayoutGroup", "VerticalLayoutGroup", "GridLayoutGroup",
			"ContentSizeFitter", "AspectRatioFitter", "RectMask2D", "Mask", "ScrollRect", "Scrollbar",
			"UIInvisibleGraphic", "Button",
		};

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.QMTabEnabled.Value)
				{
					if (_tab != null || _page != null) Teardown();
					return;
				}
				if (_tab != null && _page != null) { Pump(); return; }

				float now = Time.realtimeSinceStartup;
				if (now < _nextTry) return;
				_nextTry = now + 3f;              // the QuickMenu only exists once the UI is built
				if (_fails > 40) return;          // give up quietly rather than scan forever
				if (!TryBuild()) _fails++;
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogWarning($"[QMTab] update threw: {e.Message}");
				_fails++;
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			if (_tab == null || _page == null) { _tab = null; _page = null; _fails = 0; _nextTry = 0f; _wasActive = false; }
		}

		public override void OnShutdown() => Teardown();

		// ---------------------------------------------------------------- build

		private bool TryBuild()
		{
			Transform qm = Core.QuickMenu.Root();
			if (qm == null) return false;

			Transform strip = qm.Find(StripPath);
			Transform body = qm.Find(BodyPath);
			if (strip == null || body == null)
			{
				if (!_loggedOnce)
				{
					_loggedOnce = true;
					Killiorim.Logger.LogWarning($"[QMTab] QuickMenu found but strip={(strip != null)} body={(body != null)} — layout changed?");
				}
				return false;
			}
			_body = body;

			// THE DEVTOOLS METHOD. VRChat already ships a complete, fully-styled tab + page that
			// nobody uses: Page_DevTools and Menu_DevTools, both disabled. Rather than cloning a
			// tab and stripping it — which threw away the StyleElement that themes it, so ours
			// rendered as a flat coloured square that matched nothing — we simply switch the real
			// ones ON and fill the page. The tab is then genuinely VRChat's: its hover, selected
			// state and page-opening are driven by the game's own controller, for free.
			Transform tab = strip.Find("Page_DevTools");
			Transform page = body.Find("Menu_DevTools");
			if (tab == null || page == null)
			{
				Killiorim.Logger.LogWarning($"[QMTab] DevTools tab/page not present (tab={(tab != null)} page={(page != null)}).");
				return false;
			}

			_tabWasActive = tab.gameObject.activeSelf;
			_pageWasActive = page.gameObject.activeSelf;

			tab.gameObject.SetActive(true);
			BrandTab(tab);

			// Build the page while it is ACTIVE. Cloning tiles into a disabled page means Unity
			// never runs Awake on them, so VRChat's own text component is still uninitialised and
			// setting its text throws — which is exactly why the four toggle tiles failed. The
			// QuickMenu itself is closed at this point, so nothing flashes on screen; the previous
			// state is restored immediately afterwards.
			bool pageWas = page.gameObject.activeSelf;
			try { page.gameObject.SetActive(true); } catch { }
			FillPage(page, body);
			try { page.gameObject.SetActive(pageWas); } catch { }

			_tab = tab.gameObject;
			_page = page.gameObject;
			Killiorim.Logger.LogInfo("[QMTab] VRChat's own DevTools tab enabled and re-purposed as the Killiorium tab.");
			return true;
		}


		private static Transform FindDonorTab(Transform strip)
		{
			string[] preferred = { "Page_DevTools", "Page_Store", "Page_VRCPlus_Subscribed" };
			foreach (string n in preferred) { var t = strip.Find(n); if (t != null) return t; }
			for (int i = 0; i < strip.childCount; i++)
			{
				var c = strip.GetChild(i);
				if (c != null && c.name.StartsWith("Page_", StringComparison.Ordinal)) return c;
			}
			return null;
		}

		private static Transform FindDonorPage(Transform body)
		{
			string[] preferred = { "Menu_DevTools", "Menu_QM_ClockMode" };
			foreach (string n in preferred) { var t = body.Find(n); if (t != null) return t; }
			return null;
		}

		// Strip only THIS object's foreign components (children keep theirs).
		private static void StripRoot(Transform t)
		{
			try
			{
				var comps = t.GetComponents<Component>();
				if (comps == null) return;
				foreach (var c in comps)
				{
					if (c == null) continue;
					if (Keep.Contains(Il2CppName(c))) continue;
					try { UnityEngine.Object.DestroyImmediate(c); } catch { }
				}
			}
			catch { }
		}

		private static void StripTree(Transform t, int depth)
		{
			if (t == null || depth < 0) return;
			StripRoot(t);
			for (int i = 0; i < t.childCount; i++) StripTree(t.GetChild(i), depth - 1);
		}

		// Our identity on the cloned tab: the real VRChat Archive logo as its icon.
		// Only the icon is ours. Colours, hover and the selected highlight stay with VRChat's
		// StyleElement — overriding them is what made the cloned tab look like a foreign square.
		private static void BrandTab(Transform tab)
		{
			try
			{
				var icon = tab.Find("Icon");
				if (icon == null) return;

				// The Icon carries a StyleElement, and that is what kept putting VRChat's DevTools
				// wrench back over our logo. Drop it on this one object (the tab's own Background
				// keeps its StyleElement, so the tab still themes with the rest of the strip).
				foreach (var c in icon.GetComponents<Component>())
				{
					if (c == null) continue;
					if (Il2CppName(c) != "StyleElement") continue;
					try { UnityEngine.Object.DestroyImmediate(c); } catch { }
				}

				var img = icon.GetComponent<Image>();
				if (img == null) return;
				var sp = LogoSprite();
				if (sp != null) { img.sprite = sp; img.color = Color.white; }
			}
			catch { }

			RenameTooltip(tab);
		}

		// The hover bubble still read "Developer Tools": it comes from one of VRChat's own
		// components on the tab, which we deliberately keep (that is what makes the tab behave
		// natively). The component type name is obfuscated and changes every build, so instead of
		// guessing it we walk the tab's components and rewrite any string member that currently
		// holds the stock caption. Also covers the tab's Badge label if it carries one.
		private static void RenameTooltip(Transform tab)
		{
			const string NewName = "VRCHAT ARCHIVE";
			try
			{
				foreach (var comp in tab.GetComponents<Component>())
				{
					if (comp == null) continue;
					Type t = comp.GetType();

					foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
					{
						if (prop.PropertyType != typeof(string) || !prop.CanRead || !prop.CanWrite) continue;
						try
						{
							if (prop.GetValue(comp) is string cur && LooksLikeStockCaption(cur))
								prop.SetValue(comp, NewName);
						}
						catch { }
					}

					foreach (var fld in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
					{
						if (fld.FieldType != typeof(string)) continue;
						try
						{
							if (fld.GetValue(comp) is string cur && LooksLikeStockCaption(cur))
								fld.SetValue(comp, NewName);
						}
						catch { }
					}
				}

				// The badge label, if this tab shows one.
				var badge = tab.Find("Badge");
				if (badge != null)
				{
					var tmp = badge.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (tmp != null && LooksLikeStockCaption(tmp.text)) tmp.text = NewName;
				}
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[QMTab] tooltip rename failed: {e.Message}"); }
		}

		private static bool LooksLikeStockCaption(string s)
		{
			if (string.IsNullOrEmpty(s)) return false;
			return s.IndexOf("Developer", StringComparison.OrdinalIgnoreCase) >= 0
			    || s.IndexOf("DevTools", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static Sprite LogoSprite()
		{
			try
			{
				if (_logoSprite != null) return _logoSprite;
				var tex = AssetLoader.ArchiveLogo;
				if (tex == null) return null;
				_logoSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
				if (_logoSprite != null) _logoSprite.hideFlags = HideFlags.HideAndDontSave;
				return _logoSprite;
			}
			catch { return null; }
		}

		// The page's rows: real VRChat button cards, cloned from the Launchpad's quick-links grid
		// so they carry the game's own font, sprites and layout, then relabelled and re-wired.
		// A tile is either a one-shot ACTION (VRChat's Warp tile shape) or a TOGGLE (VRChat's
		// Invisible/Tag tile shape, which carries an Icon_Off / Icon_On pair). Reusing the game's
		// own two shapes is what makes the page look native instead of home-made.
		// Icon: an optional texture of OURS to put on the tile instead of whatever the donor card
		// happened to carry. Without it a soundboard tile inherits some unrelated VRChat glyph.
		// IconName: hints, '|'-separated, matched case-insensitively against the names of the sprites
		// VRChat itself has loaded (SpriteIndex). The first hint that names a real sprite wins, so a
		// tile wears the game's own glyph for what it does instead of whatever the donor carried.
		private struct Act { public string Label; public Action Do; public Func<bool> State; public Func<Texture2D> Icon; public string IconName; }

		// SPRITE INDEX. Every Sprite the game has loaded, by name, built once per page fill and
		// thrown away with the page. Lets a tile wear one of VRChat's own icons by asking for it
		// by (part of) its name. The first fill also writes every candidate name to
		// BepInEx/qm_sprites.txt, once, so the hints in ActsFor can be tuned against the real
		// list instead of guessed.
		private static class SpriteIndex
		{
			private static readonly Dictionary<string, Sprite> _byName = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
			private static readonly List<string> _names = new List<string>();
			private static bool _built, _dumped;

			public static void Invalidate() { _built = false; _byName.Clear(); _names.Clear(); }

			private static void Build()
			{
				_built = true;
				try
				{
					var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(typeof(Sprite)));
					if (all == null) return;
					for (int i = 0; i < all.Length; i++)
					{
						var sp = all[i]?.TryCast<Sprite>();
						if (sp == null) continue;
						string n = sp.name ?? "";
						if (n.Length == 0 || _byName.ContainsKey(n)) continue;
						_byName[n] = sp; _names.Add(n);
					}
					Killiorim.Logger.LogInfo("[QMTab] sprite index: " + _names.Count + " name(s).");
					if (!_dumped)
					{
						_dumped = true;
						try
						{
							string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "qm_sprites.txt");
							var sorted = new List<string>(_names); sorted.Sort(StringComparer.OrdinalIgnoreCase);
							System.IO.File.WriteAllLines(path, sorted);
							Killiorim.Logger.LogInfo("[QMTab] sprite names written to " + path);
						}
						catch (Exception e) { Killiorim.Logger.LogWarning("[QMTab] sprite dump failed: " + e.Message); }
					}
				}
				catch (Exception e) { Killiorim.Logger.LogWarning("[QMTab] sprite index failed: " + e.Message); }
			}

			// First hint that is contained in a loaded sprite's name wins; an exact name beats a
			// substring so a hint like "eye" cannot land on "keyeye_bg" when "Icon_Eye" exists.
			public static Sprite Find(string hints)
			{
				if (string.IsNullOrEmpty(hints)) return null;
				if (!_built) Build();
				foreach (string raw in hints.Split('|'))
				{
					string h = raw.Trim();
					if (h.Length == 0) continue;
					if (_byName.TryGetValue(h, out var exact) && exact != null) return exact;
					for (int i = 0; i < _names.Count; i++)
					{
						string n = _names[i];
						if (n.IndexOf(h, StringComparison.OrdinalIgnoreCase) < 0) continue;
						// icons only: skip obvious non-glyph art (backgrounds, gradients, wallpapers)
						if (n.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("wallpaper", StringComparison.OrdinalIgnoreCase) >= 0) continue;
						if (_byName.TryGetValue(n, out var sp) && sp != null) return sp;
					}
				}
				return null;
			}
		}

		private sealed class ToggleTile { public Transform Card; public Func<bool> State; public bool Last; }
		private readonly List<ToggleTile> _toggles = new List<ToggleTile>();

		// VRChat assigns card sprites through StyleElement when a page is first shown. We build our
		// page at startup, before the QuickMenu has ever been opened, so at that moment the DONOR
		// cards have null sprites too — which is why three tiles had no icon and no border while
		// the others (whose sprites are baked) looked right. Keep the pairs and re-copy until the
		// game has resolved them.
		private sealed class Pair { public Transform Clone; public Transform Donor; }
		private readonly List<Pair> _pairs = new List<Pair>();
		private bool _spritesResolved;

		// ------------------------------------------------------------------ sub-pages
		//
		// The page is a MENU, so it navigates like one. A flat grid of nine tiles cannot grow —
		// every new feature either pushes another card into an already-full 3x3, or does not get a
		// tile at all — and it also forces unrelated things to sit together: a soundboard clip
		// beside the switch that stops every script in the world.
		//
		// So a tile can now open another SET of tiles on the same page. This is a content swap, not
		// a VRChat page push: we rebuild the grid we already own and retitle the header. That keeps
		// it entirely inside Unity — no obfuscated page-stack API to break on the next update — and
		// the Back arrow is VRChat's own, which ships in this header already disabled.
		public enum QmPage { Root = 0, Protection = 1, Overlays = 2, Sounds = 3, Movement = 4, Tools = 5 }

		private static QmPage _qmPage = QmPage.Root;
		private static bool _pageDirty;

		public static void GoTo(QmPage page)
		{
			if (_qmPage == page) return;
			_qmPage = page;
			_pageDirty = true;
		}

		private static string TitleFor(QmPage p)
		{
			switch (p)
			{
				case QmPage.Protection: return "Archive · Protection";
				case QmPage.Overlays:   return "Archive · On screen";
				case QmPage.Sounds:     return "Archive · Sounds";
				case QmPage.Movement:   return "Archive · Movement";
				case QmPage.Tools:      return "Archive · Tools";
				default:                return "VRChat Archive";
			}
		}

		private List<Act> ActsFor(QmPage page)
		{
			// No Back tile. The header carries VRChat's own back arrow (SyncBackButton clones and
			// wires it), so a tile that did the same thing only spent a slot and wore a borrowed
			// lightning-bolt icon that meant nothing.

			switch (page)
			{
				case QmPage.Tools:
					return new List<Act>
					{
						new Act { IconName = "Icon_UdonSpotlight|Logging|debug", Label = "Udon Console", Do = () => { ModConfig.UdonLogEnabled.Value = !ModConfig.UdonLogEnabled.Value; }, State = () => ModConfig.UdonLogEnabled.Value },
						new Act { IconName = "Tag|Tag_Disabled|ReloadIcon", Label = "Refresh Tags", Do = VaTagsModule.RequestRefresh },
					};

				case QmPage.Protection:
					return new List<Act>
					{

						new Act { IconName = "Icon_Shield|Icon_Shield_Custom|shield", Label = "Crash protection", Do = () => { ModConfig.AntiCrashEnabled.Value = !ModConfig.AntiCrashEnabled.Value; }, State = () => ModConfig.AntiCrashEnabled.Value },
						new Act { IconName = "BlockUser|Blocked_White_Transparent|icon_listener_blocked|block", Label = "Block crasher scripts", Do = () => { ModConfig.UdonBlockCrashers.Value = !ModConfig.UdonBlockCrashers.Value; }, State = () => ModConfig.UdonBlockCrashers.Value },
						// The destructive one, and it says what it costs. A user reported "mirrors
						// don't work" and could not find this switch, because the tile that turns it
						// on never mentioned that mirrors are world scripts too.
						new Act { IconName = "StopIcon|stop|Icon_Close_X", Label = "STOP all world scripts (breaks mirrors)", Do = () => { ModConfig.UdonBlockAll.Value = !ModConfig.UdonBlockAll.Value; }, State = () => ModConfig.UdonBlockAll.Value },
						new Act { IconName = "Unblock|Eye|visibility", Label = "Anti-block", Do = () => { ModConfig.AntiBlockEnabled.Value = !ModConfig.AntiBlockEnabled.Value; }, State = () => ModConfig.AntiBlockEnabled.Value },
						new Act { IconName = "ReloadIcon|ic_reset|reset", Label = "Re-check everyone", Do = () => { Core.Menu.RequestRescan(); } },
					};

				case QmPage.Overlays:
					return new List<Act>
					{
						new Act { IconName = "Icon_Safety_Avatar_Shape|Hand_Avatar|icon_user", Label = "2D player ESP", Do = () => { ModConfig.EspEnabled.Value = !ModConfig.EspEnabled.Value; }, State = () => ModConfig.EspEnabled.Value },
						new Act { IconName = "Rectangle|Box|Icon_Safety_Avatar_Shape", Label = "Player box", Do = () => { ModConfig.EspBox.Value = !ModConfig.EspBox.Value; }, State = () => ModConfig.EspBox.Value },
						new Act { IconName = "Skeleton|Avatar|Humanoid", Label = "Player skeleton", Do = () => { ModConfig.EspSkeleton.Value = !ModConfig.EspSkeleton.Value; }, State = () => ModConfig.EspSkeleton.Value },
						new Act { IconName = "Text|Name|User", Label = "Player names", Do = () => { ModConfig.EspName.Value = !ModConfig.EspName.Value; }, State = () => ModConfig.EspName.Value },
						new Act { IconName = "Distance|Location|Ruler", Label = "Player distance", Do = () => { ModConfig.EspDistance.Value = !ModConfig.EspDistance.Value; }, State = () => ModConfig.EspDistance.Value },

						// ONE GLOW, ONE TILE. The screen-space "Box around players" is gone; the 3D
						// capsule is what shows a player through a wall, and every glow below is a
						// switch of its own — none of them needs another to be on (HighlightEspModule,
						// CapsuleEspModule).
						new Act { IconName = "Icon_Safety_Avatar_Shape|Hand_Avatar|icon_user", Label = "Player capsules", Do = () => { ModConfig.EspCapsule.Value = !ModConfig.EspCapsule.Value; }, State = () => ModConfig.EspCapsule.Value },
						new Act { IconName = "Eye|visibility|Eye_Disabled", Label = "See through walls", Do = () => { ModConfig.EspThroughWalls.Value = !ModConfig.EspThroughWalls.Value; }, State = () => ModConfig.EspThroughWalls.Value },
						new Act { IconName = "Icon_Spotlight|glow|DynamicLight", Label = "Glow around avatars", Do = () => { ModConfig.EspHighlight.Value = !ModConfig.EspHighlight.Value; }, State = () => ModConfig.EspHighlight.Value },
						new Act { IconName = "Grab|hand|Handshake", Label = "Glow on pickups", Do = () => { ModConfig.EspItems.Value = !ModConfig.EspItems.Value; }, State = () => ModConfig.EspItems.Value },
						new Act { IconName = "DropPortal|Portal|TeleportTo", Label = "Glow on portals", Do = () => { ModConfig.EspPortals.Value = !ModConfig.EspPortals.Value; }, State = () => ModConfig.EspPortals.Value },
						new Act { IconName = "prints_location|LocationUnavailable|TeleportToMe", Label = "Radar", Do = () => { ModConfig.RadarEnabled.Value = !ModConfig.RadarEnabled.Value; }, State = () => ModConfig.RadarEnabled.Value },
						new Act { IconName = "Social|Friends|icon_user", Label = "Player list", Do = () => { ModConfig.InstancePanelsEnabled.Value = !ModConfig.InstancePanelsEnabled.Value; }, State = () => ModConfig.InstancePanelsEnabled.Value },
					};

				case QmPage.Movement:
					// PLAIN SWITCHES, and the VALUES live on the sliders below. These were tiles that
					// cycled through preset numbers, which is not what a card is for — and it made
					// "custom speed" a third switch that had to be on before the others did anything.
					return new List<Act>
					{

						new Act { IconName = "ic_fly_mode|Drone_FlightModes|WingLeft", Label = "Fly", Do = () => { ModConfig.FlyEnabled.Value = !ModConfig.FlyEnabled.Value; }, State = () => ModConfig.FlyEnabled.Value },
						new Act { IconName = "TeleportTo|TeleportToMe|DropPortal", Label = "Click to teleport", Do = () => { ModConfig.ClickTpEnabled.Value = !ModConfig.ClickTpEnabled.Value; }, State = () => ModConfig.ClickTpEnabled.Value },
						new Act { IconName = "PlayerMove|BodyMode_Standing|Hand_Avatar", Label = "Walk mod", Do = () => { ModConfig.WalkMod.Value = !ModConfig.WalkMod.Value; }, State = () => ModConfig.WalkMod.Value },
						new Act { IconName = "Arrow_Right|arrow|PlayerMove", Label = "Run mod", Do = () => { ModConfig.RunMod.Value = !ModConfig.RunMod.Value; }, State = () => ModConfig.RunMod.Value },
						new Act { IconName = "arrow_up|Arrow_Right", Label = "Jump mod", Do = () => { ModConfig.JumpMod.Value = !ModConfig.JumpMod.Value; }, State = () => ModConfig.JumpMod.Value },
						new Act { IconName = "ic_reset|reset|Home_Reset", Label = "Back to normal", Do = SpeedModule.ResetToWorld },
					};

				case QmPage.Sounds:
				{
					// Built FROM the clip list, not from hard-coded indices: adding a clip to
					// SoundboardModule.Clips used to mean it silently never appeared here, and a
					// removed one would have thrown. Each tile shows the clip's own image when it
					// has one, so the board is readable instead of a column of identical hearts.
					var sounds = new List<Act>();
					foreach (var clip in SoundboardModule.Clips)
					{
						var c = clip;   // captured per iteration, not by reference to the loop var
						sounds.Add(new Act
						{
							Label = c.Label,
							Do = () => SoundboardModule.Send(c),
							Icon = () => AssetLoader.Icon(c.Image) ?? AssetLoader.HeartIcon,
						});
					}
					return sounds;
				}

				default:
					return new List<Act>
					{
						new Act { IconName = "Icon_Shield|Icon_Shield_Custom|shield", Label = "Protection ›",  Do = () => GoTo(QmPage.Protection), State = () => ModConfig.UdonBlockAll.Value || ModConfig.AntiCrashEnabled.Value },
						new Act { IconName = "HUD|HUD_Verbose|Eye", Label = "On screen ›",   Do = () => GoTo(QmPage.Overlays) },
						new Act { IconName = "PlayerMove|ic_fly_mode|BodyMode_Standing", Label = "Movement ›",    Do = () => GoTo(QmPage.Movement), State = () => ModConfig.FlyEnabled.Value || ModConfig.SpeedEnabled.Value },
						new Act { IconName = "Settings|settings|Tool", Label = "Tools ›", Do = () => GoTo(QmPage.Tools) },
						new Act { Label = "Sounds ›",      Do = () => GoTo(QmPage.Sounds), Icon = () => AssetLoader.HeartIcon },
					};
			}
		}

		private void FillPage(Transform page, Transform body)
		{
			try
			{
				Transform content = FindContent(page);
				if (content == null) { Killiorim.Logger.LogWarning("[QMTab] page has no content node."); return; }

				Transform donorGrid = body.Find(QuickLinksPath);
				if (donorGrid == null)
				{
					Killiorim.Logger.LogWarning("[QMTab] Launchpad quick-links grid not found — page left as-is.");
					return;
				}

				// Wipe whatever the DevTools page came with, then drop in a clone of the real,
				// already-styled quick-links grid. It ships exactly six cards, each with its own
				// icon — which is also why we no longer duplicate one card six times.
				for (int i = content.childCount - 1; i >= 0; i--)
				{
					try { UnityEngine.Object.DestroyImmediate(content.GetChild(i).gameObject); } catch { }
				}
				// That loop just destroyed VA_Sliders along with the cards, but the slider list
				// still held its rows, so SyncSliders saw "sliders exist" and never rebuilt them:
				// Movement → Back → Movement lost the sliders for good. Forgetting them here lets
				// SyncSliders rebuild the host on the next Pump tick.
				_sliders.Clear(); _sliderHost = null;

				var grid = UnityEngine.Object.Instantiate(donorGrid.gameObject, content);
				grid.name = "Buttons_Archive";
				grid.SetActive(true);
				_archiveGrid = grid.transform;
				_archiveContent = content;

				// Two wide cards make the labels readable and give every Archive page one consistent
				// rhythm. Width comes from the live content rect, so the same layout works across menu
				// sizes instead of inheriting the Launchpad's fixed three-column tile geometry.
				try
				{
					ApplyArchiveGridLayout();

					// The donor rect only accounts for its original six tiles. Fit the height to however
					// many rows the Archive page actually needs, so sliders and scroll bounds follow it.
					var fit = grid.GetComponent<UnityEngine.UI.ContentSizeFitter>();
					if (fit == null) fit = grid.AddComponent<UnityEngine.UI.ContentSizeFitter>();
					fit.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
					fit.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;

					// And let the parent layout size it rather than pinning it to the donor's height.
					var gle = grid.GetComponent<UnityEngine.UI.LayoutElement>();
					if (gle != null) { gle.ignoreLayout = false; gle.minHeight = -1f; gle.preferredHeight = -1f; }
				}
				catch (Exception e) { Killiorim.Logger.LogWarning($"[QMTab] grid constraint failed: {e.Message}"); }

				SpriteIndex.Invalidate();
				var acts = ActsFor(_qmPage);

				var cards = new List<Transform>();
				for (int i = 0; i < grid.transform.childCount; i++) cards.Add(grid.transform.GetChild(i));

				// The page is built from the Launchpad's OWN cards, and that grid ships exactly six.
				// Anything past the sixth act has no card to land on, so it would silently never
				// appear — clone more from the last real one until there are enough. Cloning an
				// already-styled sibling is what keeps the new tiles looking native.
				if (cards.Count > 0 && acts.Count > cards.Count)
				{
					var template = cards[cards.Count - 1];
					int missing = acts.Count - cards.Count;
					for (int k = 0; k < missing; k++)
					{
						try
						{
							var extra = UnityEngine.Object.Instantiate(template.gameObject, grid.transform);
							extra.name = "Button_VAExtra" + k;
							extra.SetActive(true);
							extra.transform.SetAsLastSibling();
							// Forget the template's icon so CopySprite pulls the ROTATED donor's one in.
							try { var ei = extra.transform.Find("Icons/Icon")?.GetComponent<UnityEngine.UI.Image>(); if (ei != null) ei.sprite = null; } catch { }
							cards.Add(extra.transform);
						}
						catch (Exception e)
						{
							Killiorim.Logger.LogWarning($"[QMTab] could not add tile {k}: {e.Message}");
							break;
						}
					}
				}

				_toggles.Clear();
				_pairs.Clear();
				_spritesResolved = false;
				int used = 0;
				for (int i = 0; i < cards.Count; i++)
				{
					var card = cards[i];
					if (card == null) continue;
					if (i >= acts.Count) { try { UnityEngine.Object.DestroyImmediate(card.gameObject); } catch { } continue; }
					// Cloned-in tiles have no donor of their own; reuse the last real one so they are
					// styled from a card VRChat has actually themed.
					// Rotated, not pinned to the last card: every extra tile used to borrow the sixth
					// card's icon, which is how Radar and Player list came out as two storefronts.
					Transform donorCard = donorGrid.childCount > 0 ? donorGrid.GetChild(i % donorGrid.childCount) : null;
					SetupCard(card, acts[i], donorCard);
					if (donorCard != null) _pairs.Add(new Pair { Clone = card, Donor = donorCard });
					if (acts[i].State != null)
						_toggles.Add(new ToggleTile { Card = card, State = acts[i].State, Last = !acts[i].State() });
					used++;
				}
				RefreshToggles();

				var header = page.Find("Header_DevTools") ?? page.Find("Header_H1");
				if (header != null)
				{
					var htmp = header.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (htmp != null) htmp.text = TitleFor(_qmPage);
				}

				Killiorim.Logger.LogInfo($"[QMTab] page filled with {used} Launchpad-styled tile(s).");
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[QMTab] page fill failed: {e.Message}"); }
		}

		private void ApplyArchiveGridLayout()
		{
			try
			{
				if (_archiveGrid == null) return;
				var grid = _archiveGrid.GetComponent<UnityEngine.UI.GridLayoutGroup>();
				if (grid == null) return;

				grid.constraint = UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount;
				grid.constraintCount = 2;
				float width = _archiveContent != null
					? _archiveContent.GetComponent<RectTransform>().rect.width : 0f;
				if (width <= 1f) width = 920f;
				float cellWidth = Mathf.Max(160f, (width - grid.padding.horizontal - grid.spacing.x) * 0.5f);
				Vector2 size = new Vector2(cellWidth, 78f);
				if ((grid.cellSize - size).sqrMagnitude > 1f) grid.cellSize = size;
			}
			catch { }
		}

		// The card recipe now lives in Core.MenuCard and is shared with the per-user menu, so a fix
		// on one is a fix on both. Keeping a second copy here is what let the two drift apart.
		private static void SetupCard(Transform card, Act act, Transform donor)
		{
			bool lit = false;
			try { lit = act.State != null && act.State(); } catch { }
			// keepStyle: same as the per-user cards, and for the same reason — VRChat's own
			// StyleElement themes the tile, so it matches the Launchpad cards it was cloned from
			// instead of wearing a palette of ours. The ON state rides the game's Foreground
			// overlay (see MenuCard.SetLit), which is how VRChat lights its own toggle cards.
			Core.MenuCard.Setup(card, donor, act.Label, act.Do, lit, keepStyle: true);
			Core.MenuCard.StripBadges(card);

			// The game's own glyph for what the tile does, when one of the hints names a loaded
			// sprite. Resolved from SpriteIndex (built once per page fill); a miss keeps the donor
			// icon, which the rotated donor choice keeps from repeating across the grid.
			if (!string.IsNullOrEmpty(act.IconName))
			{
				var sp = SpriteIndex.Find(act.IconName);
				if (sp != null) Core.MenuCard.SetIcon(card, sp);
			}
			Core.MenuCard.TintIcon(card);

			// Our own icon, if the tile brought one. Done after Setup so it overrides the sprite the
			// clone inherited rather than being overwritten by it.
			if (act.Icon != null)
			{
				try
				{
					var tex = act.Icon();
					if (tex != null) Core.MenuCard.SetIcon(card, tex);
				}
				catch (Exception e) { Killiorim.Logger.LogWarning($"[QMTab] icon for '{act.Label}' failed: {e.Message}"); }
			}
		}


		// ---------------------------------------------------------------- native sliders
		//
		// VRChat's own settings rows carry a plain Unity Slider inside a RightItemContainer, styled
		// by the game. Cloning one gives a slider that looks and behaves like every other slider in
		// the menu — where a card cannot: a QuickMenu card is a button, so the speed tiles could
		// only ever cycle through preset values.
		//
		// Donor path comes from a live capture: a Settings row such as NameplateOpacity ->
		// RightItemContainer -> Slider (+ Background / Fill Area / Fill / Handle Slide Area).
		private sealed class SliderRow
		{
			public Transform Go;
			public UnityEngine.UI.Slider S;
			public TMPro.TMP_Text Label;
			public TMPro.TMP_Text Value;   // the donor's own right-hand readout
			public BepInEx.Configuration.ConfigEntry<float> Cfg;
			public string Title;
			public float Min, Max;
		}

		private readonly List<SliderRow> _sliders = new List<SliderRow>();
		private Transform _sliderHost;
		// BUILT FROM SCRATCH, NOT CLONED. Cloning VRChat's own slider row failed five different
		// ways: its right-anchored container collapsed in a narrow page, its label refused to
		// re-target, its rows stacked on top of each other. Every one of those was a fight with a
		// layout the donor row was built for and this page is not.
		//
		// So the rows are made from plain Unity UI, where every piece is ours: a label, a Slider
		// assembled from Background/Fill/Handle images, and a value readout. The ONE thing that
		// cannot be made from nothing is a font — a fresh TextMeshPro has none and renders
		// invisibly — so the font is lifted off a text the page already shows.
		private static TMPro.TMP_FontAsset _font;

		private static TMPro.TMP_FontAsset StealFont()
		{
			if (_font != null) return _font;
			try
			{
				Transform root = Core.QuickMenu.Root() ?? Core.QuickMenu.Main();
				var t = root != null ? root.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
				if (t != null) _font = t.font;
			}
			catch { }
			return _font;
		}

		private void BuildSliders(Transform content)
		{
			if (_sliders.Count > 0)
			{
				if (_sliderHost != null && _sliderHost.gameObject != null) return;
				_sliders.Clear(); _sliderHost = null;
			}

			// A NORMAL LAYOUT CHILD of `content` (the page's VerticalLayoutGroup), added AFTER the
			// button grid so it flows BELOW the buttons and scrolls with VRChat's own page scroll.
			// The previous "fixed footer pinned to the Viewport with ignoreLayout" reported zero
			// height to the layout and the whole slider section vanished. A real
			// LayoutElement.preferredHeight (plus an explicit sizeDelta, to cover a layout group that
			// controls child height and one that does not) is what reserves the space and shows it.
			const float RowH = 44f, Pad = 16f;
			float panelH = 4 * RowH + 3 * 8f + 2 * Pad;   // 4 rows + gaps + padding = 232

			var hostGo = new GameObject("VA_Sliders", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
			var host = hostGo.GetComponent<RectTransform>();
			host.SetParent(content, false);
			host.anchorMin = new Vector2(0f, 1f);
			host.anchorMax = new Vector2(1f, 1f);
			host.pivot = new Vector2(0.5f, 1f);
			host.sizeDelta = new Vector2(0f, panelH);

			// The dark panel behind the rows — what makes it read like VRChat's Audio Volume block.
			var bgIm = hostGo.AddComponent<UnityEngine.UI.Image>();
			bgIm.color = new Color(0.06f, 0.07f, 0.11f, 0.86f);

			// Real height so the page's VerticalLayoutGroup reserves and scrolls the panel.
			var hostLe = hostGo.AddComponent<UnityEngine.UI.LayoutElement>();
			hostLe.ignoreLayout = false;
			hostLe.minHeight = panelH; hostLe.preferredHeight = panelH; hostLe.flexibleWidth = 1f;

			var vlg = hostGo.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
			vlg.childForceExpandHeight = false; vlg.childControlHeight = false;
			vlg.childForceExpandWidth = true;  vlg.childControlWidth = true;
			vlg.childAlignment = TextAnchor.UpperCenter;
			vlg.spacing = 8f; vlg.padding = new RectOffset((int)Pad, (int)Pad, (int)Pad, (int)Pad);

			host.SetAsLastSibling();   // after the button grid
			_sliderHost = host.transform;

			MakeRow(host, "Jump force", ModConfig.JumpImpulse, 0f, 15f);
			MakeRow(host, "Fly speed",  ModConfig.FlySpeed,    1f, 60f);
			MakeRow(host, "Walk speed", ModConfig.WalkSpeed,   0.1f, 20f);
			MakeRow(host, "Run speed",  ModConfig.RunSpeed,    0.1f, 40f);

			Killiorim.Logger.LogInfo($"[QMTab] built {_sliders.Count} slider row(s) below the buttons.");
		}

		private static RectTransform NewRect(string name, Transform parent)
		{
			var go = new GameObject(name, Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);
			return rt;
		}

		private static UnityEngine.UI.Image Img(RectTransform rt, Color c)
		{
			var im = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
			im.color = c;
			return im;
		}

		private static TMPro.TextMeshProUGUI Label(RectTransform rt, string text, float size, TMPro.TextAlignmentOptions align)
		{
			var tmp = rt.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
			var f = StealFont();
			if (f != null) tmp.font = f;
			tmp.text = text;
			tmp.fontSize = size;
			tmp.alignment = align;
			tmp.enableWordWrapping = false;
			tmp.color = new Color(0.92f, 0.90f, 1f);
			return tmp;
		}

		private void MakeRow(Transform host, string title,
							 BepInEx.Configuration.ConfigEntry<float> cfg, float min, float max)
		{
			try
			{
				// One row, laid out by hand in a fixed 48-high strip: label | slider | value.
				var row = NewRect("VA_Row_" + title.Replace(" ", ""), host);
				var rle = row.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
				rle.minHeight = 44f; rle.preferredHeight = 44f; rle.flexibleWidth = 1f;
				row.sizeDelta = new Vector2(0f, 44f);

				// label — left third
				var lab = NewRect("Label", row);
				lab.anchorMin = new Vector2(0f, 0f); lab.anchorMax = new Vector2(0.32f, 1f);
				lab.offsetMin = Vector2.zero; lab.offsetMax = Vector2.zero;
				var labelTmp = Label(lab, title, 15f, TMPro.TextAlignmentOptions.MidlineLeft);

				// value — right, fixed width
				var val = NewRect("Value", row);
				val.anchorMin = new Vector2(0.86f, 0f); val.anchorMax = new Vector2(1f, 1f);
				val.offsetMin = Vector2.zero; val.offsetMax = Vector2.zero;
				var valueTmp = Label(val, "", 15f, TMPro.TextAlignmentOptions.MidlineRight);

				// slider — the middle, between label and value
				var slRt = NewRect("Slider", row);
				slRt.anchorMin = new Vector2(0.34f, 0.25f); slRt.anchorMax = new Vector2(0.84f, 0.75f);
				slRt.offsetMin = Vector2.zero; slRt.offsetMax = Vector2.zero;
				var sl = slRt.gameObject.AddComponent<UnityEngine.UI.Slider>();

				// track
				var bg = NewRect("Background", slRt);
				bg.anchorMin = Vector2.zero; bg.anchorMax = Vector2.one;
				bg.offsetMin = Vector2.zero; bg.offsetMax = Vector2.zero;
				Img(bg, new Color(0.10f, 0.12f, 0.18f, 0.9f));

				// fill
				var fillArea = NewRect("Fill Area", slRt);
				fillArea.anchorMin = Vector2.zero; fillArea.anchorMax = Vector2.one;
				fillArea.offsetMin = Vector2.zero; fillArea.offsetMax = Vector2.zero;
				var fill = NewRect("Fill", fillArea);
				fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0f, 1f);
				fill.offsetMin = Vector2.zero; fill.offsetMax = Vector2.zero;
				Img(fill, new Color(0.24f, 0.72f, 0.80f, 1f));

				// handle
				var handleArea = NewRect("Handle Slide Area", slRt);
				handleArea.anchorMin = Vector2.zero; handleArea.anchorMax = Vector2.one;
				handleArea.offsetMin = Vector2.zero; handleArea.offsetMax = Vector2.zero;
				var handle = NewRect("Handle", handleArea);
				handle.sizeDelta = new Vector2(14f, 0f);
				Img(handle, Color.white);

				sl.fillRect = fill;
				sl.handleRect = handle;
				sl.targetGraphic = handle.GetComponent<UnityEngine.UI.Image>();
				sl.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
				sl.minValue = min; sl.maxValue = max; sl.wholeNumbers = false;

				var srow = new SliderRow { Go = row, S = sl, Label = labelTmp, Value = valueTmp, Cfg = cfg, Title = title, Min = min, Max = max };
				try { sl.value = Mathf.Clamp(cfg.Value, min, max); } catch { }
				Core.UiClick.AddValueChanged(sl, v =>
				{
					try { cfg.Value = v; } catch { }
					Retitle(srow);
				});

				Retitle(srow);
				_sliders.Add(srow);
			}
			catch (Exception e) { Killiorim.Logger.LogWarning("[QMTab] make row '" + title + "': " + e.Message); }
		}






		// Name on the left, value on the right — the row's own two slots, used for what they are.
		// The readout is a PERCENTAGE of the slider's range, which is what the donor row shows and
		// what makes four different units (metres per second, jump impulse) comparable at a glance.
		private static void Retitle(SliderRow r)
		{
			try
			{
				if (r.Label != null) r.Label.text = r.Title;

				if (r.Value != null)
				{
					float span = Mathf.Max(0.0001f, r.Max - r.Min);
					int pct = Mathf.RoundToInt((r.Cfg.Value - r.Min) / span * 100f);
					r.Value.text = pct + "%";
				}
				else if (r.Label != null)
				{
					// No second slot on this donor: fall back to putting the number in the label.
					r.Label.text = r.Title + "   " + r.Cfg.Value.ToString("0.#");
				}
			}
			catch { }
		}

		private static Transform FindDeep(Transform t, string name)
		{
			try
			{
				foreach (var c in t.GetComponentsInChildren<Transform>(true))
					if (c != null && c.name == name) return c;
			}
			catch { }
			return null;
		}

		// Sliders belong to the Movement page only.
		private void SyncSliders()
		{
			try
			{
				bool want = _qmPage == QmPage.Movement;
				if (want && _sliders.Count == 0 && _page != null)
				{
					var content = FindContent(_page.transform);
					if (content != null) BuildSliders(content);
				}
				if (_sliderHost != null && _sliderHost.gameObject.activeSelf != want)
					_sliderHost.gameObject.SetActive(want);

				// Re-assert the order: FillPage adds the grid after us when the page changes.
				if (want && _sliderHost != null
					&& _sliderHost.GetSiblingIndex() != _sliderHost.parent.childCount - 1)
					_sliderHost.SetAsLastSibling();

				// The value can change from the mod's own menu too, so follow it.
				if (want)
					foreach (var r in _sliders)
					{
						if (r?.S == null) continue;
						try
						{
							float v = Mathf.Clamp(r.Cfg.Value, r.Min, r.Max);
							if (Mathf.Abs(r.S.value - v) > 0.001f) { r.S.SetValueWithoutNotify(v); Retitle(r); }
						}
						catch { }
					}
			}
			catch { }
		}

		// THE HEADER'S BACK ARROW — a CLONE of the Button_Back that Menu_DevTools ships disabled.
		//
		// It used to be VRChat's own object: we switched it on, wiped its listeners and re-pointed
		// it at Root. That breaks rule 1 (never modify VRChat's objects) and it never held — the
		// game's page controller still owned that button, re-styled it and re-armed it to ITS page
		// stack, so the arrow either did nothing or popped a VRChat page instead of ours. The
		// clone sits in the same slot (first in the header's left container, where every VRChat
		// back arrow sits) and keeps the child icon's own styling; its ROOT is stripped like every
		// other clone of ours, and it gets a fresh Button so none of VRChat's serialized onClick
		// targets ride along. The original stays exactly as shipped: disabled, untouched.
		private Transform _backBtn;      // VRChat's own — read as the donor, never written
		private Transform _backClone;
		private bool _backWired;

		private void SyncBackButton()
		{
			try
			{
				if (_page == null) return;
				if (_backBtn == null)
				{
					var header = _page.transform.Find("Header_DevTools") ?? _page.transform.Find("Header_H1");
					if (header == null) return;
					_backBtn = header.Find("LeftItemContainer/Button_Back") ?? header.Find("Button_Back");
					if (_backBtn == null) return;
				}

				if (_backClone == null)
				{
					// Unity-null covers a canvas rebuild too: a dead clone means a dead listener,
					// so the wire flag drops with it and the new clone is wired below.
					_backWired = false;
					var go = UnityEngine.Object.Instantiate(_backBtn.gameObject, _backBtn.parent);
					go.name = "VA_Button_Back";
					_backClone = go.transform;
					_backClone.SetAsFirstSibling();

					// The cloned Button carries VRChat's persistent onClick calls; a stripped root
					// cannot be trusted to have nulled every one of them, so it goes and a clean
					// Button takes its target graphic.
					var old = go.GetComponent<UnityEngine.UI.Button>();
					UnityEngine.UI.Graphic target = null;
					try { if (old != null) target = old.targetGraphic; } catch { }
					try { if (old != null) UnityEngine.Object.DestroyImmediate(old); } catch { }
					StripRoot(_backClone);

					var btn = go.AddComponent<UnityEngine.UI.Button>();
					if (target == null) target = go.GetComponent<UnityEngine.UI.Graphic>() ?? go.GetComponentInChildren<UnityEngine.UI.Graphic>(true);
					if (target != null) btn.targetGraphic = target;
					// Same feedback rule as our cards (MenuCard.Setup): white tint = untouched at
					// rest, brighter on hover/press, so the arrow visibly reacts to the pointer.
					btn.transition = UnityEngine.UI.Selectable.Transition.ColorTint;
					btn.colors = Core.MenuCard.Tint(Color.white);
					btn.interactable = true;
				}

				if (!_backWired)
				{
					var btn = _backClone.GetComponent<UnityEngine.UI.Button>();
					if (btn != null)
					{
						Core.UiClick.AddClick(btn, () => GoTo(QmPage.Root));
						_backWired = true;
					}
				}

				// Shown only on a sub-page AND only once it actually does something — an arrow
				// that is lit but unwired is a lie the user clicks three times.
				bool want = _qmPage != QmPage.Root && _backWired;
				if (_backClone.gameObject.activeSelf != want) _backClone.gameObject.SetActive(want);
			}
			catch { }
		}

		// Every QuickMenu page shares <page>/ScrollRect/Viewport/VerticalLayoutGroup as its content.
		private static Transform FindContent(Transform page)
		{
			string[] paths = { "ScrollRect/Viewport/VerticalLayoutGroup", "Scrollrect/Viewport/VerticalLayoutGroup" };
			foreach (string p in paths) { var t = page.Find(p); if (t != null) return t; }
			return null;
		}

		internal static Transform ArchiveContent()
		{
			try
			{
				Transform qm = Core.QuickMenu.Root();
				Transform page = qm != null ? qm.Find(BodyPath + "/Menu_DevTools") : null;
				Transform content = page != null ? FindContent(page) : null;
				return content != null && content.Find("Buttons_Archive") != null ? content : null;
			}
			catch { return null; }
		}

		// VRChat's toggle tile shows its state by swapping two children, Icon_Off and Icon_On.
		// Driving those from our config is what makes the tile behave like the game's own.
		// VRChat's cards carry a 'Foreground' highlight layer that ships disabled — that is the
		// game's own way of showing a card as lit. Driving it from our config gives the tiles a
		// real ON/OFF state using VRChat's own artwork.
		// Re-copy any sprite that was still unassigned when the page was built (VRChat resolves some
		// of them only once a page has been shown). Runs while our page is open, then stops.
		private void RepairSprites()
		{
			if (_spritesResolved || _pairs.Count == 0) return;
			bool allDone = true;
			for (int i = 0; i < _pairs.Count; i++)
			{
				var p = _pairs[i];
				if (p == null || p.Clone == null || p.Donor == null) continue;
				if (!Core.MenuCard.CopySprite(p.Donor, p.Clone, "Background")) allDone = false;
				if (!Core.MenuCard.CopySprite(p.Donor, p.Clone, "Icons/Icon")) allDone = false;
			}
			if (allDone)
			{
				_spritesResolved = true;
				Killiorim.Logger.LogInfo("[QMTab] card sprites resolved.");
			}
		}

		private void RefreshToggles()
		{
			for (int i = 0; i < _toggles.Count; i++)
			{
				var t = _toggles[i];
				if (t == null || t.Card == null) continue;
				bool on;
				try { on = t.State(); } catch { continue; }
				if (on == t.Last) continue;
				t.Last = on;
				try
				{
					// Brighten the card's own Background for the lit state. Enabling VRChat's
					// Foreground overlay washed the tile out and made ON read as "disabled".
					Core.MenuCard.SetLit(t.Card, on, keepStyle: true);
				}
				catch { }
			}
		}

		// VRChat's own tab controller decides when the page is shown, so there is nothing to
		// force here any more — we only keep our toggle tiles reflecting the live config.
		private bool _wasActive;   // our page's activeSelf on the previous tick: gives the "just shown" edge

		private void Pump()
		{
			try
			{
				bool active = _page != null && _page.activeSelf;
				bool shown = active && !_wasActive;
				_wasActive = active;
				if (!active)
				{
					// Hidden: forget the sub-page so the next open lands on Root, like every VRChat
					// page does. Reopening straight onto Movement showed a lit Back arrow on a page
					// the user never navigated to, and FillPage below rebuilds it from Root.
					if (_qmPage != QmPage.Root) { _qmPage = QmPage.Root; _pageDirty = true; }
					return;
				}

				// A tile asked for another sub-page: rebuild the grid with that set. Done here, in
				// the pump, rather than inside the click handler — a click runs during VRChat's own
				// UI event dispatch, and destroying the very cards being dispatched to is how you
				// get a crash instead of a menu.
				if (_pageDirty)
				{
					_pageDirty = false;
					try { FillPage(_page.transform, _body); }
					catch (Exception e) { Killiorim.Logger.LogWarning("[QMTab] sub-page rebuild: " + e.Message); }
				}

				// VRChat's own back arrow ships in this header, disabled. Lit only on a sub-page,
				// where it means what it says.
				SyncBackButton();
				SyncSliders();
				QMConsole.KeepLast();

				// A live capture caught Menu_DevTools active AT THE SAME TIME as Menu_QM_Launchpad.
				// In uGUI the later sibling draws on top, and Launchpad is the later one — so it
				// covered our page and swallowed every click.
				//
				// The fix used to be "last sibling, EVERY frame". That kept us above every page
				// VRChat stacks in Body after ours — whatever it opens on top while our tab is
				// still active rendered underneath us and could not be clicked, one of the
				// "buttons do nothing" reports. Now: one SetAsLastSibling on the show edge (the
				// game has just reordered Body for its own page, so a single reorder lands above
				// it), then only ever "just above Launchpad" — the one page the capture proved
				// overlaps us — so anything VRChat stacks later stays on top and clickable.
				if (shown) _page.transform.SetAsLastSibling();
				var lp = _body != null ? _body.Find("Menu_QM_Launchpad") : null;
				if (lp != null && lp.gameObject.activeSelf)
				{
					int want = lp.GetSiblingIndex() + 1;
					if (_page.transform.GetSiblingIndex() < want) _page.transform.SetSiblingIndex(want);
				}

				RepairSprites();

				RefreshToggles();

				// Re-assert card geometry twice a second while our page is open. Placing the icon
				// and label once at build time was not holding — VRChat restyles and re-lays a page
				// when it is shown, and whatever it does to ours happens after we are done building.
				float now = Time.realtimeSinceStartup;
				if (now >= _nextLayout)
				{
					_nextLayout = now + 0.5f;
					ApplyArchiveGridLayout();
					for (int i = 0; i < _pairs.Count; i++)
					{
						var p = _pairs[i];
						if (p != null && p.Clone != null) Core.MenuCard.Relayout(p.Clone);
					}
				}
			}
			catch { }
		}

		private float _nextLayout;

		// These are VRChat's OWN objects now, not clones — put them back the way we found them
		// instead of destroying them.
		private void Teardown()
		{
			try { if (_page != null) _page.SetActive(_pageWasActive); } catch { }
			try { if (_tab != null) _tab.SetActive(_tabWasActive); } catch { }
			// The back-arrow clone is OURS, parented under VRChat's header: destroy it so the page
			// is left exactly as found. The original Button_Back was never touched.
			try { if (_backClone != null) UnityEngine.Object.Destroy(_backClone.gameObject); } catch { }
			_backClone = null; _backBtn = null; _backWired = false; _wasActive = false;
			_page = null; _tab = null; _body = null; _archiveGrid = null; _archiveContent = null; _fails = 0; _nextTry = 0f;
			_toggles.Clear();
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
