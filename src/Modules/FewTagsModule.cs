using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FewTags integration: downloads the community tag database published by Fewdys
	// (github.com/Fewdys/FewTags) and renders each tagged user's tags above their
	// nameplate, exactly where the original FewTags mod puts them.
	//
	// Rendering follows the FewTags technique — clone the nameplate's own "quickStats"
	// popup (it carries a correctly-styled background + "Trust Text" TMP label), keep
	// only the label, and stack one clone per tag above the plate. Cloning a native
	// object means the plates inherit VRChat's fonts/materials and billboard for free.
	//
	// Every game-side lookup (PlayerManager → Player → VRCPlayer → PlayerNameplate)
	// goes through reflection with by-name *and* by-type fallbacks, so a member rename
	// in a future build degrades to "tags stop appearing", never a crash.
	public class FewTagsModule : IModule
	{
		public override string Name => "FewTags";

		// Plates start above the nameplate and step up. Base height + spacing are live-tunable
		// (ModConfig.FewTagsBaseY / FewTagsSpacing) so positions can be corrected in-game.
		private const float BigTextY = 344.75f;
		private const string CloneName = "ArchiveFewTagPlate";
		private static bool _loggedFirstBuild;

		private const string HeaderStr = "<b><color=#ff0000>-</color> <color=#ff7f00>F</color><color=#ffff00>e</color><color=#80ff00>w</color><color=#00ff00>T</color><color=#00ff80>a</color><color=#00ffff>g</color><color=#0000ff>s</color> <color=#8b00ff>-</color></b>";
		private const string MaliciousStr = "<b><color=#ff0000>Malicious User</color></b>";

		private sealed class TagRecord
		{
			public string Uid;
			public string[] Tags;
			public string BigText;
			public string Size;
			public bool Malicious;
			public bool BigTextActive;
		}

		private sealed class Applied
		{
			public int DbVersion;
			public int Lines;                                   // plate rows this user currently occupies
			public readonly List<GameObject> Clones = new List<GameObject>();
		}

		// How many nameplate rows FewTags is currently using for this user. VaTags reads this so
		// its own plates stack ABOVE them — the two systems used to draw from separate fixed
		// baselines and landed on top of each other.
		public static int LinesFor(string uid)
		{
			try
			{
				if (Instance == null || string.IsNullOrEmpty(uid)) return 0;
				return Instance._applied.TryGetValue(uid, out Applied a) ? a.Lines : 0;
			}
			catch { return 0; }
		}

		private static FewTagsModule Instance;

		// --- database state ---
		private static readonly HttpClient Http = CreateClient();
		private Dictionary<string, TagRecord> _db = new Dictionary<string, TagRecord>(StringComparer.OrdinalIgnoreCase);
		private volatile Dictionary<string, TagRecord> _pendingDb;
		private int _dbVersion;
		private float _nextFetchAt;
		private bool _fetching;

		// --- per-player state ---
		private readonly Dictionary<string, Applied> _applied = new Dictionary<string, Applied>(StringComparer.OrdinalIgnoreCase);
		private int _frame;

		// --- live stats for the menu ---
		public static int RecordsLoaded { get; private set; }
		public static int TaggedHere { get; private set; }
		public static string LastFetchInfo { get; private set; } = "not fetched yet";

		public override void OnInitialize()
		{
			Instance = this;
			if (!ModConfig.FewTagsEnabled.Value)
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[FewTags] disabled by config.");
				return;
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[FewTags] armed — community tag DB by Fewdys, plates via nameplate quickStats clone.");
		}

		public override void OnUpdate()
		{
			if (!ModConfig.FewTagsEnabled.Value)
			{
				if (_applied.Count > 0) RemoveAllPlates();
				return;
			}

			// Adopt a freshly-parsed database (fetched on a worker thread).
			var pending = _pendingDb;
			if (pending != null)
			{
				_pendingDb = null;
				_db = pending;
				_dbVersion++;
				RecordsLoaded = _db.Count;
				VRChatArchiveModPlugin.Logger.LogInfo($"[FewTags] database applied: {_db.Count} active record(s).");
			}

			// Periodic refresh + the menu's "Update DB now" button.
			float now = Time.realtimeSinceStartup;
			if (Menu.ConsumeFewTagsRefreshRequest()) _nextFetchAt = 0f;
			if (now >= _nextFetchAt)
			{
				int minutes = Mathf.Max(1, ModConfig.FewTagsUpdateMinutes.Value);
				_nextFetchAt = now + minutes * 60f;
				StartFetch();
			}

			// Throttled apply pass.
			if (++_frame < 60) return;
			_frame = 0;
			ApplyPass();
		}

		public override void OnShutdown()
		{
			RemoveAllPlates();
		}

		// ---------------------------------------------------------------- database

		private static HttpClient CreateClient()
		{
			var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			c.DefaultRequestHeaders.Add("User-Agent", "VRChatArchiveMod-FewTags/1.0");
			return c;
		}

		private void StartFetch()
		{
			if (_fetching) return;
			_fetching = true;
			string url = ModConfig.FewTagsDbUrl.Value;
			Task.Run(async () =>
			{
				try
				{
					string raw = await Http.GetStringAsync(url);
					var parsed = ParseDb(raw);
					_pendingDb = parsed; // adopted on the main thread
					LastFetchInfo = $"{parsed.Count} records @ {DateTime.Now:HH:mm:ss}";
				}
				catch (Exception e)
				{
					LastFetchInfo = "fetch failed: " + e.Message;
					VRChatArchiveModPlugin.Logger.LogWarning($"[FewTags] DB fetch failed: {e.Message}");
				}
				finally
				{
					_fetching = false;
				}
			});
		}

		// Accepts both shapes FewTags has used: a bare array, or {"records":[...]}.
		private static Dictionary<string, TagRecord> ParseDb(string raw)
		{
			var db = new Dictionary<string, TagRecord>(StringComparer.OrdinalIgnoreCase);
			using (JsonDocument doc = JsonDocument.Parse(raw))
			{
				JsonElement records;
				JsonElement root = doc.RootElement;
				if (root.ValueKind == JsonValueKind.Array) records = root;
				else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var r) && r.ValueKind == JsonValueKind.Array) records = r;
				else return db;

				foreach (JsonElement rec in records.EnumerateArray())
				{
					if (rec.ValueKind != JsonValueKind.Object) continue;
					if (!ReadBool(rec, "Active", true)) continue;
					string uid = ReadString(rec, "UserID");
					if (string.IsNullOrEmpty(uid)) continue;

					var tags = new List<string>();
					if (rec.TryGetProperty("Tag", out var tagArr) && tagArr.ValueKind == JsonValueKind.Array)
					{
						foreach (var t in tagArr.EnumerateArray())
						{
							if (t.ValueKind == JsonValueKind.String)
							{
								string s = t.GetString();
								if (!string.IsNullOrWhiteSpace(s)) tags.Add(s);
							}
						}
					}

					db[uid] = new TagRecord
					{
						Uid = uid,
						Tags = tags.ToArray(),
						BigText = ReadString(rec, "PlateBigText"),
						Size = ReadString(rec, "Size"),
						Malicious = ReadBool(rec, "Malicious", false),
						BigTextActive = ReadBool(rec, "BigTextActive", false),
					};
				}
			}
			return db;
		}

		private static string ReadString(JsonElement el, string name)
		{
			if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
			return "";
		}

		private static bool ReadBool(JsonElement el, string name, bool fallback)
		{
			if (!el.TryGetProperty(name, out var v)) return fallback;
			switch (v.ValueKind)
			{
				case JsonValueKind.True: return true;
				case JsonValueKind.False: return false;
				case JsonValueKind.String: return string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase);
				case JsonValueKind.Number: return v.TryGetInt32(out int n) && n != 0;
				default: return fallback;
			}
		}

		// ---------------------------------------------------------------- apply pass

		private void ApplyPass()
		{
			if (_db.Count == 0) { TaggedHere = 0; return; }

			int taggedHere = 0;
			try
			{
				foreach (object player in EnumeratePlayers())
				{
					if (player == null) continue;
					string uid = UserIdOf(player);
					if (string.IsNullOrEmpty(uid)) continue;
					if (!_db.TryGetValue(uid, out TagRecord rec)) continue;

					taggedHere++;
					if (_applied.TryGetValue(uid, out Applied a) && a.DbVersion == _dbVersion && CloneAlive(a)) continue;

					BuildPlates(player, uid, rec);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[FewTags] apply pass threw: {e}");
			}
			TaggedHere = taggedHere;
			PruneDead();
		}

		// A rebuilt nameplate (avatar change, instance travel) destroys our clones with it.
		private static bool CloneAlive(Applied a)
		{
			if (a.Clones.Count == 0) return false;
			try { return a.Clones[0] != null; } catch { return false; }
		}

		private void PruneDead()
		{
			List<string> dead = null;
			foreach (var kv in _applied)
			{
				if (CloneAlive(kv.Value)) continue;
				(dead ??= new List<string>()).Add(kv.Key);
			}
			if (dead == null) return;
			foreach (string uid in dead) _applied.Remove(uid);
		}

		private void RemoveAllPlates()
		{
			foreach (var a in _applied.Values)
			{
				foreach (GameObject go in a.Clones)
				{
					try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
				}
			}
			_applied.Clear();
			TaggedHere = 0;
		}

		private void BuildPlates(object player, string uid, TagRecord rec)
		{
			// Tear down whatever we previously built for this user.
			if (_applied.TryGetValue(uid, out Applied old))
			{
				foreach (GameObject go in old.Clones)
				{
					try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
				}
				_applied.Remove(uid);
			}

			if (!ResolveNameplate(player, out GameObject quickStats, out Transform contents))
			{
				if (!_loggedFirstBuild)
				{
					_loggedFirstBuild = true;
					VRChatArchiveModPlugin.Logger.LogWarning($"[FewTags] could not resolve nameplate for a tagged user ({rec.Tags.Length} tag(s)) — quickStats/contents not found.");
				}
				return;
			}

			float baseY = EffectiveBaseY();
			float spacing = EffectiveSpacing();

			var applied = new Applied { DbVersion = _dbVersion };

			// Bottom-up stack: header plate first, then one plate per tag.
			int line = 0;
			if (ModConfig.FewTagsShowHeader.Value)
			{
				GameObject header = MakePlate(quickStats, contents, baseY + line * spacing,
					rec.Malicious ? MaliciousStr : HeaderStr);
				if (header != null) { applied.Clones.Add(header); line++; }
			}

			int max = Mathf.Clamp(ModConfig.FewTagsMaxTagsPerUser.Value, 1, 10);
			for (int i = 0; i < rec.Tags.Length && i < max; i++)
			{
				string tag = rec.Tags[i];
				if (string.IsNullOrWhiteSpace(tag)) continue;
				if (IsAbusive(tag)) continue;   // third-party DB, anyone can write to it
				GameObject plate = MakePlate(quickStats, contents, baseY + line * spacing, tag);
				if (plate != null) { applied.Clones.Add(plate); line++; }
			}

			if (ModConfig.FewTagsShowBigPlates.Value && rec.BigTextActive && !string.IsNullOrWhiteSpace(rec.BigText))
			{
				GameObject big = MakePlate(quickStats, contents, BigTextY, (rec.Size ?? "") + rec.BigText, hideBackground: true);
				if (big != null) applied.Clones.Add(big);
			}

			if (applied.Clones.Count > 0)
			{
				applied.Lines = line;
				_applied[uid] = applied;
				if (!_loggedFirstBuild)
				{
					_loggedFirstBuild = true;
					VRChatArchiveModPlugin.Logger.LogInfo($"[FewTags] rendered {applied.Clones.Count} plate(s) for a tagged user (baseY={baseY}, spacing={spacing}).");
				}
			}
		}

		// Clones the nameplate's quickStats popup, keeps only its "Trust Text" label and
		// turns it into one floating tag line.
		// The FewTags database is third party and open to anyone, so it carries slurs aimed at real
		// people. This filters what THIS client draws above someone's head — it does not and cannot
		// change the database. Matching is done on a letter-only, lowercase form so that spacing,
		// punctuation and l33t-speak padding cannot walk a slur straight past the check.
		private static readonly string[] Abusive =
		{
			"nigger", "nigga", "faggot", "tranny", "kike", "chink", "retard", "coon", "spic",
		};

		private static bool IsAbusive(string tag)
		{
			try
			{
				if (!ModConfig.FewTagsFilterSlurs.Value || string.IsNullOrEmpty(tag)) return false;

				var sb = new System.Text.StringBuilder(tag.Length);
				foreach (char c in tag)
				{
					char l = char.ToLowerInvariant(c);
					if (l >= 'a' && l <= 'z') sb.Append(l);
					else if (l == '0') sb.Append('o');
					else if (l == '1' || l == '!') sb.Append('i');
					else if (l == '3') sb.Append('e');
					else if (l == '4' || l == '@') sb.Append('a');
					else if (l == '5' || l == '$') sb.Append('s');
					else if (l == '7') sb.Append('t');
				}
				string flat = sb.ToString();
				if (flat.Length == 0) return false;

				foreach (string bad in Abusive)
					if (flat.IndexOf(bad, StringComparison.Ordinal) >= 0) return true;
			}
			catch { }
			return false;
		}

		// The label ResolveNameplate anchored on, remembered so MakePlate does not have to hunt
		// again — and, most importantly, so it does not fall back to a hardcoded "Trust Text".
		// "root/child/leaf" — used to reject anchors nested under a Group / Icon / Bubble sub-module.
		private static string ChainName(Transform t)
		{
			var sb = new System.Text.StringBuilder();
			int n = 0;
			while (t != null && n++ < 8) { if (sb.Length > 0) sb.Insert(0, '/'); sb.Insert(0, t.name); t = t.parent; }
			return sb.ToString();
		}

		private static bool _makePlateFailLogged;
		private static bool _plateShapeLogged;

		private static int Depth(Transform t, Transform root) { int d=0; while (t!=null && t!=root && d<10) { d++; t=t.parent; } return d; }


		private static bool Log(string msg) { try { VRChatArchiveModPlugin.Logger.LogWarning(msg); } catch { } return true; }

		internal static string LastAnchorName { get; private set; } = "Trust Text";

		// Which nameplate layout this build uses. The Fragments layout (ExpandedInfo) has much
		// taller plates, so it needs its own spacing — 78 in the reference against 28 for the old
		// Quick Stats layout. Using one number for both is what stacked every tag on top of the
		// next and over the player's name.
		internal static bool IsExpandedInfo { get; private set; }

		// The font size of the label we clone, read off the real nameplate. Spacing has to clear
		// the text, and the text is whatever THIS build's layout uses — on the Fragments layout the
		// label we anchor to is a good deal taller than the old Quick Stats one, which is why a
		// fixed 78 still left the rows sitting on top of each other.
		internal static float AnchorFontSize { get; private set; }

		// Where the FIRST plate sits. Same reasoning as the spacing: the Fragments plate is taller,
		// so the legacy height started the stack inside the nameplate rather than above it.
		internal static float EffectiveBaseY()
		{
			try
			{
				return IsExpandedInfo
					? ModConfig.FewTagsBaseYExpanded.Value
					: ModConfig.FewTagsBaseY.Value;
			}
			catch { return 205f; }
		}

		internal static float EffectiveSpacing()
		{
			try
			{
				float cfg = IsExpandedInfo
					? ModConfig.FewTagsSpacingExpanded.Value
					: ModConfig.FewTagsSpacing.Value;
				// SELF-CALIBRATING FLOOR. A line has to be at least ~1.7x its own font size clear of
				// the next one or the glyphs collide. Measuring beats guessing: the number comes
				// from the label actually on screen, so a future layout change corrects itself.
				float floor = AnchorFontSize > 0f ? AnchorFontSize * 1.7f : 0f;
				return Mathf.Max(cfg, floor);
			}
			catch { return 78f; }
		}

		// Nameplate furniture that must not ride along on a tag plate — the reference's
		// ObjectsToDestroy (Util/Utils.cs L16).
		private static readonly string[] PlateJunk =
		{
			"Trust Icon", "Performance Icon", "Performance Text", "Friend Anchor Stats", "Reason",
			"Shared Connections Icon", "Shared Connections Text", "Spacing", "Earmuffs Icon",
			"Age Verification Icon", "Performance Rank Icon", "Group Icon",
		};

		// The TMP label MakePlate last wrote to. Callers that need to animate the text must use
		// THIS rather than searching the clone again: the anchor name is build-dependent
		// ("Trust Text" on the old layout, "GroupName" on the current one) and the branches around
		// it are deactivated, so a re-search matched nothing and every tag silently lost its
		// animation. Set on every successful build, read immediately after the call.
		internal static TextMeshProUGUI LastPlateLabel { get; private set; }

		internal static GameObject MakePlate(GameObject quickStats, Transform contents, float y, string richText, bool hideBackground = false)
		{
			try
			{
				// STAY IN THE MODEL'S COORDINATE FRAME.
				//
				// `y` here is measured in the old plate's own local units — the value that used to
				// stack tags neatly above the nameplate. Parenting the clone to a DIFFERENT
				// transform (the positioner instead of the old contents container) changes the
				// coordinate frame, so "y = 91" starts meaning something like "91 metres" and the
				// plate ends up somewhere off-screen — invisible, but still constructed. Exactly
				// what you see.
				//
				// So the clone keeps the model's OWN local position, rotation AND scale, and then
				// we only add "y" on top — a nudge in the same units the model itself was placed
				// in, which is what the old code was really doing without saying so.
				GameObject clone = UnityEngine.Object.Instantiate(quickStats, contents);
				if (clone == null) return null;
				clone.name = CloneName;

				// ABSOLUTE, x = 0 — the reference's exact line (Plate.cs L107/L118):
				//     _gameObject.transform.localPosition = new Vector3(0f, position, 0);
				// Adding the model's own localPosition on top (what this did before) double-counts
				// the offset the panel already carries and pushes the plate off to one side.
				clone.transform.localPosition = new Vector3(0f, y, 0f);

				// THE CANVASGROUP IS WHY NOTHING SHOWED. VRChat fades the nameplate with a
				// CanvasGroup; the clone inherits it, complete with whatever alpha the original had
				// at that instant — frequently 0. The plate is then built, positioned and textured
				// perfectly, and drawn fully transparent. DestroyChildren in the reference
				// (Util/Utils.cs L653-660) disables it for exactly this reason.
				try
				{
					var cg = clone.GetComponent<CanvasGroup>();
					if (cg != null) cg.enabled = false;
				}
				catch { }

				// The group pill is the visible body of the plate on the ExpandedInfo layout, and it
				// ships inactive. The reference switches it on by name.
				try
				{
					Transform pill = FindDeep(clone.transform, "GroupPill");
					if (pill != null) pill.gameObject.SetActive(true);
				}
				catch { }

				// Icons that belong to the real nameplate and mean nothing on a tag plate. Left in
				// place they draw over the text and widen the row. Names from the reference's
				// ObjectsToDestroy list.
				try
				{
					foreach (string junk in PlateJunk)
					{
						Transform j = FindDeep(clone.transform, junk);
						if (j != null) j.gameObject.SetActive(false);
					}
				}
				catch { }

				// LOOK FOR THE ANCHOR WE ACTUALLY RESOLVED, then any TMP as last resort. This used
				// to search hardcoded "Trust Text" — and, on a build that renamed it, quietly
				// destroyed every clone: quickStats/contents came back fine, the plate was cloned,
				// then Destroy(clone); return null. "resolve-failed=0, plates=0" was the visible
				// result: not a resolve failure, a build failure with no message.
				Transform trust = FindDeep(clone.transform, LastAnchorName);
				if (trust == null)
				{
					try { trust = clone.GetComponentInChildren<TextMeshProUGUI>(true)?.transform; } catch { }
				}
				if (trust == null)
				{
					_makePlateFailLogged = _makePlateFailLogged || Log("[FewTags] MakePlate: no label under the cloned plate.");
					LastPlateLabel = null;
					UnityEngine.Object.Destroy(clone);
					return null;
				}

				// Keep only the branch that carries the label.
				for (int i = 0; i < clone.transform.childCount; i++)
				{
					Transform child = clone.transform.GetChild(i);
					bool keep = child == trust || trust.IsChildOf(child);
					if (!keep) child.gameObject.SetActive(false);
				}
				trust.gameObject.SetActive(true);

				var tmp = trust.GetComponent<TextMeshProUGUI>();
				if (tmp == null)
				{
					UnityEngine.Object.Destroy(clone);
					return null;
				}
				// SetTextSafe, from the reference (Util/Utils.cs L334-344). Each of these can make a
				// correctly-built label render as nothing:
				//   autoSizing on a rect this small shrinks the text toward zero;
				//   an overflow mode of Truncate/Ellipsis clips it away entirely.
				tmp.richText = true;
				tmp.enableAutoSizing = false;
				tmp.overflowMode = TextOverflowModes.Overflow;
				tmp.horizontalAlignment = HorizontalAlignmentOptions.Center;
				// CENTRING, the reference's way (Util/Utils.cs L346-355). A HorizontalLayoutGroup two
				// levels up drives the row's alignment; without this the label keeps the nameplate's
				// own left-ish anchoring and the stack looks ragged instead of centred over the head.
				try
				{
					var gp = tmp.transform.parent != null ? tmp.transform.parent.parent : null;
					var lg = gp != null ? gp.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>() : null;
					if (lg != null) lg.childAlignment = TextAnchor.MiddleCenter;
				}
				catch { }
				tmp.text = richText;
				// Draw over the world rather than being occluded by it.
				try { tmp.isOverlay = true; } catch { }
				LastPlateLabel = tmp;

				if (hideBackground)
				{
					// Big plates render as free text: disable every graphic that isn't the label.
					var graphics = clone.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
					for (int i = 0; i < graphics.Length; i++)
					{
						var g = graphics[i];
						if (g == null) continue;
						if (g.transform == trust) continue;
						g.enabled = false;
					}
				}

				clone.SetActive(true);
				return clone;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[FewTags] plate build failed: {e.Message}");
				return null;
			}
		}

		internal static Transform FindDeep(Transform root, string name)
		{
			if (root == null) return null;
			try
			{
				if (root.name == name) return root;
				for (int i = 0; i < root.childCount; i++)
				{
					Transform hit = FindDeep(root.GetChild(i), name);
					if (hit != null) return hit;
				}
			}
			catch { }
			return null;
		}

		// ---------------------------------------------------------------- game access (reflection)

		private static Type _playerType;
		private static Il2CppSystem.Type _nameplateIl2cppType;
		private static Il2CppSystem.Type _playerIl2cppType;
		private static MethodInfo _tryCastPlayer;
		private static bool _typesResolved;

		private static void ResolveTypes()
		{
			if (_typesResolved) return;
			_typesResolved = true;
			try
			{
				var asm = Assembly.Load("Assembly-CSharp");
				_playerType = asm.GetType("VRC.Player");
				if (_playerType != null)
				{
					_playerIl2cppType = Il2CppInterop.Runtime.Il2CppType.From(_playerType);
					_tryCastPlayer = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
						.GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)
						?.MakeGenericMethod(_playerType);
				}

				// The nameplate is no longer a named member of VRCPlayer on this build; the type still
				// exists, so it is reached as a COMPONENT in the player's hierarchy instead.
				// THE NAMESPACE IS A GUESS, so try the plausible ones and accept that all may miss.
				// Nothing depends on this any more — ResolveNameplate walks to "Trust Text" from the
				// player when the type is unavailable — but when it does resolve it saves the walk.
				foreach (string cand in new[] { "PlayerNameplate", "VRC.UI.PlayerNameplate", "VRC.PlayerNameplate" })
				{
					var npType = asm.GetType(cand);
					if (npType == null) continue;
					_nameplateIl2cppType = Il2CppInterop.Runtime.Il2CppType.From(npType);
					break;
				}
				if (_nameplateIl2cppType == null)
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[FewTags] PlayerNameplate type not resolvable by name on this build — "
						+ "plates will be anchored structurally instead.");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[FewTags] type resolve failed: {e.Message}");
			}
			if (_playerType == null || _tryCastPlayer == null)
				VRChatArchiveModPlugin.Logger.LogWarning("[FewTags] VRC.Player type unavailable — tags cannot render this build.");
		}

		// Enumerate players via the SDK-stable VRCPlayerApi.AllPlayers (same source as ESP),
		// then map each to its VRC.Player component so UserIdOf/ResolveNameplate can read the
		// APIUser id and the nameplate. No dependency on the obfuscated PlayerManager, which
		// this build renamed out of the VRC namespace.
		// internal: shared with VaTagsModule (players, user ids, nameplates, reflection kit).
		// CACHED, one frame at a time.
		//
		// Three modules call this every apply pass — InstancePanels, VaTags, and FewTags's own —
		// and each call did, per player: three GetComponent lookups then a reflection Invoke
		// through Il2CppObjectBase.TryCast. On a full instance the profiler measured
		// InstancePanels=724 ms/s, VaTags=467 ms/s, FewTags=60 ms/s — a full second of the game's
		// time each second spent recomputing the same list.
		//
		// The result does not change between the modules' passes within one frame, so this hands
		// back the cached list unless the frame has moved on.
		private static readonly List<object> _playerCache = new List<object>(64);
		private static int _playerCacheFrame = -1;

		internal static IEnumerable<object> EnumeratePlayers()
		{
			if (_playerCacheFrame == Time.frameCount) return _playerCache;
			_playerCacheFrame = Time.frameCount;
			_playerCache.Clear();

			ResolveTypes();
			if (_playerIl2cppType == null) return _playerCache;

			var players = VRCPlayerApi.AllPlayers;
			if (players == null) return _playerCache;
			int count;
			try { count = players.Count; } catch { return _playerCache; }

			for (int i = 0; i < count; i++)
			{
				try
				{
					VRCPlayerApi api = players[i];
					if (api == null) continue;
					GameObject go = api.gameObject;
					if (go == null) continue;

					Component comp = go.GetComponent(_playerIl2cppType);
					if (comp == null)
					{
						// Only walk the hierarchy when the direct hit failed. The 3-lookup fallback
						// was paying the price EVERY player: two extra il2cpp queries multiplied by
						// forty is what put VaTags at 467 ms/s.
						comp = go.GetComponentInChildren(_playerIl2cppType, true)
							?? go.GetComponentInParent(_playerIl2cppType);
						if (comp == null) continue;
					}

					// TryCast, NOT reflection.Invoke: same result, without the method-lookup and
					// argument-boxing cost that made this the biggest single item in the profile.
					var b = comp as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
					object player = b != null ? _tryCastPlayerDirect(b) : (object)comp;
					if (player != null) _playerCache.Add(player);
				}
				catch { }
			}
			return _playerCache;
		}

		// TryCast<T>() with the target type only known at runtime. Built once from _playerType and
		// invoked as a plain delegate; the overhead is a virtcall, not a full reflection Invoke.
		private static Func<Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, object> _tryCastPlayerDirect =
			b => { try { return _tryCastPlayer?.Invoke(b, null); } catch { return null; } };

		internal static string UserIdOf(object player)
		{
			try
			{
				object apiUser = GetMemberByTypeName(player, "APIUser", "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
				if (apiUser == null) return null;
				object id = GetMember(apiUser, "id");
				return id as string;
			}
			catch { return null; }
		}

		// player → VRCPlayer → PlayerNameplate → (quickStats template, contents parent).
		// WHERE IT GAVE UP, not just that it did.
		//
		// This returns false from four different places and said which from none of them, so
		// "nameplate-resolve-failed=1" could mean the player object was wrong, the nameplate member
		// moved with a game update, or the fallback could not find "Trust Text". Those need
		// completely different fixes and the log could not tell them apart — which is how a
		// regression here turned into a guessing game.
		//
		// One line, once per distinct reason, then silent.
		internal static string LastResolveFailure { get; private set; } = "";
		private static string _loggedFailure = "";

		private static bool Failed(string why)
		{
			LastResolveFailure = why;
			if (_loggedFailure != why)
			{
				_loggedFailure = why;
				try { VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] resolve failed: " + why); } catch { }
			}
			return false;
		}

		// Every label the trust text has been called. "Trust Text" is the classic one; the rest are
		// what a Fragment-based nameplate is likely to use. Order matters only for speed.
		// The plate's OWN label — not a bolted-on sub-module. Live log said the fallback picked
		// "GroupName" (the little group pill), which cloned a pill and parented it wrong; the plates
		// went out of sight because their MODEL was wrong, not their position. So the label search
		// prefers actual player-name / rank labels first, and everything with "Group" in it is
		// explicitly avoided at the end.
		private static readonly string[] AnchorNames =
		{
			// what the plate is really called on this build (Fragments layout, seen 2026-08-28)
			"Text_PlayerName", "PlayerNameText", "Text_Name", "NameText",
			// classic FewTags landmark
			"Trust Text", "TrustText", "Text_TrustRank", "Trust", "Text_Trust",
			// legacy shapes
			"Text_Status", "StatusText",
		};
		// Labels that BELONG to something else and must not be chosen as the plate anchor.
		private static readonly string[] AnchorDeny =
		{
			"Group",   // GroupName, GroupPill, etc.
			"Icon",
			"Bubble",
		};

		// ONE DUMP, THE FIRST TIME IT MATTERS.
		//
		// A rename can only be fixed by knowing the new name, and nothing in the mod could see the
		// nameplate's children. This writes them out once — bounded, so a deep hierarchy cannot turn
		// a diagnostic into a freeze — and then never again for the session.
		private static bool _dumpedSubtree;

		private static void DumpPlayerSubtreeOnce(Transform root)
		{
			if (_dumpedSubtree) return;

			// NOT WHILE YOU ARE ALONE. This fired on the first failure, which is always in the first
			// seconds of a session — before anyone else has joined. It then printed "players in the
			// instance: 1", marked itself done, and stayed silent for the rest of the session, so a
			// world with 29 people in it was never looked at. Exactly the same one-shot mistake the
			// VaTags summary line had.
			//
			// A probe for OTHER players' plates is worthless with no other players, so it waits.
			try
			{
				int here = 0;
				foreach (var e in VaTagsModule.Roster) if (e != null) here++;
				if (here < 2) return;   // stay armed; try again once somebody is around
			}
			catch { }

			_dumpedSubtree = true;
			try
			{
				// THE NAMEPLATE IS NOT UNDER THE PLAYER ANY MORE. The first dump of this proved it:
				// the player's subtree is the avatar rig and nothing else — Armature, Hips, Spine,
				// the mirror and shadow clones — and 400 nodes of bones with no plate in sight. So
				// walking down from the player can never find it, whatever the anchor is called.
				//
				// This looks for it where it actually lives instead: every object in the scene whose
				// name mentions a nameplate, with its path and its labels. One scene-wide scan, once
				// per session, on a path that is already broken — the cost buys the only thing that
				// can unblock this.
				var sb = new System.Text.StringBuilder(8192);
				sb.Append("[Nameplate] scene-wide probe — where nameplates actually live:\n");

				// SEARCH BY THE TEXT, NOT BY THE OBJECT NAME.
				//
				// The first scene-wide pass looked for objects called *Nameplate* and found only the
				// SETTINGS menu — "Nameplate Opacity", "Nameplate Scale". No player plate is named
				// that any more, so the name is useless as a search key.
				//
				// A plate is identifiable by what it SAYS: it carries a label holding a player's
				// display name. That survives every rename, and it is the same property the plates
				// are cloned from. So collect the display names in the instance and find whatever
				// label is showing one.
				var names = new List<string>();
				try
				{
					foreach (var e in VaTagsModule.Roster)
						if (e != null && !string.IsNullOrWhiteSpace(e.Name)) names.Add(e.Name);
				}
				catch { }
				sb.Append("  players in the instance: ").Append(names.Count);
				if (names.Count <= 1) sb.Append("  <-- ALONE: there may simply be no other plate to find");
				sb.Append('\n');

				int found = 0;
				foreach (var tmp in Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>())
				{
					if (tmp == null || found >= 8) continue;
					string txt;
					try { txt = tmp.text ?? ""; } catch { continue; }
					if (txt.Length == 0 || txt.Length > 64) continue;

					bool isName = false;
					for (int i = 0; i < names.Count; i++)
						if (txt.IndexOf(names[i], StringComparison.Ordinal) >= 0) { isName = true; break; }
					if (!isName) continue;

					Transform t;
					try { t = tmp.transform; } catch { continue; }
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
					// The plate is the label's ancestry, so climb a little to show the whole card.
					for (int up = 0; up < 3 && t.parent != null; up++) t = t.parent;

					found++;
					sb.Append("\n--- ").Append(PathOf(t)).Append("  (active=");
					try { sb.Append(t.gameObject.activeInHierarchy); } catch { sb.Append('?'); }
					sb.Append(")\n");
					int c = 0;
					Walk(t, 1, sb, ref c);
				}

				if (found == 0) sb.Append("  nothing in the scene is named *Nameplate*.\n");
				VRChatArchiveModPlugin.Logger.LogWarning(sb.ToString());
			}
			catch (Exception e)
			{
				try { VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] probe failed: " + e.Message); } catch { }
			}
		}

		private static string PathOf(Transform t)
		{
			try
			{
				var parts = new List<string>();
				for (Transform c = t; c != null && parts.Count < 12; c = c.parent) parts.Add(c.name);
				parts.Reverse();
				return string.Join("/", parts);
			}
			catch { return "?"; }
		}

		private static void Walk(Transform t, int depth, System.Text.StringBuilder sb, ref int n)
		{
			if (t == null || n >= 120 || depth > 6) return;
			n++;
			sb.Append(' ', depth * 2).Append(t.name);

			// The text is what identifies the anchor, so any label carries its content.
			try
			{
				var tmp = t.GetComponent<TMPro.TMP_Text>();
				if (tmp != null)
				{
					string txt = tmp.text ?? "";
					if (txt.Length > 24) txt = txt.Substring(0, 24) + "…";
					sb.Append("   [TMP: \"").Append(txt).Append("\"]");
				}
			}
			catch { }

			sb.Append('\n');
			for (int i = 0; i < t.childCount && n < 400; i++) Walk(t.GetChild(i), depth + 1, sb, ref n);
		}

		internal static bool ResolveNameplate(object player, out GameObject quickStats, out Transform contents)
		{
			quickStats = null;
			contents = null;
			try
			{
				if (player == null) return Failed("the player object was null");

				// FewTags-Rewrite V2 recipe. Verified on THIS VRChat build by reading the
				// reference source: the field is "NameplateContainer", a GameObject exposed on
				// VRCPlayer under some obfuscated name — no lookup by type name required, we scan
				// the VRCPlayer's GameObject fields and take the one whose object is called
				// "NameplateContainer".
				GameObject container = FindNameplateContainer(player);
				if (container == null)
				{
					// Last resort so an unknown build still gets some diagnostic instead of silence.
					GameObject pgo = AsGameObject(player)
						?? AsGameObject(GetMemberByTypeName(player, "VRCPlayer", "_vrcplayer",
							"prop_VRCPlayer_0", "field_Private_VRCPlayer_0"));
					if (pgo == null) return Failed("the player object has no GameObject behind it");
					DumpPlayerSubtreeOnce(pgo.transform);
					return Failed("no NameplateContainer field on VRCPlayer — subtree dumped");
				}
				Transform from = container.transform;

				// EXACT PATHS AND PARENTING FROM FewTags-Rewrite V2/Plate/Plate.cs L67-89. The
				// author was explicit: GetNameplateContainer returns the HOST, not the thing to
				// clone. What gets cloned is the Quick Stats / ExpandedInfo PANEL, and the clone is
				// parented to that PANEL'S OWN PARENT — a sibling of it — not to the container and
				// not to the panel itself. Parenting to the container (my previous mistake) is what
				// put the plate in the wrong coordinate frame and off-screen.
				const string PATH_OLD = "PlayerNameplate/Canvas/NameplateGroup/Nameplate/Contents/Quick Stats";
				const string PATH_NEW = "PlayerNameplate/Canvas/NameplateGroup/NameplateFragment/ExpandedInfo";

				// Reference order: Quick Stats first, ExpandedInfo only if that is null.
				Transform panel = from.Find(PATH_OLD);
				IsExpandedInfo = false;
				if (panel == null)
				{
					panel = from.Find(PATH_NEW);
					IsExpandedInfo = panel != null;
				}
				if (panel == null)
				{
					DumpPlayerSubtreeOnce(from);
					return Failed("neither Quick Stats nor ExpandedInfo exists under NameplateContainer — subtree dumped");
				}
				Transform parent = panel.parent;
				if (parent == null)
				{
					DumpPlayerSubtreeOnce(from);
					return Failed("the plate panel has no parent — subtree dumped");
				}

				quickStats = panel.gameObject;   // the MODEL to Instantiate
				contents = parent;               // the PARENT to hang the clone from (panel.parent)

				// Give MakePlate an anchor name to search for inside the clone. The container
				// panel always carries a TMP label (that's what identifies it as a plate); grab
				// the first one and remember its name so MakePlate does not fall back to a
				// hard-coded "Trust Text" that this build does not have.
				try
				{
					var lbl = panel.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (lbl != null && !string.IsNullOrEmpty(lbl.name))
					{
						LastAnchorName = lbl.name;
						try { AnchorFontSize = lbl.fontSize; } catch { }
					}
				}
				catch { }

				if (!_plateShapeLogged)
				{
					_plateShapeLogged = true;
					try
					{
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[Nameplate] container='NameplateContainer' | panel='" + panel.name
							+ "' | anchor='" + LastAnchorName + "' fontSize=" + AnchorFontSize.ToString("0.#")
							+ " | spacing=" + EffectiveSpacing().ToString("0.#"));
					}
					catch { }
				}

				LastResolveFailure = "";
				_loggedFailure = "";
				return true;
			}
			catch (Exception e) { return Failed("threw: " + e.Message); }
		}

		// From FewTags-Rewrite V2/Util/Utils.cs. VRCPlayer holds a set of GameObject fields under
		// obfuscated names; one of them is the nameplate host, identified by its OBJECT NAME
		// "NameplateContainer" — the only property that survives il2cpp renames between builds.
		//
		// THE KEY DETAIL: the reference uses vrcplayer.GetIl2CppType().GetFields(), the IL2CPP
		// class API — NOT managed reflection. On a proxy, GetType().GetFields() lists the WRAPPER's
		// members (NativeFieldInfoPtr_*, Pointer, ...), never the game's real fields, so the search
		// found nothing and reported "no NameplateContainer field". Enumerating the native fields
		// with il2cpp_class_get_fields is what actually walks VRCPlayer's own layout.
		private static bool _fieldsDumped;

		private static GameObject FindNameplateContainer(object player)
		{
			try
			{
				object vrcObj = GetMemberByTypeName(player, "VRCPlayer",
					"_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
				if (vrcObj == null) vrcObj = player;

				var vp = vrcObj as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (vp == null || vp.Pointer == IntPtr.Zero) return null;

				IntPtr basePtr = vp.Pointer;
				var seen = _fieldsDumped ? null : new List<string>();

				// PROPERTIES AND METHODS FIRST — the FewTags author's own note: the nameplate host is
				// exposed as a member whose RETURN/PROPERTY type is GameObject, not a plain field.
				// Il2CppInterop generates the game's real members as C# properties/methods on the
				// wrapper type, so managed reflection over the WRAPPER (unlike GetFields, which only
				// showed wrapper plumbing) does see them.
				Type wt = vrcObj.GetType();
				foreach (var pi in wt.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (pi.PropertyType == null || pi.PropertyType.Name != "GameObject") continue;
					GameObject go;
					try { go = pi.GetValue(vrcObj) as GameObject; } catch { continue; }
					if (go == null) continue;
					string n; try { n = go.name; } catch { continue; }
					if (seen != null) seen.Add("prop:" + n);
					if (n == "NameplateContainer") return go;
				}
				foreach (var mi in wt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (mi.ReturnType == null || mi.ReturnType.Name != "GameObject") continue;
					if (mi.GetParameters().Length != 0) continue;   // getters only
					GameObject go;
					try { go = mi.Invoke(vrcObj, null) as GameObject; } catch { continue; }
					if (go == null) continue;
					string n; try { n = go.name; } catch { continue; }
					if (seen != null) seen.Add("method:" + n);
					if (n == "NameplateContainer") return go;
				}

				// Walk the class AND ITS PARENTS: il2cpp_class_get_fields only returns fields
				// declared on the exact class, so a field on a base type would be invisible with a
				// single-class walk. Climb via il2cpp_class_get_parent.
				IntPtr klass = IL2CPP.il2cpp_object_get_class(basePtr);
				while (klass != IntPtr.Zero)
				{
					IntPtr iter = IntPtr.Zero;
					while (true)
					{
						IntPtr field = IL2CPP.il2cpp_class_get_fields(klass, ref iter);
						if (field == IntPtr.Zero) break;

						IntPtr ftype = IL2CPP.il2cpp_field_get_type(field);
						IntPtr fclass = ftype != IntPtr.Zero ? IL2CPP.il2cpp_class_from_type(ftype) : IntPtr.Zero;
						if (fclass == IntPtr.Zero) continue;
						string cn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(fclass));
						if (cn != "GameObject") continue;

						GameObject go = ReadGameObjectField(basePtr, field);
						if (go == null) continue;
						string n;
						try { n = go.name; } catch { continue; }

						if (seen != null) seen.Add(n);
						if (n == "NameplateContainer") return go;
					}
					klass = IL2CPP.il2cpp_class_get_parent(klass);
				}

				// FIRST FAILURE ONLY: print every GameObject field name we found, so the real name
				// of the nameplate host can be read straight off the log instead of guessed.
				if (seen != null)
				{
					_fieldsDumped = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[Nameplate] VRCPlayer GameObject fields seen: "
						+ (seen.Count == 0 ? "(none)" : string.Join(", ", seen)));
				}
			}
			catch { }
			return null;
		}

		// Reads one instance field as a GameObject reference. The field's storage is a pointer to
		// the il2cpp object; we pool it back into a managed GameObject wrapper. Anything that is not
		// actually a live GameObject is rejected by the wrapper / the name read above.
		private static unsafe GameObject ReadGameObjectField(IntPtr instance, IntPtr field)
		{
			try
			{
				uint off = IL2CPP.il2cpp_field_get_offset(field);
				if (off == 0) return null;
				IntPtr slot = (IntPtr)((byte*)instance + off);
				IntPtr obj = *(IntPtr*)slot;
				if (obj == IntPtr.Zero) return null;
				// Only bother if it is a Unity Object; GameObject.name will throw otherwise and be
				// swallowed by the caller.
				return new GameObject(obj);
			}
			catch { return null; }
		}

		// `is` AND `as` LIE ABOUT IL2CPP PROXIES.
		//
		// The managed type of a proxy is not its runtime type, so `o is GameObject` is false for an
		// object that IS a GameObject, and the whole method fell through to null. Everything
		// downstream then read that as "the nameplate has no quickStats", took the structural
		// fallback, and — since that used `as Component`, the same trap — failed there too. The
		// result was a nameplate that resolves perfectly well reported as unresolvable.
		//
		// TryCast asks il2cpp, which is the only authority on what a proxy actually is. The plain
		// casts stay as a fallback for genuine managed objects.
		// THE NAMEPLATE, VIA THE POSITIONER — which is where the game actually keeps it.
		//
		// A metadata reflection over the interop said it plainly: VRCPlayer no longer exposes a
		// PlayerNameplate at all. What it exposes is
		//     VRCPlayer.field_Public_PlayerNameplatePositioner_0  ->  PlayerNameplatePositioner
		// and the positioner IS the plate's Component (its .gameObject is the plate root). Every
		// previous version of this method searched the player's SUBTREE for a PlayerNameplate — but
		// the nameplate was not in the subtree, so the search could only ever fail, whatever the
		// anchor was called ("Trust Text" or otherwise). Two dumps proved it: the player's tree is
		// just the avatar rig, and a scene-wide sweep for *Nameplate* found only the Settings menu.
		//
		// So: reach the positioner by its typed member, cast through TryCast (`as` lies on il2cpp
		// proxies — the mistake that ate this feature for a day), and return it as a Component.
		private static Component FindNameplate(object player)
		{
			try
			{
				// The positioner is on VRCPlayer, not on VRC.Player. Both wrappers appear in this
				// code base; the field lives on the former.
				object vrcplayer = GetMemberByTypeName(player, "VRCPlayer",
					"_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
				if (vrcplayer == null) vrcplayer = player;   // sometimes the player object IS a VRCPlayer

				object positioner = GetMemberByTypeName(vrcplayer, "PlayerNameplatePositioner",
					"field_Public_PlayerNameplatePositioner_0", "prop_PlayerNameplatePositioner_0",
					"playerNameplatePositioner", "nameplatePositioner");
				if (positioner == null) return null;

				var b = positioner as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				var comp = b != null ? b.TryCast<Component>() : positioner as Component;
				return comp;
			}
			catch { return null; }
		}

		private static GameObject AsGameObject(object o)
		{
			if (o == null) return null;
			try
			{
				var b = o as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (b != null)
				{
					var go = b.TryCast<GameObject>();
					if (go != null) return go;
					var tr = b.TryCast<Transform>();
					if (tr != null) return tr.gameObject;
					var cp = b.TryCast<Component>();
					if (cp != null) return cp.gameObject;
				}
			}
			catch { }

			if (o is GameObject g) return g;
			if (o is Transform t) return t.gameObject;
			if (o is Component c) { try { return c.gameObject; } catch { return null; } }
			return null;
		}

		// --- tiny reflection kit (IL2CPP interop exposes game fields as properties) ---

		private static readonly Dictionary<string, MemberInfo> MemberCache = new Dictionary<string, MemberInfo>();

		internal static object GetMember(object obj, string name)
		{
			if (obj == null) return null;
			Type t = obj.GetType();
			string key = t.FullName + "::" + name;
			if (!MemberCache.TryGetValue(key, out MemberInfo mi))
			{
				mi = (MemberInfo)t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
					?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				MemberCache[key] = mi;
			}
			return ReadMember(obj, mi);
		}

		// First tries the given member names, then any property/field whose type name matches.
		internal static object GetMemberByTypeName(object obj, string typeName, params string[] names)
		{
			if (obj == null) return null;
			foreach (string n in names)
			{
				object v = GetMember(obj, n);
				if (v != null) return v;
			}
			Type t = obj.GetType();
			string key = t.FullName + "::bytype::" + typeName;
			if (!MemberCache.TryGetValue(key, out MemberInfo mi))
			{
				mi = FindByTypeName(t, typeName, exactPrefix: false);
				MemberCache[key] = mi;
			}
			return ReadMember(obj, mi);
		}

		private static object GetMemberByTypeNamePrefix(object obj, string typePrefix, params string[] names)
		{
			if (obj == null) return null;
			foreach (string n in names)
			{
				object v = GetMember(obj, n);
				if (v != null) return v;
			}
			Type t = obj.GetType();
			string key = t.FullName + "::byprefix::" + typePrefix;
			if (!MemberCache.TryGetValue(key, out MemberInfo mi))
			{
				mi = FindByTypeName(t, typePrefix, exactPrefix: true);
				MemberCache[key] = mi;
			}
			return ReadMember(obj, mi);
		}

		private static MemberInfo FindByTypeName(Type t, string typeName, bool exactPrefix)
		{
			try
			{
				foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					string n = p.PropertyType.Name;
					if (exactPrefix ? n.StartsWith(typeName, StringComparison.Ordinal) : n == typeName) return p;
				}
				foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					string n = f.FieldType.Name;
					if (exactPrefix ? n.StartsWith(typeName, StringComparison.Ordinal) : n == typeName) return f;
				}
			}
			catch { }
			return null;
		}

		private static object GetStaticByTypeName(Type t, string typeName, params string[] names)
		{
			foreach (string n in names)
			{
				try
				{
					PropertyInfo p = t.GetProperty(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
					object v = p?.GetValue(null);
					if (v != null) return v;
				}
				catch { }
			}
			try
			{
				foreach (PropertyInfo p in t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (p.PropertyType.Name != typeName) continue;
					object v = p.GetValue(null);
					if (v != null) return v;
				}
			}
			catch { }
			return null;
		}

		// Exposed because the same danger exists for any direct member access on a game object, not
		// just the reflected ones: a dead proxy has to be turned into null before it is touched.
		internal static T AliveOrNull<T>(T o) where T : class => Core.NativeGuard.Alive(o) ? o : null;

		private static object ReadMember(object obj, MemberInfo mi)
		{
			// The guard has to come BEFORE the read, not around it: reading a field off a proxy whose
			// native object is not there is an AccessViolation, and .NET 6 ends the process on those
			// rather than raising something the catch below could see.
			if (mi == null || !Core.NativeGuard.Alive(obj)) return null;
			try
			{
				if (mi is PropertyInfo p) return p.GetValue(obj);
				if (mi is FieldInfo f) return f.GetValue(obj);
			}
			catch { }
			return null;
		}
	}
}
