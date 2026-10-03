using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using Killiorim.Core;

namespace Killiorim.Modules
{
	internal enum QMLogType { Info, Moderation, Warning, Error, Success, Join, Left, GodEye }

	internal static class QMConsole
	{
		private const int MaxLogEntries = 23;
		private static readonly object Gate = new object();
		private static readonly List<string> DebugLogs = new List<string>();
		private static int _version;
		private static GameObject _root;
		private static TMPro.TMP_Text _heading;
		private static TMPro.TMP_Text _text;
		private static Transform _carousel;
		private static Transform _vrcPlusBanners;
		private static bool _carouselWasActive;
		private static bool _vrcPlusBannersWasActive;
		private static Transform _layout;

		internal static int Version { get { lock (Gate) return _version; } }
		internal static bool Created => _root != null && _text != null;

		internal static string DisplayText
		{
			get { lock (Gate) return string.Join("\n", DebugLogs); }
		}

		internal static void Log(string message, QMLogType type = QMLogType.Info)
		{
			if (string.IsNullOrEmpty(message)) return;
			string color = ColorFor(type);
			string safeMessage = message.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
			string formatted = $"<color=#FFFFFF>[</color><color={color}>{type}</color><color=#FFFFFF>] - {safeMessage}</color>";
			lock (Gate)
			{
				DebugLogs.Insert(0, formatted);
				if (DebugLogs.Count > MaxLogEntries) DebugLogs.RemoveAt(MaxLogEntries);
				_version++;
			}
		}

		internal static void ClearLogs()
		{
			lock (Gate)
			{
				DebugLogs.Clear();
				_version++;
			}
		}

		private static string ColorFor(QMLogType type)
		{
			switch (type)
			{
				case QMLogType.Info: return "#00BFFF";
				case QMLogType.Moderation: return "#FFA500";
				case QMLogType.Warning: return "#FFD700";
				case QMLogType.Error: return "#FF0000";
				case QMLogType.Success:
				case QMLogType.Join: return "#32CD32";
				case QMLogType.Left: return "#FF0000";
				case QMLogType.GodEye: return "#FFA500";
				default: return "#FFFFFF";
			}
		}

		internal static bool CreateConsole()
		{
			_layout = QuickMenuTabModule.ArchiveContent();
			if (_layout == null) return false;
			if (_root != null && _text != null)
			{
				if (_root.transform.parent != _layout) _root.transform.SetParent(_layout, false);
				KeepLast();
				return true;
			}

			GameObject background = GameObject.Find(
				"Canvas_QuickMenu(Clone)/CanvasGroup/Container/Window/Panel_QM_Widget/Panel_QM_DebugInfo/Panel/Background");
			if (background == null) return false;

			var title = MenuDonor.LaunchpadTitle()?.GetComponent<TMPro.TMP_Text>();
			if (title == null || title.font == null) return false;

			_root = UnityEngine.Object.Instantiate(background, _layout);
			if (_root == null) return false;
			_root.name = "Archive QM Console";
			_root.transform.SetAsLastSibling();
			var rootRect = _root.GetComponent<RectTransform>();
			rootRect.anchorMin = new Vector2(0f, 1f);
			rootRect.anchorMax = new Vector2(1f, 1f);
			rootRect.pivot = new Vector2(0.5f, 1f);
			rootRect.anchoredPosition = Vector2.zero;
			rootRect.sizeDelta = new Vector2(0f, 420f);
			rootRect.localScale = Vector3.one;
			var layoutElement = _root.GetComponent<LayoutElement>() ?? _root.AddComponent<LayoutElement>();
			layoutElement.minHeight = 420f;
			layoutElement.preferredHeight = 420f;
			layoutElement.flexibleHeight = 0f;
			if (_root.GetComponent<RectMask2D>() == null) _root.AddComponent<RectMask2D>();

			_heading = CreateLabel("ConsoleHeading", title, new Vector2(14f, -10f), new Vector2(-14f, -38f), 18f,
				TMPro.TextAlignmentOptions.MidlineLeft);
			var headingRect = _heading.GetComponent<RectTransform>();
			headingRect.anchorMin = new Vector2(0f, 1f);
			headingRect.anchorMax = new Vector2(1f, 1f);
			headingRect.pivot = new Vector2(0.5f, 1f);
			headingRect.sizeDelta = new Vector2(-28f, 28f);
			headingRect.anchoredPosition = new Vector2(0f, -10f);
			_heading.text = "SLEEP CONSOLE  /  RECENT ACTIVITY";

			_text = CreateLabel("DebugText", title, new Vector2(14f, 12f), new Vector2(-14f, -42f), 14f,
				TMPro.TextAlignmentOptions.TopLeft);
			_text.enableWordWrapping = false;
			_text.richText = true;
			LayoutRebuilder.ForceRebuildLayoutImmediate(_layout.GetComponent<RectTransform>());
			return true;
		}

		private static TMPro.TextMeshProUGUI CreateLabel(string name, TMPro.TMP_Text donor, Vector2 offsetMin,
			Vector2 offsetMax, float fontSize, TMPro.TextAlignmentOptions alignment)
		{
			var labelObject = new GameObject(name, new Il2CppSystem.Type[] { Il2CppType.Of<RectTransform>() });
			var rect = labelObject.GetComponent<RectTransform>();
			rect.SetParent(_root.transform, false);
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = offsetMin;
			rect.offsetMax = offsetMax;
			rect.localScale = Vector3.one;

			var label = labelObject.AddComponent<TMPro.TextMeshProUGUI>();
			label.font = donor.font;
			label.fontSharedMaterial = donor.fontSharedMaterial;
			label.fontSize = fontSize;
			label.color = Color.white;
			label.alignment = alignment;
			label.enableAutoSizing = false;
			label.raycastTarget = false;
			return label;
		}

		internal static bool PrepareArchive()
		{
			if (!ModConfig.QMTabEnabled.Value) return false;
			Transform launchpad = MenuDonor.LaunchpadContent();
			if (launchpad == null) return false;

			if (_carousel == null)
			{
				_carousel = launchpad.Find("Carousel_Banners");
				if (_carousel != null) _carouselWasActive = _carousel.gameObject.activeSelf;
			}
			if (_vrcPlusBanners == null)
			{
				_vrcPlusBanners = launchpad.Find("VRC+_Banners");
				if (_vrcPlusBanners != null) _vrcPlusBannersWasActive = _vrcPlusBanners.gameObject.activeSelf;
			}
			if (_carousel != null) _carousel.gameObject.SetActive(false);
			if (_vrcPlusBanners != null) _vrcPlusBanners.gameObject.SetActive(false);
			try { LayoutRebuilder.ForceRebuildLayoutImmediate(launchpad.GetComponent<RectTransform>()); } catch { }
			return QuickMenuTabModule.ArchiveContent() != null;
		}

		internal static void KeepLast()
		{
			if (_root == null || _layout == null) return;
			try
			{
				bool changed = false;
				if (_root.transform.parent != _layout)
				{
					_root.transform.SetParent(_layout, false);
					changed = true;
				}
				if (_root.transform.GetSiblingIndex() != _layout.childCount - 1)
				{
					_root.transform.SetAsLastSibling();
					changed = true;
				}
				if (changed) LayoutRebuilder.ForceRebuildLayoutImmediate(_layout.GetComponent<RectTransform>());
			}
			catch { }
		}

		internal static void DestroyConsole()
		{
			if (_root != null) UnityEngine.Object.Destroy(_root);
			_root = null;
			_heading = null;
			_text = null;
			try { if (_carousel != null) _carousel.gameObject.SetActive(_carouselWasActive); } catch { }
			try { if (_vrcPlusBanners != null) _vrcPlusBanners.gameObject.SetActive(_vrcPlusBannersWasActive); } catch { }
			_carousel = null;
			_vrcPlusBanners = null;
			_layout = null;
		}

		internal static void UpdateDisplay()
		{
			if (_text != null) _text.text = DisplayText;
		}
	}

	internal sealed class QMConsoleModule : IModule
	{
		public override string Name => "QMConsole";

		private float _nextTry;
		private int _shown = -1;
		private BepInEx.Logging.ManualLogSource _source;

		public override void OnInitialize()
		{
			_source = Killiorim.Logger;
			if (_source != null) _source.LogEvent += OnPluginLog;
		}

		public override void OnUpdate()
		{
			if (!ModConfig.QMTabEnabled.Value)
			{
				if (QMConsole.Created) QMConsole.DestroyConsole();
				return;
			}

			if (!QMConsole.Created)
			{
				float now = Time.realtimeSinceStartup;
				if (now < _nextTry) return;
				_nextTry = now + 1f;
				if (!QMConsole.PrepareArchive()) return;
				if (!QMConsole.CreateConsole()) return;
			}
			else QMConsole.KeepLast();

			int version = QMConsole.Version;
			if (version == _shown) return;
			_shown = version;
			QMConsole.UpdateDisplay();
		}

		public override void OnShutdown()
		{
			if (_source != null) _source.LogEvent -= OnPluginLog;
			_source = null;
			QMConsole.DestroyConsole();
		}

		private static void OnPluginLog(object sender, BepInEx.Logging.LogEventArgs e)
		{
			string level = e.Level.ToString();
			QMLogType type = level.Equals("Warning", StringComparison.OrdinalIgnoreCase)
				? QMLogType.Warning
				: level.Equals("Error", StringComparison.OrdinalIgnoreCase)
				  || level.Equals("Fatal", StringComparison.OrdinalIgnoreCase)
					? QMLogType.Error
					: QMLogType.Info;
			QMConsole.Log(e.Data?.ToString(), type);
		}
	}
}