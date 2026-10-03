using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Killiorim.Core
{
	// ONE recipe for turning a cloned VRChat menu card into one of ours.
	//
	// This lived twice — once in the QuickMenu tab, once in the per-user menu — and the two
	// drifted: the tab got the sprite repair, the white text and the hover sound, the user-menu
	// card did not, so it sat there greyed out next to its neighbours. Every hard-won detail is
	// now in one place:
	//
	//   * strip only the ROOT's foreign components, so VRChat's own handler cannot still fire the
	//     donor's action while the children keep their font and sprites
	//   * reuse the EXISTING Graphic — Unity allows one per object, and these cards already carry
	//     a UIInvisibleGraphic, so AddComponent<Image> returns null and the next line throws
	//   * unlock the CanvasGroup, which is how VRChat greys a card out
	//   * paint the Background, because it has no StyleElement and nothing themes it in a page the
	//     game never opens
	//   * force the label white, otherwise a cloned card keeps a dimmed "disabled" look
	//   * hover/press through Unity's own transition, plus the game's click sound
	public static class MenuCard
	{
		public static readonly Color Bg = new Color(0.10f, 0.10f, 0.10f, 1f);   // black neutral
		public static readonly Color On = new Color(0.90f, 0.90f, 0.90f, 1f);   // lit
		public static readonly Color Glow = new Color(0.75f, 0.75f, 0.75f, 0.85f); // monochrome bloom
		// The two states of a toggle tile, told entirely by the colour of its aura.
		public static readonly Color GlowOn  = new Color(1.000f, 1.000f, 1.000f, 0.90f);  // white = ON
		public static readonly Color GlowOff = new Color(0.36f, 0.36f, 0.36f, 0.55f);  // grey = OFF

		private static readonly HashSet<string> Keep = new HashSet<string>(StringComparer.Ordinal)
		{
			"RectTransform", "CanvasRenderer", "CanvasGroup", "LayoutElement",
			"VerticalLayoutGroup", "HorizontalLayoutGroup", "GridLayoutGroup",
			"Image", "ImageEx", "RawImage", "RawImageEx", "UIInvisibleGraphic", "Button",
		};

		// Configures `card` (a clone of `donor`) as a button. Each step is guarded on its own so a
		// failure to set the label can never stop the click from being wired.
		//
		// `keepStyle` decides WHO paints the card, and the split is not cosmetic taste — it comes
		// from a capture of the live menu (captures/ui_*.txt, 2026-08-24):
		//
		//   Every card VRChat ships carries StyleElement ON ITS ROOT (470 of them in the dump; the
		//   39 without are not cards). The child named "Background" has NO StyleElement of its own,
		//   so the root's is the only thing that themes the card. StripRoot was deleting it, which
		//   is why a clone came out unthemed.
		//
		//   keepStyle: true  — leave StyleElement alone and let VRChat theme the card exactly like
		//     its neighbours. Right for plain action cards (the selected-user page). We then paint
		//     nothing: the game owns the colour and gets it right in every state.
		//   keepStyle: false — we own the colour (Bg / On). Required for the QuickMenu TOGGLE tiles,
		//     whose lit/unlit state is ours to express and would fight VRChat's styling.
		//
		// What is NOT an option is sampling the donor's live colour: a donor that happened to be
		// hovered or selected when we cloned it hands over that state's colour, which is exactly how
		// six identical tiles came out in three different colours.
		public static void Setup(Transform card, Transform donor, string label, Action onClick,
			bool lit = false, bool keepStyle = false)
		{
			try { card.gameObject.SetActive(true); } catch { }

			try { StripRoot(card, keepStyle); } catch { }

			try
			{
				var cg = card.GetComponent<CanvasGroup>();
				if (cg != null) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; }
			}
			catch { }

			// Click first: it is the one step that must never be skipped.
			try
			{
				var g = card.GetComponent<Graphic>();
				if (g == null)
				{
					var img = card.gameObject.AddComponent<Image>();
					img.color = new Color(0f, 0f, 0f, 0f);
					g = img;
				}
				g.raycastTarget = true;

				var bgImg = card.Find("Background")?.GetComponent<Image>();
				var btn = card.GetComponent<Button>() ?? card.gameObject.AddComponent<Button>();
				btn.targetGraphic = bgImg != null ? (Graphic)bgImg : g;
				btn.interactable = true;

				// ALWAYS ColorTint. Transition.None under keepStyle left our cards with no hover and
				// no press feedback at all — a tile you could click that never reacted, which reads
				// as "the button does nothing" even when the click went through. StyleElement only
				// paints the Background's base colour; a ColorTint on the same Button multiplies
				// the hover/pressed shade on top of it, so the two never fight. Under keepStyle the
				// block is a WHITE tint (normal = untouched, hover/press = brighter), so the colour
				// VRChat (or MenuThemeModule) chose is what shows at rest. Never
				// ColorBlock.defaultColorBlock here — see Tint() for why that static read throws.
				btn.transition = Selectable.Transition.ColorTint;
				btn.colors = keepStyle ? Tint(Color.white) : Tint(lit ? On : Bg);

				try { btn.onClick.RemoveAllListeners(); } catch { }
				if (onClick != null) UiClick.AddClick(btn, onClick);
				UiClick.AddClick(btn, PlayClick);
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[MenuCard] '{label}' click failed: {e.Message}"); }

			try { CopySprite(donor, card, "Background"); CopySprite(donor, card, "Icons/Icon"); } catch { }

			// OPAQUE BASE. A clone can come back with a transparent Background (or none at all, leaving
			// only the root Image which Setup made 0,0,0,0) -- the tile was see-through while VRChat's
			// own cards stayed solid, and the glassy theme had nothing to tint. Force the base opaque:
			// the Background child if there is one, else the card root. The theme keeps this alpha and
			// only tints the colour, so themed cards stay solid too.
			try
			{
				var baseBg = card.Find("Background")?.GetComponent<Image>();
				if (baseBg != null)
				{
					var c = baseBg.color;
					if (c.r + c.g + c.b < 0.05f) c = new Color(0.12f, 0.12f, 0.12f, 1f);   // colourless copy -> a neutral black card
					baseBg.color = new Color(c.r, c.g, c.b, 1f);
				}
				else
				{
					var root = card.GetComponent<Image>();
					if (root != null) { var c = root.color; if (c.a < 0.9f || c.r + c.g + c.b < 0.05f) root.color = new Color(0.12f, 0.12f, 0.12f, 1f); }
				}
			}
			catch { }

			// A neutral violet aura to begin with. A tile that HAS a toggle state gets recoloured
			// pink/blue a moment later by RefreshToggles; one that is a plain action keeps the
			// neutral colour, because blue would claim it is "off" when it has no off.
			SetAura(card, Glow);
			if (keepStyle) { /* the game owns Background */ }
			else SetLit(card, lit, keepStyle);

			// Label last. Only forced white when WE own the colours — under keepStyle the game's own
			// styling picks the text colour along with everything else, and overriding it is how a
			// card ends up not quite matching its neighbours.
			try
			{
				var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (tmp != null)
				{
					// THE COLOUR GOES IN THE TEXT, NOT ON THE COMPONENT.
					//
					// Under keepStyle the game owns tmp.color, and MenuThemeModule was repainting it
					// from a scan a few times a second — a repair loop against VRChat, which re-colours
					// its own labels on selection, hover and every theme refresh. Whichever wrote last
					// won, so some tiles came out themed and their neighbours did not, and no scan rate
					// fixes that: it is a race, not a delay.
					//
					// A TMP rich-text tag is part of the STRING, applied per character at layout time.
					// It cannot be overwritten by anyone assigning tmp.color, so the label is simply
					// the right colour, once, for as long as the text stands — and the scan has
					// nothing left to repair.
					tmp.richText = true;
					tmp.text = keepStyle ? Tinted(label) : label;
					if (!keepStyle) tmp.color = Color.white;

					// Our labels are longer than the one-word ones VRChat puts on these cards, and the
					// text box is sized for those: anything longer wrapped to a second line that fell
					// straight out of the bottom of the tile. Shrink to fit instead of wrapping, with
					// an ellipsis as the last resort, so a long label can never escape its card.
					try
					{
						float baseSize = tmp.fontSize;
						tmp.enableWordWrapping = false;
						tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
						tmp.enableAutoSizing = true;
						tmp.fontSizeMax = baseSize > 1f ? baseSize : 24f;
						tmp.fontSizeMin = Mathf.Max(8f, tmp.fontSizeMax * 0.55f);

						// VRChat's own labels are one short word and sit low in the card by design;
						// with ours the icon floats in the middle and the text hugs the bottom edge,
						// which reads as unbalanced rather than as a label.
						//
						// Moving the text was not enough on its own: the card's root carries a
						// VerticalLayoutGroup, and a layout group RECOMPUTES its children's positions
						// on the next pass — so an anchoredPosition written here was simply undone.
						// The text's own parent is therefore taken OUT of the layout first
						// (LayoutElement.ignoreLayout), after which it stays where it is put.
						// Centred in its own band, now that the band is exactly the label's height.
						tmp.alignment = TMPro.TextAlignmentOptions.Center;
						_labelHolder = LabelRect(card, tmp);
					}
					catch { }
				}
			}
			catch { }

			LayoutCard(card, _labelHolder);
			_labelHolder = null;
		}

		private static RectTransform _labelHolder;

		// LAY OUT ICON AND LABEL TOGETHER — one calculation, because two cannot disagree.
		//
		// The card root carries a VerticalLayoutGroup over Icons and TextLayoutParent. Pinning only
		// the text left Icons alone in that layout, so the group re-centred it over the whole card
		// and it landed on the label. Pinning them separately then had the two placed by different
		// formulas that had to agree by luck. So both leave the layout, and the label is positioned
		// FROM the icon's box rather than from its own fraction of the card — they can no longer
		// overlap whatever the card or the artwork measures.
		//
		// The block (icon + gap + label) is centred vertically, which is what VRChat's own cards do:
		// on a 241x164 tile its 72x72 icon spans 27..99 and its text 109..142.
		// THE RECT THAT CARRIES THE LABEL — AND NEVER THE CARD ITSELF.
		//
		// Most VRChat cards wrap their text in a TextLayoutParent, so "the text's parent" is a small
		// band inside the button and everything below is written to that band. Two cards on the
		// per-user page do NOT: Button_FavoriteFriend and Button_Boop hang Text_H4 straight off the
		// button root. For those, "the text's parent" IS THE CARD, and LayoutCard then does to the
		// whole card what it means to do to a text band:
		//     ignoreLayout = true     -> the card leaves the GridLayoutGroup and stops taking a cell
		//     anchors (0,1)-(1,1)     -> stretched to the container's full 920, i.e. FOUR columns
		//     sizeDelta.y = textH     -> and textH was read from the card, so 184: one row tall
		//     every child stretched   -> the 81x81 icon fills the whole plate
		// which is exactly the giant translucent plate lying across two rows of the user page, with
		// the five other cards stacked invisibly underneath it.
		//
		// It only started happening when the donor picker was fixed to choose a fully-enabled button:
		// the old fallback, Button_FriendRequest, HAS a TextLayoutParent, so the trap stayed shut.
		// Instance ids rather than ==, because these are il2cpp proxies.
		private static RectTransform LabelRect(Transform card, TMPro.TMP_Text tmp)
		{
			try
			{
				if (tmp == null || card == null) return null;
				var holder = tmp.transform.parent?.TryCast<RectTransform>();
				if (holder == null || holder.GetInstanceID() == card.GetInstanceID()) return tmp.rectTransform;
				return holder;
			}
			catch { return null; }
		}

		private static void LayoutCard(Transform card, RectTransform label)
		{
			try
			{
				var cardRt = card?.TryCast<RectTransform>();
				var icons = card.Find("Icons")?.TryCast<RectTransform>();
				if (cardRt == null || icons == null) return;
				// Belt and braces for any future caller: laying the card out as its own label is the
				// bug above, and it is cheaper to refuse than to explain.
				if (label != null && label.GetInstanceID() == cardRt.GetInstanceID()) label = null;

				// The card's own rect is still zero on the frame it is built, which silently skipped
				// this whole pass. The grid that owns the card knows the cell size before any layout
				// runs, so ask it first.
				float w = cardRt.rect.width, h = cardRt.rect.height;
				try
				{
					var grid = cardRt.parent != null ? cardRt.parent.GetComponent<UnityEngine.UI.GridLayoutGroup>() : null;
					if (grid != null && grid.cellSize.y > 1f) { w = grid.cellSize.x; h = grid.cellSize.y; }
				}
				catch { }
				if (h <= 1f || w <= 1f) return;

				float side = Mathf.Min(h * 0.44f, w * 0.40f);
				float gap = h * 0.05f;
				float textH = 33f;
				if (label != null && label.rect.height > 1f) textH = label.rect.height;

				// Centre the pair, then hand each half its own slot.
				float top = Mathf.Max(4f, (h - (side + gap + textH)) * 0.5f);

				Ignore(icons);
				icons.anchorMin = new Vector2(0.5f, 1f);
				icons.anchorMax = new Vector2(0.5f, 1f);
				icons.pivot = new Vector2(0.5f, 0.5f);
				icons.sizeDelta = new Vector2(side, side);
				icons.anchoredPosition = new Vector2(0f, -(top + side * 0.5f));

				// Icon sits inside Icons at a FIXED 72x72, so resizing the holder does not resize it.
				// Stretch it to fill instead, keeping the aspect: our icons are real artwork with
				// their own proportions, not VRChat's square glyphs.
				var icon = icons.Find("Icon")?.TryCast<RectTransform>();
				if (icon != null)
				{
					icon.anchorMin = Vector2.zero;
					icon.anchorMax = Vector2.one;
					icon.offsetMin = Vector2.zero;
					icon.offsetMax = Vector2.zero;
					var im = icon.GetComponent<Image>();
					if (im != null) im.preserveAspect = true;
				}

				if (label != null)
				{
					Ignore(label);
					label.anchorMin = new Vector2(0f, 1f);
					label.anchorMax = new Vector2(1f, 1f);
					label.pivot = new Vector2(0.5f, 1f);
					label.sizeDelta = new Vector2(0f, textH);   // explicit, not inherited offsets
					label.anchoredPosition = new Vector2(0f, -(top + side + gap));

					// AND THE TEXT INSIDE IT. Placing the holder is only half the job: VRChat's
					// Text_H4 is a 100-tall box anchored to its parent's CENTRE, so inside a 33-tall
					// holder it overflowed from -72 to -172 and, being top-aligned, drew its glyphs
					// at -72 — straight across the icon. Measured, not guessed: the holder was
					// correctly at -106 the whole time while the text sat 34px above it.
					// …unless the "holder" IS the text (the donor had no TextLayoutParent, so LabelRect
					// fell back to the TMP's own rect). Its children are then TMP's sub-mesh objects,
					// which TMP lays out itself and regenerates — stretching them achieves nothing and
					// fights that regeneration.
					if (label.GetComponent<TMPro.TMP_Text>() == null)
					{
						for (int i = 0; i < label.childCount; i++)
						{
							var ch = label.GetChild(i)?.TryCast<RectTransform>();
							if (ch == null) continue;
							ch.anchorMin = Vector2.zero;
							ch.anchorMax = Vector2.one;
							ch.offsetMin = Vector2.zero;
							ch.offsetMax = Vector2.zero;
							ch.pivot = new Vector2(0.5f, 0.5f);
						}
					}
				}

				// Logged ONCE per card. Two fixes in a row failed to move anything on screen, and
				// there is no way to tell "this code never ran" from "it ran and something undid it"
				// by looking at the result. This says which.
				try
				{
					if (_logged.Add(card.GetInstanceID()))
						Killiorim.Logger.LogInfo(
							$"[MenuCard] layout '{card.name}' card={w:F0}x{h:F0} icon={side:F0} @-{top + side * 0.5f:F0} "
							+ $"label@-{top + side + gap:F0} textH={textH:F0} labelNull={label == null}");
				}
				catch { }
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[MenuCard] card layout failed: {e.Message}"); }
		}

		private static readonly HashSet<int> _logged = new HashSet<int>();

		// Re-assert the geometry. VRChat rebuilds and restyles a page whenever it is shown, and a
		// one-shot placement done at build time is exactly the kind of thing that gets undone.
		public static void Relayout(Transform card)
		{
			try
			{
				if (card == null) return;
				var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
				// Same rule as Setup — recomputing the holder naively here would re-arm the
				// card-as-its-own-label bug on every page rebuild.
				LayoutCard(card, LabelRect(card, tmp));
			}
			catch { }
		}

		private static void Ignore(RectTransform rt)
		{
			try
			{
				var le = rt.GetComponent<UnityEngine.UI.LayoutElement>()
					?? rt.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
				le.ignoreLayout = true;
			}
			catch { }
		}

		// The rounded rim sprite, shared by every card that wants an inner glow — ours and the ones
		// MenuThemeModule paints across the rest of the menu. It lives here rather than in the theme
		// module so there is exactly one generator: two copies would drift, and the corner maths is
		// the part that is easy to get subtly wrong.
		private static Sprite _rimSprite;
		private const string RimChild = "VA_InnerGlow";

		public static Sprite RimSprite()
		{
			if (_rimSprite != null) return _rimSprite;
			try
			{
				const int N = 64, B = 22;      // B = border kept unstretched by the 9-slice
				var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.filterMode = FilterMode.Bilinear;

				var px = new Color[N * N];
				for (int y = 0; y < N; y++)
				{
					for (int x = 0; x < N; x++)
					{
						// Distance to the nearest edge of a ROUNDED rectangle, corner radius = B.
						// Measuring to the nearest straight edge is what put a square rim around
						// VRChat's rounded cards — the fill was curved, the light around it was not,
						// and the tiles came out boxy and harsh.
						float dx = Mathf.Min(x, N - 1 - x);
						float dy = Mathf.Min(y, N - 1 - y);

						float d;
						if (dx < B && dy < B)
						{
							// In a corner: measure from the arc's centre so the rim curves with it,
							// and drop anything beyond the radius so the corner is actually cut.
							float ox = B - dx, oy = B - dy;
							float r = Mathf.Sqrt(ox * ox + oy * oy);
							if (r > B) { px[y * N + x] = new Color(1f, 1f, 1f, 0f); continue; }
							d = B - r;
						}
						else
						{
							d = Mathf.Min(dx, dy);
						}

						float t = Mathf.Clamp01(d / (float)B);
						float alpha = 1f - t;
						alpha = alpha * alpha * alpha;        // tight to the rim, gone by the centre
						px[y * N + x] = new Color(1f, 1f, 1f, alpha);
					}
				}
				tex.SetPixels(px);
				tex.Apply(false, false);

				_rimSprite = Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f),
					100f, 0, SpriteMeshType.FullRect, new Vector4(B, B, B, B));
				if (_rimSprite != null) _rimSprite.hideFlags = HideFlags.HideAndDontSave;
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[MenuCard] rim sprite failed: {e.Message}"); }
			return _rimSprite;
		}


		// Gives the card an inner rim in `c`. Same child name the theme module uses, so a card can
		// never end up wearing two rims.
		public static void SetRim(Transform card, Color c)
		{
			try
			{
				var bg = card.Find("Background");
				if (bg == null) return;

				var existing = bg.Find(RimChild);
				if (existing != null)
				{
					var ex = existing.GetComponent<Image>();
					if (ex != null) ex.color = c;
					existing.gameObject.SetActive(true);
					return;
				}

				var sp = RimSprite();
				if (sp == null) return;

				var go = new GameObject(RimChild);
				var rt = go.AddComponent<RectTransform>();
				rt.SetParent(bg, false);
				rt.anchorMin = Vector2.zero;
				rt.anchorMax = Vector2.one;
				rt.offsetMin = Vector2.zero;
				rt.offsetMax = Vector2.zero;

				var img = go.AddComponent<Image>();
				img.sprite = sp;
				img.type = Image.Type.Sliced;
				img.color = c;
				img.raycastTarget = false;
			}
			catch { }
		}

		// Creates the aura if it is missing and gives it a colour. Everything that wants to say
		// something with the glow goes through here.
		public static void SetAura(Transform card, Color c)
		{
			try
			{
				// Two layers, one colour: the soft bloom OUTSIDE the tile, and the rim INSIDE its
				// edge. Only the bloom was being lit, which is why our tiles had no border while
				// every other card in the menu did.
				var glow = card.Find(GlowName) ?? MakeGlow(card);
				if (glow != null)
				{
					var gi = glow.GetComponent<Image>();
					if (gi != null) gi.color = c;
					glow.gameObject.SetActive(true);
				}

				// Brighter and more opaque than the bloom: a rim has to read as an edge, not a haze.
				SetRim(card, new Color(
					Mathf.Clamp01(c.r * 1.15f + 0.10f),
					Mathf.Clamp01(c.g * 1.15f + 0.06f),
					Mathf.Clamp01(c.b * 1.15f + 0.10f),
					Mathf.Clamp01(c.a + 0.18f)));
			}
			catch { }
		}

		// Shows a card's ON state through the colour of its AURA: pink for on, blue for off.
		//
		// VRChat's own way — swapping Icon_On / Icon_Off and enabling a Foreground overlay — was
		// tried first and rejected: it makes the icon visibly jump when a tile is pressed. Both are
		// now left untouched, so pressing a toggle changes the light around the tile and nothing on
		// the tile itself moves.
		// The label colour, as a rich-text tag. Matches MenuThemeModule's TextViolet so a themed card
		// and a repainted one cannot disagree.
		private const string LabelHex = "#D3A4FF";

		private static string Tinted(string label)
		{
			if (string.IsNullOrEmpty(label)) return label;
			// Never wrap twice — Setup runs again on every refresh.
			if (label.StartsWith("<color=", StringComparison.OrdinalIgnoreCase)) return label;
			return "<color=" + LabelHex + ">" + label + "</color>";
		}

		public static void SetLit(Transform card, bool lit, bool keepStyle = false)
		{
			try
			{
				if (lit)
				{
					SetAura(card, GlowOn);
				}
				else
				{
					var glow = card.Find(GlowName);
					if (glow != null) glow.gameObject.SetActive(false);
					var rim = card.Find("Background")?.Find(RimChild);
					if (rim != null) rim.gameObject.SetActive(false);
				}

				// Under keepStyle the game owns the Background colour. OFF is the untouched theme;
				// only ON adds our highlight, so an inactive switch cannot still look lit.
				if (keepStyle) return;

				var btn = card.GetComponent<Button>();
				if (btn != null) btn.colors = Tint(lit ? On : Bg);
				var bg = card.Find("Background")?.GetComponent<Image>();
				if (bg != null) bg.color = lit ? On : Bg;
				var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (tmp != null) tmp.color = Color.white;
			}
			catch { }
		}

		private const string GlowName = "VA_Glow";
		private static Sprite _glowSprite;

		// A soft radial falloff, built in code so it owes nothing to VRChat's assets. Alpha fades
		// from the centre out on a smoothstep curve, which is what makes it read as light rather
		// than as a coloured rectangle. Generated once and shared by every card.
		private static Sprite GlowSprite()
		{
			if (_glowSprite != null) return _glowSprite;
			try
			{
				const int N = 96;
				var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.filterMode = FilterMode.Bilinear;

				float c = (N - 1) * 0.5f;
				var px = new Color[N * N];
				for (int y = 0; y < N; y++)
				{
					for (int x = 0; x < N; x++)
					{
						float dx = (x - c) / c, dy = (y - c) / c;
						float d = Mathf.Sqrt(dx * dx + dy * dy);
						// 0 in the middle, 1 at the rim; squared so the core stays bright and the
						// edge trails off instead of ending on a visible ring.
						float a = Mathf.Clamp01(1f - d);
						a = a * a;
						px[y * N + x] = new Color(1f, 1f, 1f, a);
					}
				}
				tex.SetPixels(px);
				tex.Apply(false, false);

				_glowSprite = Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f));
				if (_glowSprite != null) _glowSprite.hideFlags = HideFlags.HideAndDontSave;
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[MenuCard] glow sprite failed: {e.Message}"); }
			return _glowSprite;
		}

		// The glow child: stretched past the card on every side and pushed to the BACK of the
		// sibling order so it spills around the tile instead of covering it.
		private static Transform MakeGlow(Transform card)
		{
			try
			{
				var sp = GlowSprite();
				if (sp == null) return null;

				var go = new GameObject(GlowName);
				var rt = go.AddComponent<RectTransform>();
				rt.SetParent(card, false);
				rt.anchorMin = Vector2.zero;
				rt.anchorMax = Vector2.one;
				rt.offsetMin = new Vector2(-26f, -26f);
				rt.offsetMax = new Vector2(26f, 26f);

				var img = go.AddComponent<Image>();
				img.sprite = sp;
				img.color = Glow;
				img.raycastTarget = false;      // never steal the card's own clicks

				rt.SetAsFirstSibling();
				return rt;
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogWarning($"[MenuCard] glow failed: {e.Message}");
				return null;
			}
		}

		// Puts one of OUR textures on a card's icon slot.
		//
		// The icon lives at Icons/Icon on every card VRChat ships (confirmed in the capture), and it
		// carries a StyleElement that reassigns the game's own sprite whenever the page restyles —
		// so that component has to go, or our icon flickers back to VRChat's on every repaint.
		private static readonly Dictionary<int, Sprite> IconCache = new Dictionary<int, Sprite>();

		public static void SetIcon(Transform card, Texture2D tex)
		{
			try
			{
				if (card == null || tex == null) return;
				var iconT = card.Find("Icons/Icon");
				if (iconT == null) return;
				var img = iconT.GetComponent<Image>();
				if (img == null) return;

				foreach (var c in iconT.GetComponents<Component>())
				{
					if (c == null || Il2CppName(c) != "StyleElement") continue;
					UnityEngine.Object.Destroy(c);
					break;
				}

				// One Sprite per texture, shared by every tile that asks for it.
				int key = tex.GetInstanceID();
				if (!IconCache.TryGetValue(key, out var sp) || sp == null)
				{
					sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
					if (sp != null) sp.hideFlags = HideFlags.HideAndDontSave;
					IconCache[key] = sp;
				}
				if (sp == null) return;

				img.sprite = sp;
				img.color = Color.white;
				img.type = Image.Type.Simple;
				img.preserveAspect = true;
				if (!iconT.gameObject.activeSelf) iconT.gameObject.SetActive(true);
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[MenuCard] SetIcon failed: {e.Message}"); }
		}

		public static void StripRoot(Transform t, bool keepStyle = false)
		{
			var comps = t.GetComponents<Component>();
			if (comps == null) return;
			foreach (var c in comps)
			{
				if (c == null) continue;
				string n = Il2CppName(c);
				if (Keep.Contains(n)) continue;
				// StyleElement is VRChat's theming hook and is NOT part of the donor's action, so
				// keeping it costs us nothing and buys the card the game's own look.
				if (keepStyle && n == "StyleElement") continue;
				try { UnityEngine.Object.DestroyImmediate(c); } catch { }
			}
		}

		// VRChat assigns some card sprites through StyleElement at runtime. A clone built before
		// the donor's page was ever shown gets nulls, and an Image with no sprite draws a flat
		// rectangle — which is what produced blank teal squares instead of icons.
		// A VRChat sprite, straight onto the tile. Used for the tiles that borrow one of the game's
		// own icons: the same art the rest of the menu uses is what makes a tile look native.
		public static void SetIcon(Transform card, Sprite sp)
		{
			try
			{
				if (card == null || sp == null) return;
				var iconT = card.Find("Icons/Icon");
				if (iconT == null) return;
				var img = iconT.GetComponent<Image>();
				if (img == null) return;
				foreach (var c in iconT.GetComponents<Component>())
				{
					if (c == null || Il2CppName(c) != "StyleElement") continue;
					UnityEngine.Object.Destroy(c);
					break;
				}
				img.sprite = sp;
				img.type = Image.Type.Simple;
				img.preserveAspect = true;
				img.color = IconTint;
				if (!iconT.gameObject.activeSelf) iconT.gameObject.SetActive(true);
			}
			catch (Exception e) { Killiorim.Logger.LogWarning($"[MenuCard] SetIcon(sprite) failed: {e.Message}"); }
		}

		// ICON COLOUR. The Launchpad icons the tiles are cloned from are tinted teal by VRChat's
		// StyleElement -- teal glyphs on violet glass under violet labels is the "colours not
		// integrated" look. Our tiles drop that StyleElement on the icon and take a near-white
		// lavender that sits with the label, whatever sprite the icon ended up with.
		public static readonly Color IconTint = new Color(0.94f, 0.90f, 1f, 1f);

		public static void TintIcon(Transform card)
		{
			try
			{
				var iconT = card?.Find("Icons/Icon");
				var img = iconT?.GetComponent<Image>();
				if (img == null) return;
				foreach (var c in iconT.GetComponents<Component>())
				{
					if (c == null || Il2CppName(c) != "StyleElement") continue;
					UnityEngine.Object.Destroy(c);
					break;
				}
				img.color = IconTint;
			}
			catch { }
		}

		// BADGES. A Launchpad card can carry a "NEW" pill; cloning the card clones the pill, so
		// Radar and Player list were announcing themselves as new VRChat features. Hidden, not
		// destroyed: the donor's StyleElement may still hold a reference to it.
		public static void StripBadges(Transform card)
		{
			try
			{
				if (card == null) return;
				for (int i = 0; i < card.childCount; i++)
				{
					var ch = card.GetChild(i);
					string n = ch != null ? (ch.name ?? "") : "";
					if (n.IndexOf("badge", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(n, "New", StringComparison.OrdinalIgnoreCase))
						ch.gameObject.SetActive(false);
					for (int j = 0; j < ch.childCount; j++)
					{
						var g = ch.GetChild(j);
						string gn = g != null ? (g.name ?? "") : "";
						if (gn.IndexOf("badge", StringComparison.OrdinalIgnoreCase) >= 0) g.gameObject.SetActive(false);
					}
				}
			}
			catch { }
		}

		public static bool CopySprite(Transform donor, Transform card, string path)
		{
			try
			{
				var dstT = card.Find(path);
				if (dstT == null) return true;
				var dst = dstT.GetComponent<Image>();
				if (dst == null) return true;
				if (dst.sprite != null) { if (!dstT.gameObject.activeSelf) dstT.gameObject.SetActive(true); return true; }

				var srcT = donor != null ? donor.Find(path) : null;
				var src = srcT != null ? srcT.GetComponent<Image>() : null;
				if (src == null || src.sprite == null)
				{
					// Hidden rather than left as a solid rectangle; a later repair pass can show it.
					if (path.EndsWith("Icon", StringComparison.Ordinal)) dstT.gameObject.SetActive(false);
					return false;
				}

				dst.sprite = src.sprite;
				if (src.material != null) dst.material = src.material;
				dst.type = src.type;
				if (!dstT.gameObject.activeSelf) dstT.gameObject.SetActive(true);
				return true;
			}
			catch { return true; }
		}

		public static ColorBlock Tint(Color b)
		{
			// NOT ColorBlock.defaultColorBlock: that is a static field read, and static field reads
			// throw on this VRChat build (Il2CppInterop looks for the offset where the metadata token
			// lives). It cost nothing to lose -- every one of its members is overwritten below, so the
			// defaults were never used, they just took the favourites button down with them.
			var cb = default(ColorBlock);
			cb.normalColor = b;
			cb.highlightedColor = new Color(b.r * 1.55f, b.g * 1.45f, b.b * 1.40f, 1f);
			cb.pressedColor = new Color(b.r * 2.0f, b.g * 1.7f, b.b * 1.6f, 1f);
			cb.selectedColor = cb.highlightedColor;
			cb.disabledColor = new Color(b.r, b.g, b.b, 0.4f);
			cb.colorMultiplier = 1f;
			cb.fadeDuration = 0.08f;
			return cb;
		}

		// VRChat's own menu click, taken from what the game actually plays rather than guessed.
		private static AudioSource _src;
		private static AudioClip _clip;
		private static bool _resolved;

		public static void PlayClick()
		{
			try
			{
				if (!_resolved)
				{
					_resolved = true;
					foreach (var a in Resources.FindObjectsOfTypeAll<AudioSource>())
					{
						if (a == null) continue;
						try { if (a.name != "SoundPlayer" || !a.gameObject.scene.IsValid()) continue; } catch { continue; }
						_src = a;
						if (a.clip != null) _clip = a.clip;
						break;
					}
					if (_clip == null)
					{
						foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
						{
							if (c == null) continue;
							string n;
							try { n = c.name ?? ""; } catch { continue; }
							if (n.IndexOf("click", StringComparison.OrdinalIgnoreCase) < 0) continue;
							if (n.IndexOf("hover", StringComparison.OrdinalIgnoreCase) >= 0) continue;
							_clip = c; break;
						}
					}
				}
				if (_src != null && _clip != null) _src.PlayOneShot(_clip);
			}
			catch { }
		}

		// The real IL2CPP class name of a component. Public so other modules can identify VRChat's
		// obfuscated components by their true name instead of duplicating the interop dance.
		public static string Il2CppNameOf(Il2CppObjectBase o) => Il2CppName(o);

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
