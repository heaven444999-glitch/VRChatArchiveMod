using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MENU THEME — repaints VRChat's QuickMenu in the Archive's pink → violet instead of its stock
	// teal, and turns its text violet.
	//
	// It has to run on a TIMER rather than once. VRChat themes its own UI through StyleEngine /
	// StyleElement, which re-asserts colours whenever a page is shown, restyled or rebuilt; a
	// one-shot pass is undone the moment you open a different tab. Every 2 seconds is often enough
	// to look permanent and rare enough to cost nothing.
	//
	// The "gradient" is across the GRID, not inside each button: a UI Image is a single flat colour,
	// so there is no way to gradient one card on its own without a custom shader. Instead each card
	// is tinted by WHERE it sits on screen, so a page of buttons reads as one pink→violet sweep.
	// That is an honest approximation, and it is the one that actually looks like something.
	//
	// Everything is remembered and restored: switch the toggle off and the menu goes back to
	// VRChat's own colours without a restart.
	public class MenuThemeModule : IModule
	{
		public override string Name => "MenuTheme";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";

		private static readonly Color Pink = new Color(1.000f, 0.416f, 0.835f);
		private static readonly Color Violet = new Color(0.506f, 0.263f, 0.902f);
		private static readonly Color TextViolet = new Color(0.827f, 0.643f, 1.000f);

		// Targets found by the LAST full scan. Re-asserting colours on these is a handful of property
		// writes; finding them again means walking every Image and every text in the menu, which is
		// what made this module the most expensive thing in the profiler. The scan is rare, the
		// re-assert is frequent.
		private sealed class Target
		{
			public Image Img;
			public Selectable Sel;      // resolved once: GetComponentInParent per image per pass is
			public Color Want;          // a tree walk of its own
			public bool Ours;
		}
		// ONE LIST PER CANVAS. Both used to share a single list that was CLEARED on every scan —
		// and since a scan only walks one canvas, the other canvas's targets were thrown away and
		// went back to VRChat's teal until its own turn came round, up to five seconds later. That
		// is the unreliable colouring: at any moment only half the menu was actually ours.
		//
		// Keeping them apart means a scan replaces only what it just rediscovered, and the fast
		// pass re-asserts BOTH every tick. The reason the scan was split in the first place — never
		// paying for two full walks in one frame — is untouched.
		private readonly List<Target>[] _targetsBy = { new List<Target>(), new List<Target>() };
		private readonly List<TMPro.TMP_Text>[] _liveBy = { new List<TMPro.TMP_Text>(), new List<TMPro.TMP_Text>() };
		private int _slot;   // which canvas the CURRENT scan is filling

		private List<Target> _targets => _targetsBy[_slot];
		private float _nextScan;
		private bool _scanFlip;   // alternates the two canvases so one frame never pays for both

		private readonly List<(Graphic G, Color C)> _painted = new List<(Graphic, Color)>();
		private readonly List<(TMPro.TMP_Text T, Color C)> _texts = new List<(TMPro.TMP_Text, Color)>();
		private readonly HashSet<int> _seen = new HashSet<int>();
		private float _next;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.MenuThemeEnabled.Value)
				{
					if (_painted.Count > 0 || _texts.Count > 0) RestoreAll();
					return;
				}

				float now = Time.realtimeSinceStartup;
				if (now < _next) return;
				_next = now + 0.35f;

				// A CLOSED menu needs no theming. This module walks every Image and every text in the
				// menu, and it was doing that several times a second whether or not anybody was
				// looking at it — the single most expensive thing the mod did. Skipping the work
				// beats making it cheaper.
				if (!Core.QuickMenu.AnyVisible) return;

				Repaint();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuTheme] update threw: {e.Message}"); }
		}

		public override void OnShutdown() => RestoreAll();

		public override void OnSceneLoaded(int buildIndex)
		{
			// Those Graphics belong to a canvas that may have been rebuilt; forget them rather than
			// hold references we can no longer restore correctly.
			_painted.Clear(); _texts.Clear(); _seen.Clear();
			_live.Clear(); _targets.Clear();
			_next = 0f; _nextScan = 0f;
			Core.QuickMenu.Forget();   // the canvas may not survive the world change
		}

		private void Repaint()
		{
			float now = Time.realtimeSinceStartup;
			if (now >= _nextScan || (_targetsBy[0].Count == 0 && _targetsBy[1].Count == 0))
			{
				// ONE CANVAS PER SCAN, AND LESS OFTEN.
				//
				// The spike hunter caught this module producing 143 ms frames. Both canvases were
				// walked in the SAME frame, every three seconds — GetComponentsInChildren over every
				// text and every image of the entire menu twice over — so the cost of the whole job
				// landed on one unlucky frame, which is precisely what a stutter is.
				//
				// Splitting the two canvases across alternate scans halves the worst frame, and 5s
				// instead of 3s makes those frames rarer. Nothing is lost: the FAST pass below
				// re-asserts colours every tick from the cached target list, so what the user sees
				// is unchanged — only the rediscovery is slower, and the menu's structure does not
				// change between one three-second window and the next anyway.
				_nextScan = now + 5f;
				_scanFlip = !_scanFlip;
				_slot = _scanFlip ? 0 : 1;
				// Only this canvas's own findings are replaced; the other canvas keeps its colours.
				_targets.Clear();
				_live.Clear();
				// The QuickMenu is the wrist menu; everything else the user opens — the avatar page,
				// settings, the world list — lives on Canvas_MainMenu, which this module never
				// looked at, which is why its text stayed VRChat's own colour.
				if (_scanFlip) Scan(Core.QuickMenu.Root());
				else Scan(Core.QuickMenu.Main());
			}

			// Text is re-asserted on the FAST pass, not only when we rescan. VRChat's StyleEngine
			// repaints a page the moment it is shown, so a colour only refreshed every 3 seconds
			// flickers back to stock every time you change tab.
			for (int canvas = 0; canvas < 2; canvas++)
			for (int i = 0; i < _liveBy[canvas].Count; i++)
			{
				try
				{
					var t = _live[i];
					if (t == null) continue;
					if (t.color != TextViolet) t.color = TextViolet;
				}
				catch { }
			}

			// The cheap pass: no searching, just putting the colours back where VRChat may have
			// undone them.
			for (int i = 0; i < _targets.Count; i++)
			{
				var t = _targets[i];
				try
				{
					if (t.Img == null) continue;
					if (t.Sel != null)
					{
						var cb = t.Sel.colors;
						if (cb.normalColor != t.Want)
						{
							cb.normalColor = t.Want;
							cb.highlightedColor = new Color(t.Want.r * 1.75f, t.Want.g * 1.55f, t.Want.b * 1.55f, 1f);
							cb.pressedColor = new Color(t.Want.r * 2.3f, t.Want.g * 1.9f, t.Want.b * 1.9f, 1f);
							cb.selectedColor = cb.highlightedColor;
							cb.colorMultiplier = 1f;
							t.Sel.colors = cb;
						}
					}
					else if (t.Img.color != t.Want)
					{
						t.Img.color = t.Want;
					}
				}
				catch { }
			}
		}

		// Every text currently themed, rebuilt on each scan. Separate from _texts, which is the
		// restore ledger and must keep the ORIGINAL colour of everything ever touched.
		private List<TMPro.TMP_Text> _live => _liveBy[_slot];

		// True for the text INSIDE a text field — the editable content, its placeholder, or
		// anything else parented under the field. Checked a few levels up rather than on the
		// component itself, because the field sits above its text in the hierarchy.
		// True when this text lives inside one of ArchiveFavButtonModule's cloned buttons — the
		// Save/Remove-to-Archive CTA and the Copy-id / Get-metadata pair. Those are ours to colour.
		private static bool IsOurButton(Transform t)
		{
			try
			{
				for (Transform p = t; p != null; p = p.parent)
				{
					string n = p.name ?? "";
					if (n == "VA_ArchiveFavButton" || n == "VA_CopyId" || n == "VA_GetMeta") return true;
				}
			}
			catch { }
			return false;
		}

		private static bool IsEditable(TMPro.TMP_Text t)
		{
			try
			{
				Transform p = t.transform;
				for (int i = 0; i < 4 && p != null; i++, p = p.parent)
				{
					if (p.GetComponent<TMPro.TMP_InputField>() != null) return true;
					if (p.GetComponent<UnityEngine.UI.InputField>() != null) return true;
				}
			}
			catch { }
			return false;
		}

		private void Scan(Transform qm)
		{
			if (qm == null) return;
			float h = Mathf.Max(1f, Screen.height);

			// ---- text ------------------------------------------------------------------------
			try
			{
				var texts = qm.GetComponentsInChildren<TMPro.TMP_Text>(false);
				if (texts != null)
				{
					foreach (var t in texts)
					{
						if (t == null) continue;
						try
						{
							// NEVER an input field's text. Its component is driven by the field
							// itself — caret, selection, IME composition — and writing to it several
							// times a second from outside fights all three. Chat became untypeable
							// the day this module started covering the main menu, which is where
							// VRChat's text inputs live.
							if (IsEditable(t)) continue;

							// NEVER the Archive buttons' labels. ArchiveFavButtonModule owns those
							// (white text on flat violet); painting them violet here is exactly why
							// "Remove from Archive" came out violet-on-dark instead of clean white.
							if (IsOurButton(t.transform)) continue;

							int id = t.GetInstanceID();
							if (_seen.Add(id)) _texts.Add((t, t.color));   // remember how to undo it
							t.color = TextViolet;
							_live.Add(t);
						}
						catch { }
					}
				}
			}
			catch { }

			// ---- card backgrounds -------------------------------------------------------------
			try
			{
				var imgs = qm.GetComponentsInChildren<Image>(false);
				if (imgs == null) return;

				foreach (var im in imgs)
				{
					if (im == null) continue;
					try
					{
						var tr = im.transform;
						// Only the panel behind a button. Icons keep their own art, and a blanket
						// repaint of every Image turns the menu into a solid block of colour.
						if ((tr.name ?? "") != "Background") continue;
						var parent = tr.parent;
						if (parent == null || !(parent.name ?? "").StartsWith("Button_", StringComparison.Ordinal)) continue;


						// Position on screen drives the tint, so the grid sweeps pink at the top to
						// violet at the bottom.
						float y = 0.5f;
						try { y = Mathf.Clamp01(tr.position.y / h); } catch { }
						Color want = Color.Lerp(Pink, Violet, 1f - y);
						// Kept dark enough to read white-on-top, and to sit against the wallpaper.
						want = new Color(want.r * 0.34f, want.g * 0.30f, want.b * 0.46f, 1f);

						int id = im.GetInstanceID();
						if (_seen.Add(id)) _painted.Add((im, im.color));

						// Hand the colour to the SELECTABLE when there is one, instead of only
						// stamping the Image. VRChat tints a card on hover through its own
						// transition; painting the Image behind its back means the two take turns
						// writing the same pixel — which is the flicker you get when the mouse
						// passes over a tile. Driving the ColorBlock makes hover and press derive
						// FROM our colour, so there is nothing left to fight over.
						var sel = im.GetComponentInParent<UnityEngine.UI.Selectable>();
						if (sel != null && sel.targetGraphic != im) sel = null;
						// A Selectable whose transition is NOT ColorTint ignores its ColorBlock
						// entirely, so writing one is a no-op. Our own cloned cards are exactly that
						// case — MenuCard sets Transition.None under keepStyle so VRChat's
						// StyleElement can own them — which is why the two cards in the user menu
						// stayed teal while every card around them went violet. Dropping the
						// Selectable here sends them down the paint-the-Image path instead.
						if (sel != null && sel.transition != UnityEngine.UI.Selectable.Transition.ColorTint) sel = null;
						_targets.Add(new Target { Img = im, Sel = sel, Want = want, Ours = IsOurGrid(parent) });

						// The BACKGROUND above is themed for every card including ours, so the Archive tab
						// matches the rest of the menu. The RIM is different: on our tiles it carries the
						// toggle state (MenuCard paints it pink/blue), and repainting it here every 0.35s
						// would wipe that out a third of a second after every press.
						if (!IsOurGrid(parent)) InnerGlow(tr, want);
					}
					catch { }
				}
			}
			catch { }
		}

		// INNER GLOW — a violet rim that fades INWARD from the card's edge.
		//
		// Built as a 9-sliced sprite rather than a stretched gradient: a plain radial texture
		// stretched across a wide card would smear its falloff into an oval. With a border, the rim
		// keeps the same thickness whatever size the card is.
		private const string GlowChild = "VA_InnerGlow";

		private void InnerGlow(Transform background, Color baseColor)
		{
			try
			{
				var existing = background.Find(GlowChild);
				Color rim = new Color(
					Mathf.Clamp01(baseColor.r * 3.4f + 0.18f),
					Mathf.Clamp01(baseColor.g * 2.0f + 0.06f),
					Mathf.Clamp01(baseColor.b * 3.0f + 0.30f), 0.75f);

				if (existing != null)
				{
					var ex = existing.GetComponent<UnityEngine.UI.Image>();
					if (ex != null) ex.color = rim;
					return;
				}

				var sp = Core.MenuCard.RimSprite();
				if (sp == null) return;

				var go = new GameObject(GlowChild);
				var rt = go.AddComponent<RectTransform>();
				rt.SetParent(background, false);
				rt.anchorMin = Vector2.zero;
				rt.anchorMax = Vector2.one;
				rt.offsetMin = Vector2.zero;
				rt.offsetMax = Vector2.zero;

				var img = go.AddComponent<UnityEngine.UI.Image>();
				img.sprite = sp;
				img.type = UnityEngine.UI.Image.Type.Sliced;
				img.color = rim;
				img.raycastTarget = false;      // must never eat the card's clicks
				_glows.Add(go);
			}
			catch { }
		}

		private readonly List<GameObject> _glows = new List<GameObject>();

		// True for a card sitting on the Archive tab's own grid.
		private static bool IsOurGrid(Transform card)
		{
			try
			{
				var g = card.parent;
				return g != null && (g.name ?? "") == "Buttons_Archive";
			}
			catch { return false; }
		}

		private void RestoreAll()
		{
			for (int i = 0; i < _painted.Count; i++)
			{
				try { if (_painted[i].G != null) _painted[i].G.color = _painted[i].C; }
				catch { }
			}
			for (int i = 0; i < _texts.Count; i++)
			{
				try { if (_texts[i].T != null) _texts[i].T.color = _texts[i].C; }
				catch { }
			}
			for (int i = 0; i < _glows.Count; i++)
			{
				try { if (_glows[i] != null) UnityEngine.Object.Destroy(_glows[i]); }
				catch { }
			}
			_glows.Clear();
			_painted.Clear(); _texts.Clear(); _seen.Clear(); _live.Clear(); _targets.Clear();
		}

	}
}
