using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// ONE MENU AT A TIME.
	//
	// The mod's menu is IMGUI drawn straight to the screen; VRChat's QuickMenu is a uGUI canvas in
	// the world. Neither knows the other exists, so opening ours over an open QuickMenu left two
	// interfaces stacked on the same pixels, both taking clicks.
	//
	// Rather than fight over draw order — IMGUI and uGUI do not share one, so there is no order that
	// fixes it — the two are made mutually exclusive: while the mod menu is up, VRChat's canvas is
	// switched off, and it is switched back on exactly as it was when the mod menu closes.
	//
	// Deactivating the canvas rather than closing the menu through VRChat's own path is deliberate:
	// the close path is obfuscated and differs between builds, while SetActive is reversible and
	// cannot leave VRChat's menu state half-torn-down. The cost is that the QuickMenu reappears
	// where it was rather than closed, which is what someone toggling a mod menu expects anyway.
	public class MenuExclusiveModule : IModule
	{
		public override string Name => "MenuExclusive";

		private bool _hidden;          // we are the reason it is off
		private GameObject _hiddenGo;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.MenuExclusive.Value)
				{
					Restore();
					return;
				}

				bool ours = Core.Menu.Visible;

				if (!ours)
				{
					Restore();
					return;
				}

				if (_hidden) return;

				var qm = Core.QuickMenu.Root();
				if (qm == null) return;

				var go = qm.gameObject;
				// Only hide something that is actually showing: switching off an already-closed menu
				// and then "restoring" it would OPEN VRChat's menu when the mod menu closes.
				if (!go.activeSelf) return;

				go.SetActive(false);
				_hiddenGo = go;
				_hidden = true;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuExclusive] {e.Message}"); }
		}

		public override void OnShutdown() => Restore();

		// RESTORE FIRST, then forget. This used to drop the reference without touching the object,
		// on the assumption that the canvas died with the scene — but VRChat's UserInterface canvas
		// is DontDestroyOnLoad, so it SURVIVES the world change. Forgetting it while we had it
		// switched off left VRChat's menu permanently dead: Escape and the menu button did nothing,
		// because the object they open was still SetActive(false) and nothing remembered to undo it.
		public override void OnSceneLoaded(int buildIndex)
		{
			Restore();
			_hidden = false;
			_hiddenGo = null;
		}

		private void Restore()
		{
			if (!_hidden) return;
			try { if (_hiddenGo != null) _hiddenGo.SetActive(true); }
			catch { }
			_hidden = false;
			_hiddenGo = null;
		}
	}
}
