using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// VRCHAT ARCHIVE — a row in VRChat's own avatar sidebar.
	//
	// The data-side attempt is not enough on its own. Adding a 7th FavoriteListModel to
	// API.Favorites._avatars DOES land (the log shows 7 lists holding 147 avatars), but no row
	// appears — and a live dump says why: the sidebar is not a projection of that collection. It is
	// COMPOSED. Under "My Avatars" sit "Recently Used", "Owned", "Uploaded" and "SDK Test Avatars",
	// none of which are favourite lists at all, alongside the six that are. Something merges several
	// sources under foldout headers, and writing to one source after the merge has run notifies
	// nobody.
	//
	// So this takes the route that has never failed us: clone what the game already built.
	//   Foldout_MyAvatars
	//   Avatars Container            [VerticalLayoutGroup]   <- our parent
	//     Cell_MM_SidebarListItem prefab(Clone)              <- our donor
	//       Mask/Text_Name           "avatars1"
	//       Count_BG/Text_Number     "50/50"
	//       Icon, Background_Shadow, Background_SelectedState, Text_Subtitle
	//
	// Clicking it opens the mod's own FAVORITES tab, which already renders the full list with
	// thumbnails. Rendering into VRChat's own avatar grid is a separate problem — it needs IAvatar
	// objects the grid will accept — and it is not worth blocking a working row on.
	public class ArchiveSidebarRowModule : IModule
	{
		public override string Name => "ArchiveSidebarRow";

		private const string RowName = "VA_ArchiveSidebarRow";

		private Transform _row;
		private TMPro.TMP_Text _name, _count;
		private float _next;
		private int _shown = -1;

		public static string Status = "off";

		public override void OnUpdate()
		{
			try
			{
				// No gate: the Archive category is always on.

				// FALLBACK ONLY. Once ArchiveCategoryModule publishes a real category, the game
				// draws its own row — and this cloned one would be a second VRCHAT ARCHIVE line
				// right under it. The real one wins: it fills VRChat's grid, this one cannot.
				if (ArchiveCategoryModule.Published)
				{
					if (_row != null) Remove();
					Status = "replaced by the native category";
					return;
				}

				float now = Time.realtimeSinceStartup;
				if (now < _next) return;
				_next = now + 1f;

				if (_row == null) { Build(); return; }

				int n = FavoritesModule.Count;
				if (n != _shown)
				{
					_shown = n;
					try { if (_count != null) _count.text = n.ToString(); } catch { }
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveRow] " + e.Message); }
		}

		public override void OnSceneLoaded(int buildIndex) { _row = null; _name = null; _count = null; _shown = -1; }

		public override void OnShutdown() => Remove();

		private void Build()
		{
			try
			{
				Transform box = FindActive("Avatars Container");
				if (box == null) { Status = "waiting for the avatar menu"; return; }

				var existing = box.Find(RowName);
				if (existing != null) { Adopt(existing); return; }

				// Donor: any row the game already made. Cloning one keeps every StyleElement, so
				// ours is themed and sized exactly like its neighbours with no work of our own.
				Transform donor = null;
				for (int i = 0; i < box.childCount; i++)
				{
					var c = box.GetChild(i);
					if (c != null && c.name.StartsWith("Cell_MM_SidebarListItem", StringComparison.Ordinal)) { donor = c; break; }
				}
				if (donor == null) { Status = "no sidebar row to clone"; return; }

				var go = UnityEngine.Object.Instantiate(donor.gameObject, box);
				go.name = RowName;
				var t = go.transform;
				t.SetAsLastSibling();

				// Drop the game's own handler from the ROOT so selecting our row cannot also drive
				// VRChat's category selection; keep StyleElement so the game still themes it.
				try { MenuCard.StripRoot(t, keepStyle: true); } catch { }

				var g = t.GetComponent<Graphic>();
				if (g == null)
				{
					var img = go.AddComponent<Image>();
					img.color = new Color(0f, 0f, 0f, 0f);
					g = img;
				}
				g.raycastTarget = true;

				var btn = t.GetComponent<Button>() ?? go.AddComponent<Button>();
				btn.targetGraphic = g;
				btn.interactable = true;
				btn.transition = Selectable.Transition.None;
				try { btn.onClick.RemoveAllListeners(); } catch { }
				Core.UiClick.AddClick(btn, Menu.OpenFavorites);

				// A selected-state highlight left on from the donor would make our row look active
				// whenever the donor was.
				try { t.Find("Background_SelectedState")?.gameObject.SetActive(false); } catch { }

				go.SetActive(true);
				Adopt(t);

				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveRow] VRCHAT ARCHIVE row added to the avatar sidebar.");
			}
			catch (Exception e)
			{
				Status = "could not add the row: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveRow] build failed: " + e);
			}
		}

		private void Adopt(Transform t)
		{
			_row = t;
			try { _name = t.Find("Mask/Text_Name")?.GetComponent<TMPro.TMP_Text>(); } catch { }
			try
			{
				var cbg = t.Find("Count_BG");
				if (cbg != null)
				{
					cbg.gameObject.SetActive(true);
					_count = cbg.Find("Text_Number")?.GetComponent<TMPro.TMP_Text>();
				}
			}
			catch { }

			try { if (_name != null) _name.text = "VRCHAT ARCHIVE"; } catch { }
			try { t.Find("Text_Subtitle")?.gameObject.SetActive(false); } catch { }

			_shown = FavoritesModule.Count;
			try { if (_count != null) _count.text = _shown.ToString(); } catch { }
			Status = "row shown in VRChat's avatar sidebar";
		}

		private void Remove()
		{
			try { if (_row != null) UnityEngine.Object.Destroy(_row.gameObject); } catch { }
			_row = null; _name = null; _count = null; _shown = -1;
			Status = "off";
		}

		private static Transform FindActive(string name)
		{
			try
			{
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.name != name) continue;
					try
					{
						if (!t.gameObject.scene.IsValid()) continue;
						if (!t.gameObject.activeInHierarchy) continue;   // the live menu, not the prefab
					}
					catch { continue; }
					return t;
				}
			}
			catch { }
			return null;
		}
	}
}
