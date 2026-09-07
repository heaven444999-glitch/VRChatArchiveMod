using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// ARCHIVE FAVORITES for WORLDS and SOCIAL — rendered as OUR OWN grid.
	//
	// The whole point, learned the hard way: injecting ids into VRChat's FavoriteArea does NOT
	// render. The worlds and social pages are Voyager pages that rebuild their grid from a server
	// fetch on selection, so a FavoriteListModel we fill lands in the data (the log even said
	// "filled with 54 member(s)") and the screen still shows (0). And there is nothing to hijack the
	// way the avatar tab is hijacked: the interop has an AvatarContentSection with an observable
	// IList setter, but there is NO WorldContentSection / UserContentSection and no IWorld / IUser
	// to feed one. Confirmed against the on-disk 2026 interop.
	//
	// So this takes the route that has never failed us and is what FavCat actually does: build our
	// own grid and graft it in. A cloned sidebar row ("ARCHIVE FAVORITES") toggles an opaque overlay
	// we own, laid over the menu's content column, filled with cells we build from scratch (a
	// thumbnail + a name) — the exact from-scratch approach that finally made the QuickMenu sliders
	// render. No VRChat cell is cloned, so no obfuscated per-cell component can null our image back
	// out, and the thumbnails come from the Archive bridge (which even has images for private or
	// deleted content that VRChat's live fetch would 404 on).
	//
	// Paths come from a live capture (captures/menu_2026-08-25_15-40-43.txt), not guessed:
	//   WORLDS  Menu_MM_Worlds(Clone)
	//     ScrollRect_Navigation .../ Playlists / Button_MM_WorldPlaylist(Clone)   <- sidebar donor
	//     ScrollRect_Content    .../ Content_WorldCategory / CellGrid_MM_Content   <- content column
	//   SOCIAL  Menu_Social(Clone)
	//     ScrollRect_Navigation .../ Cell_MM_SidebarListItem_*                     <- sidebar donor
	//     ScrollRect_Content    .../ (grouped cells)                               <- content column
	// Both donor rows share the children we drive: Mask/Text_Name and Background_SelectedState.
	public class ArchiveFavGridModule : IModule
	{
		public override string Name => "ArchiveFavGrid";

		private readonly Pane _worlds;
		private readonly Pane _users;

		public static string WorldStatus = "idle";
		public static string UserStatus = "idle";

		public ArchiveFavGridModule()
		{
			_worlds = new Pane
			{
				Kind = "world",
				MenuContains = "Menu_MM_Worlds",
				DonorPrefix = "Button_MM_WorldPlaylist",
				Title = "ARCHIVE FAVORITES",
				IdSource = () => WorldFavoritesModule.Snapshot(),
				RevSource = () => WorldFavoritesModule.Revision,
				Enabled = () => true,   // Favorites/WorldList went 2026-09-01; Plugin.cs decides
				CellSize = new Vector2(288f, 250f),   // matches the native Cell_MM_World
				ThumbAspect = 0.62f,   // 16:10-ish world card (from-scratch fallback only)
				Columns = 4,
				CellDonorName = "Cell_MM_World",     // confirmed on screen: renders exactly like VRChat's
			};
			_users = new Pane
			{
				Kind = "user",
				MenuContains = "Menu_Social",
				DonorPrefix = "Cell_MM_SidebarListItem",
				Title = "ARCHIVE FAVORITES",
				IdSource = () => UserFavoritesModule.Snapshot(),
				RevSource = () => UserFavoritesModule.Revision,
				Enabled = () => true,   // Favorites/SocialList went 2026-09-01; Plugin.cs decides
				CellSize = new Vector2(288f, 250f),
				ThumbAspect = 0.72f,   // friend-card proportions: portrait over a name + status line
				Columns = 4,
				// The WORLD card, reused for users — and it renders correctly on screen (portrait,
				// name, native styling), so it stays until a capture of the friends grid names the
				// real user cell. The blank grey boxes blamed on it were thumbnails that had not
				// downloaded yet, not the wrong donor. MenuCaptureModule records the friends page
				// now, and the real cell name goes here once that capture exists.
				CellDonorName = "Cell_MM_World",
			};
		}

		public override void OnUpdate()
		{
			_worlds.Tick(); WorldStatus = _worlds.Status;
			_users.Tick();  UserStatus = _users.Status;
		}

		public override void OnSceneLoaded(int buildIndex) { _worlds.OnScene(); _users.OnScene(); }
		public override void OnShutdown() { _worlds.Teardown(); _users.Teardown(); }

		// ================================================================= entry (one favourite)
		internal sealed class Entry
		{
			public string Id;
			public string Name = "";
			public string Image = "";        // vrchat.cloud image url from the OG preview
			public bool MetaDone;
			public int ThumbState;           // 0 idle, 1 fetching, 2 ready, 3 failed
			public Texture2D Thumb;

			// live cell
			public Transform Cell;
			public RawImage ThumbImg;
			public Image ThumbBg;
			public TMPro.TMP_Text NameTmp;
			public string ShownName = null;  // last text pushed to NameTmp (main-thread sync)
		}

		// ================================================================= one menu (worlds or social)
		internal sealed class Pane
		{
			public string Kind, MenuContains, DonorPrefix, Title;
			// Name of VRChat's own card to clone for this pane's grid. null = build from scratch.
			public string CellDonorName;
			public Func<List<string>> IdSource;
			public Func<int> RevSource;
			public Func<bool> Enabled;
			public Vector2 CellSize;
			public float ThumbAspect;
			public int Columns;

			public string Status = "idle";

			private Transform _root;            // our submenu (Menu_MM_Worlds / Menu_Social), cached
			private Transform _row, _overlay, _gridContent;
			private TMPro.TMP_Text _rowCount, _headerCount;
			private bool _shown;
			private int _lastRev = int.MinValue;
			private TMPro.TMP_FontAsset _font;
			private bool _loggedGrid;

			// The native card to clone, PER PANE. It used to be one static shared by both panes, so
			// whichever page was visited first won — and social ended up cloning a WORLD card, which
			// is why its grid came out as blank grey boxes. Each pane now resolves its own donor,
			// scoped to its own page, and a pane with no donor name draws from-scratch cards.
			private Transform _cellDonor;
			private float _donorNext;

			// What VRChat's own content column looked like when we took the screen over. If it
			// changes, VRChat has switched to one of its own sections and ours must get out of the
			// way — see WatchStockSelection.
			private string _contentSig;

			private readonly List<Transform> _stockRows = new List<Transform>();
			private readonly HashSet<int> _baselineSelected = new HashSet<int>();
			private readonly List<Entry> _entries = new List<Entry>();
			private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
			private readonly ConcurrentQueue<(Entry e, byte[] data)> _decoded = new ConcurrentQueue<(Entry, byte[])>();
			private int _inflight;
			private float _nextMeta;
			private bool _metaBusy;

			private const int MaxInflight = 4;

			private static bool Dead(UnityEngine.Object o) => o == null;

			// ----------------------------------------------------------------- lifecycle
			public void OnScene()
			{
				_root = null;
				_row = null; _overlay = null; _gridContent = null; _rowCount = null; _headerCount = null;
				_shown = false; _lastRev = int.MinValue; _font = null; _loggedGrid = false;
				_cellDonor = null; _donorNext = 0f; _contentSig = null;
				_stockRows.Clear(); _baselineSelected.Clear();
				// Cells belonged to the destroyed menu; drop their references so a reopen rebuilds them.
				foreach (var e in _entries) { e.Cell = null; e.ThumbImg = null; e.ThumbBg = null; e.NameTmp = null; e.ShownName = null; }
			}

			public void Teardown()
			{
				try { if (!Dead(_overlay)) UnityEngine.Object.Destroy(_overlay.gameObject); } catch { }
				try { if (!Dead(_row)) UnityEngine.Object.Destroy(_row.gameObject); } catch { }
				OnScene();
				Status = "off";
			}

			// Rate-limit stamps for the subtree searches above.
			private float _nextRootScan, _nextBuildScan;

			public void Tick()
			{
				try
				{
					if (Enabled == null || !Enabled())
					{
						if (!Dead(_overlay) || !Dead(_row)) Teardown();
						return;
					}

					// CHEAP GATE FIRST. The old code called Resources.FindObjectsOfTypeAll<Transform>()
					// every frame for BOTH panes — that walk over every loaded Transform is what pinned
					// the game at ~5 FPS with the menu open. Core.QuickMenu caches the main-menu canvas
					// and exposes a one-bool "is it on screen" check, so when the menu is closed this
					// returns immediately at zero cost.
					if (!Core.QuickMenu.MainVisible) return;

					// Resolve our submenu ONCE, by walking only the cached main-menu subtree (a few
					// thousand nodes, not the whole scene), and keep the reference. The clone persists
					// across open/close, so this search runs once per world, not once per frame.
					if (Dead(_root))
					{
						// A MISS IS RATE-LIMITED, exactly like Core.QuickMenu does for the canvases.
						//
						// The comment above is right that this runs "once per world, not once per
						// frame" — but only when it SUCCEEDS. When our submenu is not there, and on
						// this build it often is not, FindInSubtree walked the whole main-menu
						// subtree, returned null, and did it again next frame. Two panes, so twice
						// per frame. The profiler measured this module at 458 ms per second: the
						// single most expensive thing in the mod, and all of it spent failing.
						float now = Time.realtimeSinceStartup;
						if (now < _nextRootScan) return;
						_nextRootScan = now + 1f;

						var mm = Core.QuickMenu.Main();
						if (Dead(mm)) return;
						_root = FindInSubtree(mm, MenuContains);
						if (Dead(_root)) return;
					}
					Transform root = _root;

					// SAME TREATMENT. Each of these searches the subtree for a donor, and each one
					// retried every frame for as long as it failed to find it — the same unbounded
					// scan as above, three more times.
					if (Dead(_font) || Dead(_row) || Dead(_overlay))
					{
						float now = Time.realtimeSinceStartup;
						if (now < _nextBuildScan) return;
						_nextBuildScan = now + 1f;

						if (Dead(_font)) StealFont(root);
						if (Dead(_row)) BuildRow(root);
						if (Dead(_overlay)) BuildOverlay(root);
					}

					// Keep data current from the id source (RevSource is a cheap int compare).
					int rev = RevSource != null ? RevSource() : 0;
					if (rev != _lastRev) { _lastRev = rev; RebuildData(); if (_shown) Repopulate(); }

					UpdateCounts();

					// Decode any thumbnails that arrived (main-thread only), even when hidden, so a
					// reopen is instant.
					DrainDecoded(2);

					// The heavy per-frame work only matters while OUR page is the one on screen.
					bool pageActive = IsActive(root);
					if (_shown && pageActive)
					{
						WatchStockSelection();
						PumpMetaThrottled();
						RequestThumbs();
						SyncCells();
					}
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] " + e.Message); }
			}

			// ----------------------------------------------------------------- sidebar row
			private void BuildRow(Transform root)
			{
				Transform donor = FindDescendantByPrefix(root, DonorPrefix, requireChild: "Mask/Text_Name");
				if (Dead(donor)) { Status = "waiting for the sidebar"; return; }
				Transform container = donor.parent;
				if (Dead(container)) { Status = "sidebar has no container"; return; }

				string rowName = "VA_FavGridRow_" + Kind;
				var existing = container.Find(rowName);
				if (!Dead(existing)) { AdoptRow(existing, root); return; }

				var go = UnityEngine.Object.Instantiate(donor.gameObject, container);
				go.name = rowName;
				var t = go.transform;
				t.SetAsFirstSibling();

				try { MenuCard.StripRoot(t, keepStyle: true); } catch { }

				var g = t.GetComponent<Graphic>();
				if (Dead(g)) { var im = go.AddComponent<Image>(); im.color = new Color(0f, 0f, 0f, 0f); g = im; }
				g.raycastTarget = true;

				var btn = t.GetComponent<Button>();
				if (Dead(btn)) btn = go.AddComponent<Button>();
				btn.targetGraphic = g;
				btn.interactable = true;
				btn.transition = Selectable.Transition.None;
				try { btn.onClick.RemoveAllListeners(); } catch { }
				Core.UiClick.AddClick(btn, Toggle);

				try { t.Find("Background_SelectedState")?.gameObject.SetActive(false); } catch { }
				go.SetActive(true);

				AdoptRow(t, root);
				VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] ARCHIVE FAVORITES row added to the " + Kind + " sidebar.");
			}

			private void AdoptRow(Transform t, Transform root)
			{
				_row = t;
				try
				{
					var tn = t.Find("Mask/Text_Name");
					var tmp = Dead(tn) ? null : tn.GetComponent<TMPro.TMP_Text>();
					if (!Dead(tmp)) tmp.text = Title;
				}
				catch { }
				// Count badge: worlds use Subtext/Text_Count, social uses Count_BG/Text_Number.
				_rowCount = FindText(t, "Subtext/Text_Count") ?? FindText(t, "Count_BG/Text_Number");
				try { t.Find("Count_BG")?.gameObject.SetActive(true); } catch { }
				try { t.Find("Text_Subtitle")?.gameObject.SetActive(false); } catch { }
				try { t.Find("Subtext/Text_Subtitle")?.gameObject.SetActive(false); } catch { }

				CollectStockRows(root);
				Status = "row shown; " + _entries.Count + " favourite(s)";
			}

			private void CollectStockRows(Transform root)
			{
				_stockRows.Clear();
				try
				{
					var all = root.GetComponentsInChildren<Transform>(true);
					foreach (var tr in all)
					{
						if (Dead(tr) || ReferenceEquals(tr, _row)) continue;
						string n; try { n = tr.name; } catch { continue; }
						if (n.StartsWith("VA_FavGridRow_", StringComparison.Ordinal)) continue;
						bool isRow = n.StartsWith("Cell_MM_SidebarListItem", StringComparison.Ordinal)
								   || n.StartsWith("Button_MM_WorldPlaylist", StringComparison.Ordinal);
						if (!isRow) continue;
						if (Dead(tr.Find("Background_SelectedState"))) continue;
						_stockRows.Add(tr);
					}
				}
				catch { }
			}

			// ----------------------------------------------------------------- overlay grid
			private void BuildOverlay(Transform root)
			{
				Transform content = FindDescendantByName(root, "ScrollRect_Content");
				if (Dead(content)) { Status = "no content column"; return; }

				string overlayName = "VA_FavGridOverlay_" + Kind;
				var existing = content.Find(overlayName);
				if (!Dead(existing)) { AdoptOverlay(existing); return; }

				// Overlay: opaque, fills the content column, sits ON TOP of (does not scroll with) the
				// stock cells. Direct child of ScrollRect_Content, so the stock viewport mask does not
				// clip it and the stock scroll does not move it.
				var overlay = NewRect(overlayName, content);
				Stretch(overlay);
				var obg = overlay.gameObject.AddComponent<Image>();
				obg.color = new Color(0.05f, 0.06f, 0.10f, 1f);
				obg.raycastTarget = true;

				// Header: title + count + close.
				var header = NewRect("Header", overlay);
				header.anchorMin = new Vector2(0f, 1f); header.anchorMax = new Vector2(1f, 1f);
				header.pivot = new Vector2(0.5f, 1f);
				header.sizeDelta = new Vector2(0f, 72f);
				header.anchoredPosition = Vector2.zero;
				var hbg = header.gameObject.AddComponent<Image>();
				hbg.color = new Color(0.09f, 0.20f, 0.23f, 1f);

				var htitle = NewRect("Title", header);
				htitle.anchorMin = new Vector2(0f, 0f); htitle.anchorMax = new Vector2(0.7f, 1f);
				htitle.offsetMin = new Vector2(28f, 0f); htitle.offsetMax = Vector2.zero;
				var titleTmp = Label(htitle, Title, 26f, TMPro.TextAlignmentOptions.MidlineLeft);
				titleTmp.color = new Color(0.86f, 0.66f, 1f);

				var hcount = NewRect("Count", header);
				hcount.anchorMin = new Vector2(0.7f, 0f); hcount.anchorMax = new Vector2(0.9f, 1f);
				hcount.offsetMin = Vector2.zero; hcount.offsetMax = Vector2.zero;
				_headerCount = Label(hcount, "", 20f, TMPro.TextAlignmentOptions.MidlineRight);

				var close = NewRect("Close", header);
				close.anchorMin = new Vector2(0.9f, 0.15f); close.anchorMax = new Vector2(1f, 0.85f);
				close.offsetMin = new Vector2(0f, 0f); close.offsetMax = new Vector2(-20f, 0f);
				var cbg = close.gameObject.AddComponent<Image>();
				cbg.color = new Color(0.86f, 0.24f, 0.44f, 0.9f);
				var cbtn = close.gameObject.AddComponent<Button>();
				cbtn.targetGraphic = cbg;
				cbtn.transition = Selectable.Transition.None;
				Core.UiClick.AddClick(cbtn, Hide);
				var ctext = NewRect("X", close);
				Stretch(ctext);
				Label(ctext, "✕  Close", 18f, TMPro.TextAlignmentOptions.Center);

				// Scroll area under the header.
				var scroll = NewRect("Scroll", overlay);
				scroll.anchorMin = new Vector2(0f, 0f); scroll.anchorMax = new Vector2(1f, 1f);
				scroll.offsetMin = new Vector2(0f, 0f); scroll.offsetMax = new Vector2(0f, -72f);
				var sr = scroll.gameObject.AddComponent<ScrollRect>();
				sr.horizontal = false; sr.vertical = true;
				sr.scrollSensitivity = 32f;
				sr.movementType = ScrollRect.MovementType.Clamped;

				var viewport = NewRect("Viewport", scroll);
				Stretch(viewport);
				viewport.gameObject.AddComponent<RectMask2D>();
				var vim = viewport.gameObject.AddComponent<Image>();
				vim.color = new Color(0f, 0f, 0f, 0.001f);   // must have a graphic to receive drag

				var gridContent = NewRect("Content", viewport);
				gridContent.anchorMin = new Vector2(0f, 1f); gridContent.anchorMax = new Vector2(1f, 1f);
				gridContent.pivot = new Vector2(0.5f, 1f);
				gridContent.anchoredPosition = Vector2.zero;
				var grid = gridContent.gameObject.AddComponent<GridLayoutGroup>();
				grid.cellSize = CellSize;
				grid.spacing = new Vector2(22f, 22f);
				grid.padding = new RectOffset(28, 28, 22, 28);
				grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
				grid.startAxis = GridLayoutGroup.Axis.Horizontal;
				grid.childAlignment = TextAnchor.UpperCenter;
				grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
				grid.constraintCount = Columns;
				var fitter = gridContent.gameObject.AddComponent<ContentSizeFitter>();
				fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
				fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

				sr.viewport = viewport;
				sr.content = gridContent;

				overlay.gameObject.SetActive(false);
				_overlay = overlay;
				_gridContent = gridContent;

				if (_shown) { _overlay.gameObject.SetActive(true); Repopulate(); }
				VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] grid overlay built.");
			}

			private void AdoptOverlay(Transform overlay)
			{
				_overlay = overlay;
				_gridContent = overlay.Find("Scroll/Viewport/Content");
				_headerCount = FindText(overlay, "Header/Count");
			}

			// ----------------------------------------------------------------- show / hide
			// The il2cpp→managed boundary: anything escaping here is not a normal exception, it is an
			// interop error that swallows the click entirely. Nothing gets out.
			private void Toggle()
			{
				try { if (_shown) Hide(); else Show(); }
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] toggle: " + e.Message); }
			}

			private void Show()
			{
				if (Dead(_overlay)) return;
				_overlay.gameObject.SetActive(true);
				_overlay.SetAsLastSibling();
				_shown = true;
				try { _row?.Find("Background_SelectedState")?.gameObject.SetActive(true); } catch { }

				// Baseline: remember which stock rows read as selected right now, so we only hide when
				// a DIFFERENT row lights up (a real click) — never fighting VRChat's own state.
				_baselineSelected.Clear();
				foreach (var sr in _stockRows)
				{
					if (Dead(sr)) continue;
					var sel = sr.Find("Background_SelectedState");
					if (!Dead(sel) && sel.gameObject.activeSelf) _baselineSelected.Add(sr.GetInstanceID());
				}

				_contentSig = ContentSignature();
				Repopulate();
				Status = "shown; " + _entries.Count + " favourite(s)";
			}

			private void Hide()
			{
				if (!Dead(_overlay)) _overlay.gameObject.SetActive(false);
				_shown = false;
				try { _row?.Find("Background_SelectedState")?.gameObject.SetActive(false); } catch { }
				Status = "hidden";
			}

			private void WatchStockSelection()
			{
				// PRIMARY SIGNAL: VRChat's own content column changed. Our overlay covers the WHOLE
				// content column, so when VRChat swaps the section inside it (clicking "Uploaded",
				// "Recently Visited", "All Friends"…) nothing looks different — our grid just stays
				// on top, which is exactly the bug. Watching the column's own active-section
				// fingerprint catches EVERY section change, including the foldout rows that carry no
				// Background_SelectedState and so were invisible to the row check below.
				string sig = ContentSignature();
				if (sig != null && _contentSig != null && sig != _contentSig) { Hide(); return; }

				// SECONDARY: a stock sidebar row lit up that was not lit when we opened.
				foreach (var sr in _stockRows)
				{
					if (Dead(sr)) continue;
					var sel = sr.Find("Background_SelectedState");
					if (Dead(sel) || !sel.gameObject.activeSelf) continue;
					if (_baselineSelected.Contains(sr.GetInstanceID())) continue;
					Hide();
					return;
				}
			}

			// ----------------------------------------------------------------- data
			private void RebuildData()
			{
				var ids = IdSource != null ? IdSource() : new List<string>();
				var keep = new Dictionary<string, Entry>(_byId, StringComparer.OrdinalIgnoreCase);
				_entries.Clear(); _byId.Clear();
				foreach (var id in ids)
				{
					if (string.IsNullOrEmpty(id) || _byId.ContainsKey(id)) continue;
					Entry e = keep.TryGetValue(id, out var old) ? old : new Entry { Id = id, Name = Short(id) };
					_entries.Add(e); _byId[id] = e;
				}
				Status = _entries.Count + " favourite(s)";
			}

			private void PumpMetaThrottled()
			{
				if (!VaAuth.InsideClient) return;
				float now = Time.realtimeSinceStartup;
				if (now < _nextMeta || _metaBusy) return;
				_nextMeta = now + 1f;

				var batch = new List<Entry>(100);
				foreach (var e in _entries) { if (!e.MetaDone) { batch.Add(e); if (batch.Count >= 100) break; } }
				if (batch.Count == 0) return;
				_metaBusy = true;
				_ = ResolveMetaAsync(batch);
			}

			private async System.Threading.Tasks.Task ResolveMetaAsync(List<Entry> batch)
			{
				try
				{
					var sb = new StringBuilder("{\"action\":\"meta\",\"kind\":\"").Append(Kind).Append("\",\"ids\":[");
					for (int i = 0; i < batch.Count; i++)
					{
						if (i > 0) sb.Append(',');
						sb.Append('"').Append(batch[i].Id).Append('"');
					}
					sb.Append("]}");

					var (ok, raw, status) = await VaAuth.FavRawAsync(sb.ToString());
					if (!ok) { foreach (var f in batch) f.MetaDone = true; return; }

					// Only plain data is mutated here — this continuation is off the main thread, so
					// no Unity object may be touched. The main-thread SyncCells() paints the change.
					using var doc = JsonDocument.Parse(raw);
					if (doc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Object)
					{
						foreach (var p in res.EnumerateObject())
						{
							string key = p.Name;                       // "world:wrld_xxx" / "user:usr_xxx"
							int c = key.IndexOf(':');
							string id = c >= 0 ? key.Substring(c + 1) : key;
							if (!_byId.TryGetValue(id, out var e)) continue;
							string title = Str(p.Value, "title");
							string image = Str(p.Value, "image");
							title = CleanTitle(title);
							if (!string.IsNullOrWhiteSpace(title)) e.Name = title;
							if (!string.IsNullOrWhiteSpace(image)) e.Image = image;
						}
					}
					foreach (var f in batch) f.MetaDone = true;
				}
				catch { foreach (var f in batch) f.MetaDone = true; }
				finally { _metaBusy = false; }
			}

			private void RequestThumbs()
			{
				if (!VaAuth.InsideClient) return;
				foreach (var e in _entries)
				{
					if (_inflight >= MaxInflight) break;
					if (e.ThumbState != 0 || !e.MetaDone || string.IsNullOrEmpty(e.Image)) continue;
					e.ThumbState = 1; _inflight++;
					_ = FetchThumbAsync(e);
				}
			}

			private async System.Threading.Tasks.Task FetchThumbAsync(Entry e)
			{
				byte[] data = null;
				try
				{
					string cache = Path.Combine(ThumbDir, e.Id + ".img");
					try { if (File.Exists(cache)) data = File.ReadAllBytes(cache); } catch { }
					if (data == null || data.Length < 100)
					{
						data = await GetImageAsync(Small(e.Image));
						if (data == null) data = await GetImageAsync(e.Image);
						if (data != null && data.Length >= 100) { try { File.WriteAllBytes(cache, data); } catch { } }
					}
				}
				catch { }
				finally { _decoded.Enqueue((e, data)); }
			}

			private void DrainDecoded(int max)
			{
				for (int n = 0; n < max; n++)
				{
					if (!_decoded.TryDequeue(out var item)) return;
					_inflight--; if (_inflight < 0) _inflight = 0;
					try
					{
						if (item.data == null || item.data.Length < 100) { item.e.ThumbState = 3; continue; }
						var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
						{ hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
						if (!ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(item.data)))
						{ item.e.ThumbState = 3; continue; }
						item.e.Thumb = tex;
						item.e.ThumbState = 2;
					}
					catch { item.e.ThumbState = 3; }
				}
			}

			// ----------------------------------------------------------------- cells
			private void Repopulate()
			{
				if (Dead(_gridContent)) return;

				// Rebuild only when the set changed size (add/remove); otherwise reuse cells.
				if (_gridContent.childCount != _entries.Count)
				{
					for (int i = _gridContent.childCount - 1; i >= 0; i--)
					{
						try { UnityEngine.Object.Destroy(_gridContent.GetChild(i).gameObject); } catch { }
					}
					foreach (var e in _entries) { e.Cell = null; e.ThumbImg = null; e.ThumbBg = null; e.NameTmp = null; e.ShownName = null; }
					Transform donor = ResolveCellDonor();
					foreach (var e in _entries) { if (Dead(donor)) BuildScratchCell(e); else BuildNativeCell(e, donor); }

					if (!_loggedGrid)
					{
						_loggedGrid = true;
						VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] populated " + _gridContent.childCount
							+ " cell(s) for " + _entries.Count + " favourite(s); style=" + (Dead(donor) ? "scratch" : "native"));
					}
				}
				SyncCells();
			}

			// The native world card, cloned. Gives VRChat's exact look; the obfuscated data-model is
			// stripped from the ROOT (so nothing re-binds it), and SyncCells re-asserts our name and
			// texture every frame in case a surviving child binder tries to blank them.
			private void BuildNativeCell(Entry e, Transform donor)
			{
				var go = UnityEngine.Object.Instantiate(donor.gameObject, _gridContent);
				go.name = "VA_Cell";
				var cell = go.transform;
				try { MenuCard.StripRoot(cell, keepStyle: true); } catch { }
				try { go.SetActive(true); } catch { }

				// Drop the world-only chrome so the card reads as a clean thumbnail + name.
				HideChild(cell, "Detail/StatRow_Population");
				HideChild(cell, "Detail/PreloadProgressBG");
				HideChild(cell, "Image_Mask/AttributeRow");
				HideChild(cell, "Hover");
				HideChild(cell, "Detail/SelectedState");

				var nt = cell.Find("Detail/Text_WorldName");
				e.NameTmp = Dead(nt) ? null : nt.GetComponent<TMPro.TMP_Text>();

				var ci = cell.Find("Image_Mask/ContentImage");
				e.ThumbImg = Dead(ci) ? null : ci.GetComponent<RawImage>();   // ContentImage is a RawImageEx
				e.ThumbBg = null;

				e.Cell = cell; e.ShownName = null;
			}

			// From-scratch fallback, used only until a native Cell_MM_World exists to clone. Brighter
			// than before so the card is clearly visible even before its thumbnail loads.
			// EVERY STEP GUARDED. This runs from a Button.onClick, i.e. inside an il2cpp→managed
			// trampoline, where an escaping NullReferenceException is not a caught exception but a
			// logged interop error that abandons the whole click — which is exactly what happened
			// the first time the social page was opened before any world card existed to clone.
			private void BuildScratchCell(Entry e)
			{
				try
				{
					var cell = NewRect("Cell", _gridContent);
					if (Dead(cell)) return;
					var cbg = cell.gameObject.AddComponent<Image>();
					if (!Dead(cbg)) cbg.color = new Color(0.16f, 0.20f, 0.28f, 1f);

					var thumb = NewRect("Thumb", cell);
					RawImage raw = null; Image tbg = null;
					if (!Dead(thumb))
					{
						thumb.anchorMin = new Vector2(0.05f, 1f - ThumbAspect - 0.02f);
						thumb.anchorMax = new Vector2(0.95f, 0.96f);
						thumb.offsetMin = Vector2.zero; thumb.offsetMax = Vector2.zero;
						tbg = thumb.gameObject.AddComponent<Image>();
						if (!Dead(tbg)) { tbg.color = new Color(0.22f, 0.27f, 0.36f, 1f); tbg.raycastTarget = false; }
						raw = thumb.gameObject.AddComponent<RawImage>();
						if (!Dead(raw)) { raw.color = new Color(1f, 1f, 1f, 0f); raw.raycastTarget = false; }
					}

					TMPro.TextMeshProUGUI ntmp = null;
					var name = NewRect("Name", cell);
					if (!Dead(name))
					{
						name.anchorMin = new Vector2(0.05f, 0.02f);
						name.anchorMax = new Vector2(0.95f, 1f - ThumbAspect - 0.04f);
						name.offsetMin = Vector2.zero; name.offsetMax = Vector2.zero;
						ntmp = Label(name, "", 16f, TMPro.TextAlignmentOptions.Top);
						if (!Dead(ntmp))
						{
							ntmp.enableWordWrapping = true;
							ntmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
						}
					}

					e.Cell = cell; e.ThumbImg = raw; e.ThumbBg = tbg; e.NameTmp = ntmp; e.ShownName = null;
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] scratch cell: " + ex.Message);
				}
			}

			// Find one native world card to clone. Prefer a live Cell_MM_World; cache it statically so
			// both panes share it and the search runs at most once every few seconds until found.
			private Transform ResolveCellDonor()
			{
				if (string.IsNullOrEmpty(CellDonorName)) return null;   // this pane draws from scratch
				if (!Dead(_cellDonor)) return _cellDonor;
				float now = Time.realtimeSinceStartup;
				if (now < _donorNext) return null;
				_donorNext = now + 3f;
				// OUR page first, then the whole main menu. Scoping alone is not enough: the social
				// grid borrows the WORLD card, which only exists under the worlds page, so a
				// strictly-scoped search would find nothing and fall back to scratch cards forever.
				var found = FindDonorIn(_root) ?? FindDonorIn(Core.QuickMenu.Main());
				if (!Dead(found)) _cellDonor = found;
				return _cellDonor;
			}

			private Transform FindDonorIn(Transform scope)
			{
				try
				{
					if (Dead(scope)) return null;
					foreach (var t in scope.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (!n.StartsWith(CellDonorName, StringComparison.Ordinal)) continue;
						if (n.StartsWith("VA_", StringComparison.Ordinal)) continue;
						if (IsOurs(t)) continue;
						// Must carry the parts we drive, or it is not a usable donor.
						if (Dead(t.Find("Detail/Text_WorldName")) || Dead(t.Find("Image_Mask/ContentImage"))) continue;
						return t;
					}
				}
				catch { }
				return null;
			}

			// True when the transform lives inside our own overlay — never clone our own clone.
			private bool IsOurs(Transform t)
			{
				try
				{
					if (Dead(_overlay)) return false;
					var p = t;
					while (p != null) { if (ReferenceEquals(p, _overlay)) return true; p = p.parent; }
				}
				catch { }
				return false;
			}

			// A cheap fingerprint of VRChat's own content column: the names of the sections it has
			// switched on. Used to notice that the user picked one of ITS sections.
			private string ContentSignature()
			{
				try
				{
					if (Dead(_overlay)) return null;
					var host = _overlay.parent;                       // ScrollRect_Content
					if (Dead(host)) return null;
					var vp = host.Find("Viewport");
					if (Dead(vp)) return null;

					var sb = new StringBuilder(64);
					Stack(sb, vp, 0);
					return sb.ToString();
				}
				catch { return null; }
			}

			private static void Stack(StringBuilder sb, Transform t, int depth)
			{
				if (depth > 2) return;
				int n = 0; try { n = t.childCount; } catch { return; }
				for (int i = 0; i < n; i++)
				{
					Transform c = null; try { c = t.GetChild(i); } catch { continue; }
					if (Dead(c)) continue;
					bool on; try { on = c.gameObject.activeSelf; } catch { continue; }
					if (!on) continue;
					try { sb.Append(c.name).Append('|'); } catch { }
					Stack(sb, c, depth + 1);
				}
			}

			private static void HideChild(Transform cell, string path)
			{
				try { var t = cell.Find(path); if (!Dead(t)) t.gameObject.SetActive(false); } catch { }
			}

			private void SyncCells()
			{
				foreach (var e in _entries)
				{
					if (!Dead(e.NameTmp))
					{
						string want = string.IsNullOrEmpty(e.Name) ? Short(e.Id) : e.Name;
						// Compare the ACTUAL text, not just our cached copy: a native cell may carry a
						// surviving binder that blanks Text_WorldName, and re-asserting each frame wins.
						string have = null; try { have = e.NameTmp.text; } catch { }
						if (have != want) { try { e.NameTmp.text = want; e.ShownName = want; } catch { } }
					}

					if (e.ThumbState == 2 && !Dead(e.Thumb) && !Dead(e.ThumbImg))
					{
						if (!ReferenceEquals(e.ThumbImg.texture, e.Thumb))
						{
							try
							{
								e.ThumbImg.texture = e.Thumb;
								e.ThumbImg.color = Color.white;
								if (!Dead(e.ThumbBg)) e.ThumbBg.color = new Color(0f, 0f, 0f, 0f);
							}
							catch { }
						}
					}
				}
			}

			private void UpdateCounts()
			{
				string n = _entries.Count.ToString();
				if (!Dead(_rowCount)) { try { if (_rowCount.text != n) _rowCount.text = n; } catch { } }
				if (!Dead(_headerCount)) { try { string h = "(" + n + ")"; if (_headerCount.text != h) _headerCount.text = h; } catch { } }
			}

			// ----------------------------------------------------------------- helpers
			private void StealFont(Transform root)
			{
				try { var t = root.GetComponentInChildren<TMPro.TMP_Text>(true); if (!Dead(t)) _font = t.font; } catch { }
			}

			private RectTransform NewRect(string name, Transform parent)
			{
				var go = new GameObject(name, Il2CppType.Of<RectTransform>());
				var rt = go.GetComponent<RectTransform>();
				rt.SetParent(parent, false);
				return rt;
			}

			private static void Stretch(RectTransform rt)
			{
				rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
				rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
			}

			private TMPro.TextMeshProUGUI Label(RectTransform rt, string text, float size, TMPro.TextAlignmentOptions align)
			{
				var tmp = rt.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
				if (!Dead(_font)) tmp.font = _font;
				tmp.text = text; tmp.fontSize = size; tmp.alignment = align;
				tmp.enableWordWrapping = false;
				tmp.color = new Color(0.93f, 0.92f, 1f);
				tmp.raycastTarget = false;
				return tmp;
			}

			private async System.Threading.Tasks.Task<byte[]> GetImageAsync(string url)
			{
				try
				{
					if (string.IsNullOrEmpty(url)) return null;
					string body = "{\"action\":\"image\",\"kind\":\"" + Kind + "\",\"url\":\"" + url.Replace("\"", "") + "\"}";
					var (ok, raw, _) = await VaAuth.FavRawAsync(body);
					if (!ok) return null;
					using var doc = JsonDocument.Parse(raw);
					if (!doc.RootElement.TryGetProperty("b64", out var b) || b.ValueKind != JsonValueKind.String) return null;
					return Convert.FromBase64String(b.GetString());
				}
				catch { return null; }
			}

			private static string ThumbDir
			{
				get
				{
					string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "thumbs");
					try { Directory.CreateDirectory(d); } catch { }
					return d;
				}
			}

			private static string Small(string image)
			{
				try
				{
					foreach (string marker in new[] { "/api/1/file/", "/api/1/image/" })
					{
						int i = image.IndexOf(marker, StringComparison.Ordinal);
						if (i < 0) continue;
						string[] parts = image.Substring(i + marker.Length).Split('/');
						if (parts.Length < 2 || !parts[0].StartsWith("file_", StringComparison.Ordinal)) continue;
						return image.Substring(0, i) + "/api/1/image/" + parts[0] + "/" + parts[1] + "/256";
					}
				}
				catch { }
				return image;
			}

			private string CleanTitle(string title)
			{
				if (string.IsNullOrEmpty(title)) return title;
				title = title.Trim();
				// World OG titles read "Name by Author"; users are a plain display name.
				if (Kind == "world")
				{
					int at = title.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
					if (at > 0) title = title.Substring(0, at).Trim();
				}
				const string tail = " - an avatar on VRChat";
				int t = title.IndexOf(tail, StringComparison.OrdinalIgnoreCase);
				if (t > 0) title = title.Substring(0, t);
				return title;
			}

			private static string Str(JsonElement e, string prop)
				=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

			private static string Short(string id)
			{
				if (string.IsNullOrEmpty(id)) return "";
				int i = id.IndexOf('_');
				string tail = i >= 0 ? id.Substring(i + 1) : id;
				return tail.Length > 8 ? tail.Substring(0, 8) : tail;
			}

			// --- transform search (scoped to a subtree, never a whole-scene scan) ---
			private static bool IsActive(Transform t)
			{
				try { return !Dead(t) && t.gameObject.activeInHierarchy; } catch { return false; }
			}

			// Find our submenu inside the cached main-menu subtree (includes inactive), by name-contains.
			// Scoped and run once per world (the caller caches the result), so no per-frame scan.
			private static Transform FindInSubtree(Transform root, string contains)
			{
				try
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (n.IndexOf(contains, StringComparison.Ordinal) >= 0) return t;
					}
				}
				catch { }
				return null;
			}

			private static Transform FindDescendantByName(Transform root, string name)
			{
				try
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (n == name) return t;
					}
				}
				catch { }
				return null;
			}

			private static Transform FindDescendantByPrefix(Transform root, string prefix, string requireChild)
			{
				try
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (!n.StartsWith(prefix, StringComparison.Ordinal)) continue;
						if (!string.IsNullOrEmpty(requireChild) && Dead(t.Find(requireChild))) continue;
						return t;
					}
				}
				catch { }
				return null;
			}

			private static TMPro.TMP_Text FindText(Transform root, string path)
			{
				try { var t = root.Find(path); return Dead(t) ? null : t.GetComponent<TMPro.TMP_Text>(); }
				catch { return null; }
			}
		}
	}
}
