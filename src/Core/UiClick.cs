using System;
using UnityEngine.Events;
using UnityEngine.UI;

namespace VRChatArchiveMod.Core
{
	// EVERY BUTTON IN THIS MOD WENT THROUGH A CAST THAT KILLS THE GAME.
	//
	//     btn.onClick.AddListener((UnityAction)onClick);
	//
	// That cast is an implicit conversion operator Il2CppInterop generates, and it calls
	// DelegateSupport.ConvertDelegate underneath — the same call that takes VRChat down with an
	// access violation on this build. Searching the source for "ConvertDelegate" does not find any of
	// these eleven lines, which is exactly why the crash survived a first pass: the fatal call is
	// spelled as a cast.
	//
	// So the conversion happens in one place now. Today that place refuses when the bridge is off,
	// and the menu draws without responding instead of the game dying while drawing it. When the
	// delegate bridge is repaired this file is the only thing that has to change.
	internal static class UiClick
	{
		private static int _refused;

		internal static void AddClick(Button btn, Action onClick)
		{
			if (btn == null || onClick == null) return;
			if (!Il2CppDelegates.Available) { Refuse(); return; }
			try { btn.onClick.AddListener((UnityAction)onClick); }
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] listener refused: " + Unwrap.Describe(e));
			}
		}

		internal static void AddValueChanged(Slider slider, Action<float> onChanged)
		{
			if (slider == null || onChanged == null) return;
			if (!Il2CppDelegates.Available) { Refuse(); return; }
			try { slider.onValueChanged.AddListener((UnityAction<float>)onChanged); }
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] value listener refused: " + Unwrap.Describe(e));
			}
		}

		// One line for the first, then a count — eleven identical warnings would bury the log.
		private static void Refuse()
		{
			_refused++;
			if (_refused == 1)
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[UiClick] buttons are being drawn WITHOUT click handlers: wiring one needs "
					+ "Il2CppInterop's delegate bridge, which crashes this VRChat build. The menu will "
					+ "appear but not respond. See [Compatibility] AllowIl2CppDelegates.");
			else if (_refused % 25 == 0)
				VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] " + _refused + " unwired controls so far.");
		}
	}
}
