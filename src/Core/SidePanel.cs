using System;
using UnityEngine;

namespace Killiorim.Core
{
	// WHERE THE TWO SIDE PANELS HANG. The panels themselves are built by PanelSkin; all this file
	// decides is what they are parented to, and that decision has its own history worth keeping.
	//
	// PARENTED TO THE MENU'S WINDOW, BESIDE THE WINGS — NOT INSIDE ONE, AND NOT ON THE HUD.
	//
	// · Child of the QuickMenu, because in VR the menu is attached to the wrist: a panel hung on the
	//   HUD would stay planted in the world while the menu moves with the hand. That is why this is
	//   not the HUD approach a reference client uses — that is a desktop answer to a problem VR has.
	// · Beside the wings rather than among their pages, because every page in a wing fills the same
	//   rect and each wing's own MenuStateController shows exactly one at a time. A page of ours left
	//   permanently visible painted over whatever the wing was showing, and the wings went blank.
	// · Window itself is the right host: measured, it carries no StyleElement, no layout group and no
	//   CanvasGroup, so nothing there will restyle or re-lay-out what we attach, and unlike a wing it
	//   never animates to alpha 0 on retract.
	public static class SidePanel
	{
		// One failure is enough. If a panel ever fails to build, this stops asking for the rest of
		// the session rather than retrying every few seconds against a menu it might disturb.
		private static bool _refused;

		/// <summary>Builds (or rebuilds) a panel. left = the PLAYERS side. Null while the menu is not
		/// up, or for good once a build has failed.</summary>
		public static PanelSkin.Panel Build(bool left, string name, string title, string wallpaper)
		{
			if (_refused) return null;
			Transform inner = MenuDonor.Wing(left);
			if (inner == null) return null;

			Transform host = Window(inner);
			if (host == null) return null;

			var p = PanelSkin.Build(host, left, name, title, wallpaper, WingRoot(inner));
			if (p == null) { _refused = true; return null; }
			return p;
		}

		/// <summary>The Wing_* object itself — what the panel watches to know how open the wing is.
		/// The OBJECT and not just its CanvasGroup, because retraction is not always a fade: the wing
		/// can simply be deactivated, and then its alpha still reads 1. PanelSkin takes the group off
		/// this when there is one and falls back to the active state when there is not.
		/// Found by climbing from the wing's InnerContainer, the same climb Window() already makes.</summary>
		private static Transform WingRoot(Transform inner)
		{
			try
			{
				for (Transform t = inner; t != null; t = t.parent)
				{
					if (t.name == "Window") break;   // gone too far: the wing is below this
					if (t.name.StartsWith("Wing_", StringComparison.Ordinal)) return t;
				}
			}
			catch { }
			return null;
		}

		/// <summary>The menu's Window — the object Wing_Left and Wing_Right hang from, and therefore
		/// the right place to be their sibling. Climbs by NAME from the wing we already resolved, so
		/// it needs no path of its own to rot.</summary>
		private static Transform Window(Transform inner)
		{
			Transform t = inner;
			int guard = 0;
			while (t != null && guard++ < 8)
			{
				if (t.name == "Window") return t;
				t = t.parent;
			}
			// Fall back to the wing's own grandparent: still inside the menu, still a wing sibling.
			try { return inner.parent != null ? inner.parent.parent : null; } catch { return null; }
		}
	}
}
