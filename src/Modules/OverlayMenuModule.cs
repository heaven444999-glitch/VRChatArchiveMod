using System;
using System.Collections.Generic;
using UnityEngine;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// THE MENU, DECLARED.
	//
	// Every page below is written once, here, at startup. Nothing in this file runs per frame: the
	// lambdas are stored, and Overlay.Draw() reads the declaration and paints it. That is the whole
	// difference from the uGUI menu, where showing a control meant building one.
	//
	// Controls are bound to their ConfigEntry, not to a copy of it. A toggle IS the setting — it
	// reads it to draw and writes it when the user moves it — so there is no sync step, and none of
	// the "the switch flipped back on its own" class of bug can exist here.
	public class OverlayMenuModule : IModule
	{
		public override string Name => "OverlayMenu";

		public override void OnInitialize()
		{
			Build();
			Killiorim.Logger.LogInfo(
				"[Overlay] built: " + Overlay.PageCount + " page(s) — Insert or Right-Shift+N opens it.");
		}

		public override void OnUpdate()
		{
			// Insert is the primary toggle; keep the former shortcut as a fallback.
			if (Input.GetKeyDown(KeyCode.Insert)
				|| (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.N)))
				Overlay.Visible = !Overlay.Visible;
		}

		public override void OnGui() => Overlay.Draw();

		// ---------------------------------------------------------------- the declaration

		private static void Build()
		{
			Overlay.Clear();
			var overview = Overlay.AddPage("OVERVIEW");
			var entries = ConfigRegistry.Config();
			overview.Group("LIVE STATUS")
				.Label(() => "FRAME RATE     " + Mathf.RoundToInt(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f)) + " FPS")
				.Label(() => "SETTINGS       " + entries.Count + " AVAILABLE")
				.Label(() => "FEATURE AREAS  " + (Overlay.PageCount - 1));

			var pages = new Dictionary<string, Overlay.Page>(StringComparer.OrdinalIgnoreCase);
			var groups = new Dictionary<string, Overlay.Foldout>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < entries.Count; i++)
			{
				ConfigRegistry.Entry entry = entries[i];
				if (entry?.Raw == null) continue;
				string section = entry.Section ?? "General";
				int dot = section.IndexOf('.');
				string area = dot < 0 ? section : section.Substring(0, dot);
				string pageKey = area.Trim();
				if (!pages.TryGetValue(pageKey, out Overlay.Page page))
				{
					page = Overlay.AddPage(DisplayName(pageKey));
					pages.Add(pageKey, page);
				}

				string groupKey = section;
				if (!groups.TryGetValue(groupKey, out Overlay.Foldout group))
				{
					group = page.Group(DisplayName(section.Replace('.', ' ')));
					group.Open = page.Foldouts.Count == 1;
					groups.Add(groupKey, group);
				}
				group.Setting(entry.Raw);
			}
		}

		private static string DisplayName(string value)
		{
			if (string.IsNullOrEmpty(value)) return "GENERAL";
			var result = new System.Text.StringBuilder(value.Length + 8);
			for (int i = 0; i < value.Length; i++)
			{
				char c = value[i];
				if (i > 0 && char.IsUpper(c) && char.IsLower(value[i - 1])) result.Append(' ');
				result.Append(c);
			}
			return result.ToString().ToUpperInvariant();
		}
	}
}
