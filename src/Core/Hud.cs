using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// ONE chrome for every HUD panel.
	//
	// The four corner panels (players, instance log, events, radar) each grew their own look —
	// a green rule here, a teal frame there, different paddings and header sizes. Put together on
	// screen they read as four unrelated tools. Everything visual now goes through this file, so
	// changing the client's identity is a one-place edit and the panels can never drift apart
	// again.
	//
	// Palette: the client's pink -> violet, on a near-black navy, with a soft outer glow.
	public static class Hud
	{
		public static readonly Color Pink   = new Color(1.000f, 0.416f, 0.835f);   // #FF6AD5
		public static readonly Color Violet = new Color(0.506f, 0.263f, 0.902f);   // #8143E6
		public static readonly Color Body   = new Color(0.043f, 0.043f, 0.078f, 0.90f);
		public static readonly Color Head   = new Color(0.086f, 0.075f, 0.137f, 0.95f);
		public static readonly Color Text   = new Color(0.925f, 0.937f, 0.968f);
		public static readonly Color Dim    = new Color(0.478f, 0.529f, 0.612f);
		public static readonly Color Row    = new Color(1f, 1f, 1f, 0.030f);

		// EVERY size in the HUD is expressed in "design pixels" for a 1080p screen and multiplied
		// by this. Fixed pixel sizes looked fine on the machine they were written on and turned
		// into a postage stamp on a 1440p/4K display — which is exactly what happened to the radar.
		// One place to scale the whole HUD, and it follows the player's resolution automatically.
		public static float Scale
		{
			get
			{
				// Resolution only. There is deliberately no user scale: one interface, and the
				// automatic factor is what keeps it identical in proportion on every screen.
				return Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
			}
		}

		public static float S(float designPixels) => designPixels * Scale;

		public static float Radius  => S(10f);
		public static float HeaderH => S(26f);
		public static float PadX    => S(10f);

		private static GUIStyle _title, _right;
		private static float _styleScale = -1f;

		// Fonts scale with the screen too — a header that stays 12px while the panel doubles in
		// size looks broken. Rebuilt only when the resolution actually changes.
		private static void EnsureStyles()
		{
			float sc = Scale;
			if (_title != null && Mathf.Abs(sc - _styleScale) < 0.01f) return;
			_styleScale = sc;
			_title = new GUIStyle { fontSize = Mathf.RoundToInt(12f * sc), fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleLeft };
			_right = new GUIStyle { fontSize = Mathf.RoundToInt(11f * sc), fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleRight };
		}

		// Draws the card + header and returns the rect the caller should fill with content.
		// `title` is shown left, `right` (a count, a rate) is shown right in the accent colour.
		public static Rect Panel(Rect r, string title, string right)
		{
			EnsureStyles();

			// Soft violet glow so a panel keeps its edge over a bright world without needing a
			// hard border or a text shadow on every row.
			GuiKit.SoftGlow(r, new Color(Violet.r, Violet.g, Violet.b, 0.30f), Radius, 0.35f, 5, 2.5f);

			GuiKit.RoundedFill(r, Body, Radius);
			GuiKit.RoundedFill(new Rect(r.x, r.y, r.width, HeaderH + Radius), Head, Radius);
			GuiKit.Fill(new Rect(r.x, r.y + HeaderH, r.width, 1f), new Color(1f, 1f, 1f, 0.06f));

			// A pink→violet rule across the top is the client's signature; it is what makes the
			// four panels read as one product.
			AccentBar(new Rect(r.x + Radius * 0.5f, r.y, r.width - Radius, 2f));

			// Accent dot + title
			GuiKit.RoundedFill(new Rect(r.x + PadX, r.y + HeaderH * 0.5f - 3.5f, 7f, 7f), Pink, 3.5f);
			_title.normal.textColor = Text;
			GUI.Label(new Rect(r.x + PadX + 14f, r.y, r.width - PadX * 2f - 14f, HeaderH), title, _title);

			if (!string.IsNullOrEmpty(right))
			{
				_right.normal.textColor = Pink;
				GUI.Label(new Rect(r.x + PadX, r.y, r.width - PadX * 2f, HeaderH), right, _right);
			}

			return new Rect(r.x + PadX * 0.4f, r.y + HeaderH + 3f, r.width - PadX * 0.8f, r.height - HeaderH - 7f);
		}

		// Horizontal pink -> violet gradient, drawn as a few flat quads. Cheap enough to sit at
		// the top of every panel every frame.
		public static void AccentBar(Rect r)
		{
			const int steps = 12;
			float w = r.width / steps;
			for (int i = 0; i < steps; i++)
			{
				float k = steps == 1 ? 0f : (float)i / (steps - 1);
				GUI.color = Color.Lerp(Pink, Violet, k);
				GUI.DrawTexture(new Rect(r.x + i * w, r.y, w + 1f, r.height), GuiKit.Pixel);
			}
			GUI.color = Color.white;
		}

		// Zebra stripe for list rows, so every panel stripes identically.
		public static void Stripe(Rect r, int index)
		{
			if ((index & 1) == 0) GuiKit.Fill(r, Row);
		}
	}
}
