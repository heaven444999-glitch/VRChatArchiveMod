using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// A MENU THAT IS DECLARED ONCE AND DRAWN EVERY FRAME.
	//
	// The uGUI menu had to CREATE objects to show a control: a GameObject per button, a
	// RectTransform, a layout group, and a Canvas that rebuilds its batch every time any of it
	// changes. Adding a row was not "one more row", it was a scene mutation plus a layout pass —
	// which is what the lag was.
	//
	// This is the other model, the one Luna uses: immediate mode. Nothing exists between frames.
	// A page is a LIST OF DECLARATIONS, built once at startup, and drawing it is a loop that reads
	// each declaration and paints it. Adding a control costs one list entry, and showing it costs
	// one draw call — never an object, never a rebuild.
	//
	// The second half of the idea, and the part that actually removes the bugs: a widget is BOUND
	// TO ITS SETTING rather than mirroring it. GuiKit.Toggle already takes a value and returns the
	// new one, so a bound toggle reads the ConfigEntry, draws it, and writes back — there is no
	// second copy of the state to keep in sync, and therefore nothing that can drift.
	//
	// Unity's IMGUI is used rather than Dear ImGui because it is already here: no native DLL, no
	// D3D11 hook, no second render path, and GuiKit already draws in this mod's style.
	public static class Overlay
	{
		// ---------------------------------------------------------------- declaration

		public enum Kind { Toggle, Slider, IntSlider, Action, Label }

		public sealed class Widget
		{
			public Kind Kind;
			public string Label;
			public ConfigEntry<bool> Flag;
			public ConfigEntry<float> Number;
			public ConfigEntry<int> Integer;
			public float Min, Max;
			public string Format = "F1";
			// STEP AND ONCHANGE, both taken from what Luna's BindSlider carries.
			//
			// Step is what makes a slider usable with a mouse: a free float gives 3.7418266 when the
			// setting only ever wanted 3.75, and the label then shows a number nobody chose. Snapping
			// also means dragging back to a round value is possible at all.
			//
			// OnChange fires only when the value really moved, and exists for the settings whose
			// effect is not read every frame — something that has to be re-applied when the number
			// changes rather than polled. Without it those need a watcher somewhere else.
			public float Step;
			public Action<float> OnChange;
			public Action Run;
			public Func<string> Text;         // for Label: evaluated at draw time
			public Func<bool> Enabled;        // optional: grey out / skip when false
		}

		public sealed class Foldout
		{
			public string Title;
			public bool Open = true;
			public readonly List<Widget> Widgets = new List<Widget>();

			public Foldout Toggle(string label, ConfigEntry<bool> flag)
			{
				Widgets.Add(new Widget { Kind = Kind.Toggle, Label = label, Flag = flag });
				return this;
			}

			// step 0 = free. A sensible default is one 200th of the range, which is roughly one pixel
			// of travel on a slider this wide — fine enough to feel continuous, coarse enough that
			// the number stays readable.
			public Foldout Slider(string label, ConfigEntry<float> number, float min, float max,
				string format = "F1", float step = 0f, Action<float> onChange = null)
			{
				Widgets.Add(new Widget
				{
					Kind = Kind.Slider, Label = label, Number = number,
					Min = min, Max = max, Format = format, Step = step, OnChange = onChange,
				});
				return this;
			}

			public Foldout Slider(string label, ConfigEntry<int> integer, int min, int max)
			{
				Widgets.Add(new Widget { Kind = Kind.IntSlider, Label = label, Integer = integer, Min = min, Max = max, Format = "F0" });
				return this;
			}

			public Foldout Action(string label, Action run)
			{
				Widgets.Add(new Widget { Kind = Kind.Action, Label = label, Run = run });
				return this;
			}

			public Foldout Label(Func<string> text)
			{
				Widgets.Add(new Widget { Kind = Kind.Label, Text = text });
				return this;
			}

			// Applies to every widget added after it — how a whole group is greyed out behind its
			// own master switch without repeating the condition on each line.
			public Foldout OnlyWhen(Func<bool> when)
			{
				for (int i = 0; i < Widgets.Count; i++)
					if (Widgets[i].Enabled == null) Widgets[i].Enabled = when;
				return this;
			}
		}

		public sealed class Page
		{
			public string Title;
			public readonly List<Foldout> Foldouts = new List<Foldout>();
			public Vector2 Scroll;

			public Foldout Group(string title)
			{
				var f = new Foldout { Title = title };
				Foldouts.Add(f);
				return f;
			}
		}

		private static readonly List<Page> Pages = new List<Page>();
		private static int _page;

		public static Page AddPage(string title)
		{
			var p = new Page { Title = title };
			Pages.Add(p);
			return p;
		}

		public static void Clear() { Pages.Clear(); _page = 0; }
		public static int PageCount => Pages.Count;

		// ---------------------------------------------------------------- state

		public static bool Visible;
		public static Rect Window = new Rect(90f, 90f, 560f, 620f);

		private const float Pad = 14f;
		private const float RowH = 30f;
		private const float RowGap = 6f;
		private const float TabH = 30f;
		private const float HeadH = 34f;

		// ---------------------------------------------------------------- draw

		public static void Draw()
		{
			if (!Visible || Pages.Count == 0) return;

			// Everything below reads Event.current, so it must not run before IMGUI has one.
			if (Event.current == null) return;

			GuiKit.RoundedBorder(Window, new Color(0.03f, 0.04f, 0.06f, 0.96f),
				GuiKit.Accent, 10f, 1.5f);
			GuiKit.SoftGlow(Window, GuiKit.Accent, 10f, 0.16f);

			float x = Window.x + Pad;
			float w = Window.width - Pad * 2f;
			float y = Window.y + Pad;

			GUI.Label(new Rect(x, y, w, HeadH), "VRCHAT ARCHIVE", GuiKit.BoxLabel);
			y += HeadH;

			// tabs — one segmented control, not N buttons: exactly one page is open
			if (Pages.Count > 1)
			{
				string[] titles = TabTitles();
				_page = Mathf.Clamp(GuiKit.Segmented(new Rect(x, y, w, TabH), titles, _page), 0, Pages.Count - 1);
				y += TabH + RowGap;
			}

			Page page = Pages[_page];
			float bodyY = y;
			float bodyH = Window.yMax - Pad - bodyY;

			float contentH = MeasureHeight(page);
			var view = new Rect(x, bodyY, w, bodyH);
			var content = new Rect(0f, 0f, w - 18f, contentH);

			page.Scroll = GUI.BeginScrollView(view, page.Scroll, content);
			DrawPage(page, w - 18f);
			GUI.EndScrollView();
		}

		// Rebuilt only when the page changes shape, which is what a collapsed foldout does.
		private static float MeasureHeight(Page page)
		{
			float h = 0f;
			for (int i = 0; i < page.Foldouts.Count; i++)
			{
				Foldout f = page.Foldouts[i];
				h += RowH + RowGap;                                   // the foldout header
				if (!f.Open) continue;
				for (int j = 0; j < f.Widgets.Count; j++)
				{
					Widget wd = f.Widgets[j];
					if (wd.Enabled != null && !Safe(wd.Enabled)) continue;
					h += (wd.Kind == Kind.Slider || wd.Kind == Kind.IntSlider) ? RowH + 18f + RowGap : RowH + RowGap;
				}
				h += RowGap;
			}
			return h;
		}

		private static void DrawPage(Page page, float w)
		{
			float y = 0f;
			for (int i = 0; i < page.Foldouts.Count; i++)
			{
				Foldout f = page.Foldouts[i];

				string arrow = f.Open ? "▾" : "▸";
				if (GuiKit.Button(new Rect(0f, y, w, RowH), arrow + "  " + f.Title)) f.Open = !f.Open;
				y += RowH + RowGap;

				if (!f.Open) continue;

				for (int j = 0; j < f.Widgets.Count; j++)
				{
					Widget wd = f.Widgets[j];
					if (wd.Enabled != null && !Safe(wd.Enabled)) continue;
					y = DrawWidget(wd, 0f, y, w);
				}
				y += RowGap;
			}
		}

		private static float DrawWidget(Widget wd, float x, float y, float w)
		{
			switch (wd.Kind)
			{
				case Kind.Toggle:
				{
					if (wd.Flag == null) break;
					bool now = GuiKit.Toggle(new Rect(x, y, w, RowH), wd.Label, wd.Flag.Value);
					// WRITTEN ONLY ON CHANGE. BepInEx persists a ConfigEntry when it is assigned, so
					// an unconditional write here would save the config file every single frame.
					if (now != wd.Flag.Value) wd.Flag.Value = now;
					return y + RowH + RowGap;
				}

				case Kind.Slider:
				{
					if (wd.Number == null) break;
					float now = GuiKit.Slider(new Rect(x, y, w, 18f), new Rect(x, y + 18f, w, RowH),
						wd.Label, wd.Number.Value, wd.Min, wd.Max, wd.Format);
					now = Snap(now, wd);
					if (!Mathf.Approximately(now, wd.Number.Value))
					{
						wd.Number.Value = now;
						if (wd.OnChange != null)
						{
							// Never let a callback take the menu down: an exception inside the IMGUI
							// pass abandons the whole frame and the overlay simply vanishes.
							try { wd.OnChange(now); }
							catch (Exception e)
							{
								VRChatArchiveModPlugin.Logger.LogWarning(
									"[Overlay] '" + wd.Label + "' change handler failed: " + e.Message);
							}
						}
					}
					return y + RowH + 18f + RowGap;
				}

				case Kind.IntSlider:
				{
					if (wd.Integer == null) break;
					float now = GuiKit.Slider(new Rect(x, y, w, 18f), new Rect(x, y + 18f, w, RowH),
						wd.Label, wd.Integer.Value, wd.Min, wd.Max, "F0");
					int rounded = Mathf.RoundToInt(now);
					if (rounded != wd.Integer.Value) wd.Integer.Value = rounded;
					return y + RowH + 18f + RowGap;
				}

				case Kind.Action:
				{
					if (GuiKit.Button(new Rect(x, y, w, RowH), wd.Label) && wd.Run != null)
					{
						// A click must never take the menu down with it: an exception here would
						// abandon the whole IMGUI pass and the overlay would simply vanish.
						try { wd.Run(); }
						catch (Exception e)
						{
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[Overlay] '" + wd.Label + "' failed: " + e.Message);
						}
					}
					return y + RowH + RowGap;
				}

				case Kind.Label:
				{
					string text = wd.Text == null ? "" : SafeText(wd.Text);
					GUI.Label(new Rect(x, y, w, RowH), text, GuiKit.BoxLabel);
					return y + RowH + RowGap;
				}
			}
			return y + RowH + RowGap;
		}

		// ---------------------------------------------------------------- plumbing

		// Tab titles are cached: this runs every frame, and rebuilding a string[] per frame is the
		// kind of allocation that makes an overlay cost more than the menu it replaced.
		private static string[] _tabs;
		private static int _tabsFor = -1;

		private static string[] TabTitles()
		{
			if (_tabs != null && _tabsFor == Pages.Count) return _tabs;
			_tabs = new string[Pages.Count];
			for (int i = 0; i < Pages.Count; i++) _tabs[i] = Pages[i].Title;
			_tabsFor = Pages.Count;
			return _tabs;
		}

		// Rounds to the widget's step, and clamps — a drag past the end of the track otherwise writes
		// a value outside the range the setting was declared with.
		private static float Snap(float v, Widget wd)
		{
			float step = wd.Step;
			if (step <= 0f) step = Mathf.Abs(wd.Max - wd.Min) / 200f;
			if (step > 0f) v = Mathf.Round(v / step) * step;
			return Mathf.Clamp(v, Mathf.Min(wd.Min, wd.Max), Mathf.Max(wd.Min, wd.Max));
		}

		private static bool Safe(Func<bool> f)
		{
			try { return f(); }
			catch { return false; }
		}

		private static string SafeText(Func<string> f)
		{
			try { return f() ?? ""; }
			catch { return ""; }
		}
	}
}
