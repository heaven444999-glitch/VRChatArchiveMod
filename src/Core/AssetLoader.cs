using System;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// Loads the client's branding images — embedded in the DLL as EmbeddedResource (see
	// the .csproj) — into Texture2D for the IMGUI menu. Each texture is decoded once and
	// cached; the getters are safe to call every OnGUI frame. If a resource is missing or
	// fails to decode, the getter returns null and the menu falls back gracefully.
	public static class AssetLoader
	{
		private static Texture2D _bg, _archive, _menuBg;
		private static bool _bgTried, _archiveTried, _menuBgTried;

		public static Texture2D Background
		{
			get { if (!_bgTried) { _bgTried = true; _bg = Load("background.jpg"); } return _bg; }
		}

		// The QuickMenu wallpaper gets its OWN image: that panel is nearly square, and the wide
		// 1920x1080 artwork had to be squashed to fill it. Falls back to Background so a missing
		// resource degrades instead of leaving the menu blank.
		public static Texture2D MenuBackground
		{
			get
			{
				if (!_menuBgTried) { _menuBgTried = true; _menuBg = Load("menu_bg.png"); }
				return _menuBg != null ? _menuBg : Background;
			}
		}

		// Rem heart, used on the soundboard buttons.
		private static Texture2D _heart;
		private static bool _heartTried;
		public static Texture2D HeartIcon
		{
			get { if (!_heartTried) { _heartTried = true; _heart = Load("5560-heart-rem.png"); } return _heart; }
		}

		public static Texture2D ArchiveLogo
		{
			get { if (!_archiveTried) { _archiveTried = true; _archive = Load("logo_archive.jpg"); } return _archive; }
		}

		// Any embedded image, by resource name. Cached — including the misses, so a name that does
		// not exist is not re-read from the assembly on every OnGUI frame.
		private static readonly System.Collections.Generic.Dictionary<string, Texture2D> IconCache =
			new System.Collections.Generic.Dictionary<string, Texture2D>();
		public static Texture2D Icon(string resourceName)
		{
			if (string.IsNullOrEmpty(resourceName)) return null;
			if (IconCache.TryGetValue(resourceName, out var t)) return t;
			t = Load(resourceName);
			IconCache[resourceName] = t;
			return t;
		}

		// Era artwork for the 2017 loading screen, extracted from the genuine Steam build.
		// Cached by name so OnGUI can ask for them every frame.
		private static readonly System.Collections.Generic.Dictionary<string, Texture2D> EraCache =
			new System.Collections.Generic.Dictionary<string, Texture2D>();
		public static Texture2D EraTexture(string key)
		{
			if (EraCache.TryGetValue(key, out var t)) return t;
			t = Load("era_" + key + ".png");
			EraCache[key] = t;
			return t;
		}

		private static Texture2D Load(string resourceName)
		{
			try
			{
				byte[] data;
				using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
				{
					if (s == null)
					{
						VRChatArchiveModPlugin.Logger.LogWarning($"[Assets] embedded resource not found: {resourceName}");
						return null;
					}
					using (var ms = new MemoryStream())
					{
						s.CopyTo(ms);
						data = ms.ToArray();
					}
				}

				var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
				tex.hideFlags = HideFlags.HideAndDontSave;
				tex.wrapMode = TextureWrapMode.Clamp;
				// Decodes JPG/PNG bytes into the texture (resizes it to the image).
				ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(data));
				VRChatArchiveModPlugin.Logger.LogInfo($"[Assets] loaded {resourceName} ({tex.width}x{tex.height}).");
				return tex;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[Assets] failed to load {resourceName}: {e}");
				return null;
			}
		}
	}
}
