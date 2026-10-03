using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace Killiorim.Core
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

		public enum Kind { Toggle, Slider, IntSlider, Action, Label, Setting }

		public sealed class Widget
		{
			public Kind Kind;
			public string Label;
			public ConfigEntry<bool> Flag;
			public ConfigEntry<float> Number;
			public ConfigEntry<int> Integer;
			public ConfigEntryBase Entry;
			public string EditBuffer;
			public bool Editing;
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

			public Foldout Setting(ConfigEntryBase entry)
			{
				if (entry != null)
					Widgets.Add(new Widget { Kind = Kind.Setting, Label = ReadableName(entry.Definition.Key), Entry = entry });
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
		public static Rect Window = new Rect(48f, 48f, 1060f, 760f);
		private static string _search = "";
		private static Vector2 _navScroll;
		private static bool _dragging;
		private static Vector2 _dragOffset;
		private static GUIStyle _searchStyle, _navLabel, _pageHeading, _helper;

		private const float Pad = 20f;
		private const float RowH = 30f;
		private const float RowGap = 6f;
		private const float NavW = 198f;
		private const float HeaderH = 76f;

		// ---------------------------------------------------------------- draw

		public static void Draw()
		{
			if (!Visible || Pages.Count == 0) return;

			// Everything below reads Event.current, so it must not run before IMGUI has one.
			if (Event.current == null) return;

			Window.width = Mathf.Min(Window.width, Mathf.Max(360f, Screen.width - 32f));
			Window.height = Mathf.Min(Window.height, Mathf.Max(320f, Screen.height - 32f));
			Window.x = Mathf.Clamp(Window.x, 8f, Mathf.Max(8f, Screen.width - Window.width - 8f));
			Window.y = Mathf.Clamp(Window.y, 8f, Mathf.Max(8f, Screen.height - Window.height - 8f));
			var background = AssetLoader.Background;
			if (background != null)
			{
				GUI.DrawTexture(Window, background, ScaleMode.ScaleAndCrop, true, 0f,
					new Color(1f, 1f, 1f, 0.82f), Vector4.zero, new Vector4(12f, 12f, 12f, 12f));
				GuiKit.RoundedFill(Window, new Color(0.005f, 0.005f, 0.008f, 0.42f), 12f);
			}
			GuiKit.SoftGlow(Window, GuiKit.Accent, 12f, 0.12f, 5, 2.5f);
			GuiKit.RoundedBorder(Window, new Color(0.008f, 0.008f, 0.012f, background != null ? 0.58f : 0.975f),
				new Color(0.96f, 0.78f, 0.86f, 0.78f), 12f, 1.4f);
			GuiKit.Corners(Window, 28f, 1.5f, 3f);

			float x = Window.x + Pad;
			float y = Window.y + Pad;
			float w = Window.width - Pad * 2f;
			DrawHeader(x, y, w);
			float bodyY = y + HeaderH;
			float bodyH = Window.yMax - Pad - bodyY;
			DrawNavigation(new Rect(x, bodyY, NavW, bodyH));
			DrawActivePage(new Rect(x + NavW + 18f, bodyY, w - NavW - 18f, bodyH));
			HandleDrag(new Rect(Window.x, Window.y, Window.width, HeaderH + Pad));
		}

		private static void DrawHeader(float x, float y, float width)
		{
			EnsureStyles();
			string previousSearch = _search ?? "";
			var mark = new Rect(x, y + 3f, 42f, 42f);
			GuiKit.RoundedBorder(mark, new Color(0.025f, 0.025f, 0.03f, 0.98f),
				new Color(0.96f, 0.78f, 0.86f, 0.78f), 9f, 1f);
			GUI.Label(mark, "K", _pageHeading);
			GUI.Label(new Rect(x + 54f, y, 340f, 30f), "KILLIORIUM", _pageHeading);
			GUI.Label(new Rect(x + 55f, y + 29f, 300f, 19f), "IN-GAME CONTROL DECK", _helper);

			float searchW = Mathf.Min(330f, width * 0.36f);
			float searchX = x + width - searchW - 42f;
			GUI.Label(new Rect(searchX, y + 1f, searchW, 16f), "SEARCH ALL SETTINGS", _helper);
			_search = GUI.TextField(new Rect(searchX, y + 22f, searchW, 30f), _search ?? "", _searchStyle);
			if (!string.Equals(previousSearch, _search, StringComparison.Ordinal))
			{
				_navScroll = Vector2.zero;
				if (!string.IsNullOrWhiteSpace(_search))
					for (int i = 0; i < Pages.Count; i++)
						if (PageMatches(Pages[i], _search)) { _page = i; break; }
			}
			if (GuiKit.Button(new Rect(x + width - 34f, y + 22f, 34f, 30f), "×")) Visible = false;
			GuiKit.Fill(new Rect(x, y + HeaderH - 8f, width, 1f), new Color(0.96f, 0.84f, 0.89f, 0.32f));
		}

		private static void DrawNavigation(Rect rect)
		{
			GuiKit.RoundedBorder(rect, new Color(0.012f, 0.012f, 0.018f, 0.94f),
				new Color(0.94f, 0.90f, 0.92f, 0.15f), 9f, 1f);
			float pad = 10f, rowH = 34f;
			var view = new Rect(rect.x + pad, rect.y + pad, rect.width - pad * 2f, rect.height - pad * 2f);
			float contentH = Pages.Count * (rowH + 5f) + 26f;
			_navScroll = GUI.BeginScrollView(view, _navScroll, new Rect(0f, 0f, view.width - 14f, contentH));
			GUI.Label(new Rect(0f, 0f, view.width - 14f, 20f), "FEATURE AREAS", _helper);
			float y = 24f;
			for (int i = 0; i < Pages.Count; i++)
			{
				Page page = Pages[i];
				if (!string.IsNullOrWhiteSpace(_search) && !PageMatches(page, _search)) continue;
				var row = new Rect(0f, y, view.width - 14f, rowH);
				bool selected = i == _page;
				if (selected) GuiKit.Box(row, true);
				else if (row.Contains(Event.current.mousePosition)) GuiKit.RoundedFill(row, new Color(1f, 1f, 1f, 0.045f), 7f);
				if (GUI.Button(row, "", GUIStyle.none)) _page = i;
				_navLabel.normal.textColor = selected ? Color.white : new Color(0.70f, 0.75f, 0.80f);
				GUI.Label(new Rect(row.x + 11f, row.y, row.width - 20f, row.height), page.Title, _navLabel);
				y += rowH + 5f;
			}
			GUI.EndScrollView();
		}

		private static void DrawActivePage(Rect rect)
		{
			if (_page < 0 || _page >= Pages.Count) _page = 0;
			Page page = Pages[_page];
			bool searching = !string.IsNullOrWhiteSpace(_search);
			GUI.Label(new Rect(rect.x, rect.y, rect.width, 30f), page.Title, _pageHeading);
			GUI.Label(new Rect(rect.x + 1f, rect.y + 30f, rect.width, 18f),
				searching ? "MATCHING CONTROLS" : page.Foldouts.Count + " CONFIGURATION GROUPS", _helper);

			DrawInsertCard(new Rect(rect.x, rect.y + 48f, rect.width, 96f));

			float bodyY = rect.y + 150f;
			float bodyH = rect.height - 150f;
			float contentH = MeasureHeight(page);
			var view = new Rect(rect.x, bodyY, rect.width, bodyH);
			var content = new Rect(0f, 0f, rect.width - 18f, contentH);
			page.Scroll = GUI.BeginScrollView(view, page.Scroll, content);
			DrawPage(page, content.width);
			GUI.EndScrollView();
		}

		private static void DrawInsertCard(Rect rect)
		{
			if (rect.width <= 0f || rect.height <= 0f) return;
			GuiKit.SoftGlow(rect, new Color(0.94f, 0.62f, 0.90f, 1f), 16f, 0.14f, 5, 2.8f);
			GuiKit.RoundedBorder(rect, new Color(0.05f, 0.04f, 0.08f, 0.98f),
				new Color(0.97f, 0.83f, 0.90f, 0.75f), 16f, 1.5f);

			var tag = new Rect(rect.x + 18f, rect.y + 16f, 88f, 24f);
			GuiKit.RoundedFill(tag, new Color(0.98f, 0.84f, 0.92f, 0.14f), 12f);
			GUI.Label(tag, "INSERT", new GUIStyle(GuiKit.BoxLabel)
			{
				fontSize = 10,
				alignment = TextAnchor.MiddleCenter,
				normal = { textColor = new Color(0.99f, 0.94f, 0.97f, 1f) }
			});

			var status = new Rect(rect.xMax - 118f, rect.y + 16f, 100f, 24f);
			GuiKit.RoundedFill(status, new Color(0.39f, 0.97f, 0.67f, 0.18f), 10f);
			GUI.Label(status, "LIVE", new GUIStyle(GuiKit.BoxLabel)
			{
				fontSize = 10,
				alignment = TextAnchor.MiddleCenter,
				normal = { textColor = new Color(0.70f, 1f, 0.84f, 1f) }
			});

			GUI.Label(new Rect(rect.x + 18f, rect.y + 42f, rect.width * 0.52f, 26f), "KILLIORIM CONTROL PANEL", new GUIStyle(_pageHeading)
			{
				fontSize = 20,
				normal = { textColor = new Color(0.98f, 0.98f, 0.98f, 1f) }
			});
			GUI.Label(new Rect(rect.x + 18f, rect.y + 68f, rect.width * 0.6f, 18f), "custom theme • panel stream • quick access", _helper);

			var stat1 = new Rect(rect.xMax - 200f, rect.y + 42f, 70f, 42f);
			var stat2 = new Rect(rect.xMax - 120f, rect.y + 42f, 70f, 42f);
			var stat3 = new Rect(rect.xMax - 40f, rect.y + 42f, 70f, 42f);
			for (int i = 0; i < 3; i++)
			{
				Rect r = i == 0 ? stat1 : i == 1 ? stat2 : stat3;
				GuiKit.RoundedFill(r, new Color(0.12f, 0.11f, 0.18f, 0.9f), 11f);
				GuiKit.Corners(r, 10f, 1.2f, 0f);
			}
			GUI.Label(new Rect(stat1.x + 8f, stat1.y + 6f, 54f, 12f), "FPS", _helper);
			GUI.Label(new Rect(stat1.x + 8f, stat1.y + 20f, 54f, 18f), Mathf.RoundToInt(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f)).ToString(), new GUIStyle(_pageHeading) { fontSize = 14, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } });
			GUI.Label(new Rect(stat2.x + 8f, stat2.y + 6f, 54f, 12f), "PAGES", _helper);
			GUI.Label(new Rect(stat2.x + 8f, stat2.y + 20f, 54f, 18f), Pages.Count.ToString(), new GUIStyle(_pageHeading) { fontSize = 14, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } });
			GUI.Label(new Rect(stat3.x + 8f, stat3.y + 6f, 54f, 12f), "MODE", _helper);
			GUI.Label(new Rect(stat3.x + 8f, stat3.y + 20f, 54f, 18f), "ON", new GUIStyle(_pageHeading) { fontSize = 14, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.74f, 1f, 0.85f, 1f) } });
		}

		private static void HandleDrag(Rect header)
		{
			Event e = Event.current;
			if (e.type == EventType.MouseDown && e.button == 0 && header.Contains(e.mousePosition)
				&& e.mousePosition.x < Window.xMax - 390f)
			{
				_dragging = true;
				_dragOffset = new Vector2(e.mousePosition.x - Window.x, e.mousePosition.y - Window.y);
				e.Use();
			}
			else if (_dragging && e.type == EventType.MouseDrag)
			{
				Window.position = e.mousePosition - _dragOffset;
				GUI.changed = true;
				e.Use();
			}
			else if (_dragging && e.type == EventType.MouseUp) _dragging = false;
		}

		private static void EnsureStyles()
		{
			if (_searchStyle == null) _searchStyle = new GUIStyle(GUI.skin.textField)
			{
				fontSize = 12, alignment = TextAnchor.MiddleLeft,
				padding = new RectOffset(11, 8, 4, 4),
			};
			if (_navLabel == null) _navLabel = new GUIStyle(GuiKit.BoxLabel)
			{
				alignment = TextAnchor.MiddleLeft, fontSize = 11,
			};
			if (_pageHeading == null) _pageHeading = new GUIStyle(GUI.skin.label)
			{
				fontSize = 19, fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip,
			};
			if (_helper == null) _helper = new GUIStyle(GUI.skin.label)
			{
				fontSize = 9, fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip,
			};
		}

		private static bool PageMatches(Page page, string query)
		{
			if (string.IsNullOrWhiteSpace(query) || page.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
			for (int i = 0; i < page.Foldouts.Count; i++)
			{
				var group = page.Foldouts[i];
				if (group.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
				for (int j = 0; j < group.Widgets.Count; j++)
				{
					var widget = group.Widgets[j];
					if (widget.Label != null && widget.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
					if (widget.Entry != null && widget.Entry.Definition.Section.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
			}
			return false;
		}

		private static bool FoldoutMatches(Foldout foldout, string query)
		{
			if (string.IsNullOrWhiteSpace(query) || foldout.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			for (int i = 0; i < foldout.Widgets.Count; i++)
			{
				var widget = foldout.Widgets[i];
				if (widget.Label != null && widget.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
				if (widget.Entry != null && widget.Entry.Definition.Section.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			return false;
		}

		// Rebuilt only when the page changes shape, which is what a collapsed foldout does.
		private static float MeasureHeight(Page page)
		{
			float h = 0f;
			for (int i = 0; i < page.Foldouts.Count; i++)
			{
				Foldout f = page.Foldouts[i];
				if (!FoldoutMatches(f, _search)) continue;
				h += RowH + RowGap;                                   // the foldout header
				if (!f.Open && string.IsNullOrWhiteSpace(_search)) continue;
				for (int j = 0; j < f.Widgets.Count; j++)
				{
					Widget wd = f.Widgets[j];
					if (!string.IsNullOrWhiteSpace(_search) && wd.Label != null
						&& wd.Label.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0
						&& (wd.Entry == null || wd.Entry.Definition.Section.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)) continue;
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
				if (!FoldoutMatches(f, _search)) continue;

				bool searching = !string.IsNullOrWhiteSpace(_search);
				string arrow = f.Open || searching ? "−" : "+";
				if (GuiKit.Button(new Rect(0f, y, w, RowH), arrow + "  " + f.Title) && !searching) f.Open = !f.Open;
				y += RowH + RowGap;

				if (!f.Open && !searching) continue;

				for (int j = 0; j < f.Widgets.Count; j++)
				{
					Widget wd = f.Widgets[j];
					if (searching && wd.Label != null
						&& wd.Label.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0
						&& (wd.Entry == null || wd.Entry.Definition.Section.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)) continue;
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
								Killiorim.Logger.LogWarning(
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
							Killiorim.Logger.LogWarning(
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

				case Kind.Setting:
					return DrawConfigSetting(wd, x, y, w);
			}
			return y + RowH + RowGap;
		}

		private static float DrawConfigSetting(Widget widget, float x, float y, float width)
		{
			ConfigEntryBase entry = widget.Entry;
			if (entry == null) return y + RowH + RowGap;
			if (entry.SettingType == typeof(bool))
			{
				bool old = (bool)entry.BoxedValue;
				bool current = GuiKit.Toggle(new Rect(x, y, width, RowH), widget.Label, old);
				if (current != old) entry.BoxedValue = current;
				return y + RowH + RowGap;
			}

			string currentText = ConfigRegistry.ValueOf(new ConfigRegistry.Entry { Raw = entry });
			if (!widget.Editing) widget.EditBuffer = currentText;
			GUI.Label(new Rect(x, y, width * 0.34f, RowH), widget.Label, GuiKit.BoxLabel);
			float fieldX = x + width * 0.36f;
			float fieldWidth = width * 0.46f;
			Rect field = new Rect(fieldX, y, fieldWidth, RowH);
			widget.EditBuffer = IsSensitive(widget.Label)
				? GUI.PasswordField(field, widget.EditBuffer ?? "", '*', _searchStyle)
				: GUI.TextField(field, widget.EditBuffer ?? "", _searchStyle);
			if (widget.EditBuffer != currentText) widget.Editing = true;
			if (GuiKit.Button(new Rect(fieldX + fieldWidth + 8f, y,
				width - (fieldX - x) - fieldWidth - 8f, RowH), "APPLY")
				&& TryParseValue(entry.SettingType, widget.EditBuffer, out object parsed))
			{
				try { entry.BoxedValue = parsed; widget.Editing = false; }
				catch (Exception e) { Killiorim.Logger.LogWarning("[Overlay] setting '" + widget.Label + "' rejected: " + e.Message); }
			}
			return y + RowH + RowGap;
		}

		private static bool TryParseValue(Type type, string text, out object parsed)
		{
			parsed = null;
			try
			{
				if (type == typeof(string)) { parsed = text; return true; }
				if (type.IsEnum) { parsed = Enum.Parse(type, text, true); return true; }
				parsed = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
				return true;
			}
			catch { return false; }
		}

		private static bool IsSensitive(string key)
		{
			return key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
				|| key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
				|| key.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static string ReadableName(string value)
		{
			if (string.IsNullOrEmpty(value)) return "SETTING";
			var result = new System.Text.StringBuilder(value.Length + 8);
			for (int i = 0; i < value.Length; i++)
			{
				char c = value[i];
				if (c == '_' || c == '-') { result.Append(' '); continue; }
				if (i > 0 && char.IsUpper(c) && char.IsLower(value[i - 1])) result.Append(' ');
				result.Append(c);
			}
			return result.ToString();
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
