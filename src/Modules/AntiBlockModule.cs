using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Anti-block: when someone blocks you, VRChat hides them from your view. The block itself
	// is untouched — you stay blocked, nothing networked changes — but the person is revealed
	// LOCALLY so you can still see them. Same nature as ESP/Radar: it only flips local
	// visibility on players your client already tracks.
	//
	// Three rules learned the hard way (see the review notes on the first version):
	//   1. A deactivated player is NOT proof of a block. Players are deactivated while their
	//      avatar loads, while out of range, and when YOU block someone. We therefore only
	//      flag someone we have positively seen active before, and only after a confirmation
	//      delay while they are still in the instance.
	//   2. We never blanket-activate a subtree. Turning on every descendant switches on things
	//      the avatar deliberately keeps off (toggles, VFX, alternate outfits) and fights the
	//      avatar's own animators several times a second. We snapshot exactly what VRChat
	//      switched off at the moment of the hide, and restore only that.
	//   3. Everything we change is recorded and put back — on unflag, on disable, on shutdown.
	public class AntiBlockModule : IModule
	{
		public override string Name => "AntiBlock";

		private const float ScanInterval = 0.25f;
		private const float ConfirmSeconds = 1.5f;   // hidden this long, while still present = a block

		private float _lastScan;

		// Per player: what we turned back on, so it can be undone exactly.
		private sealed class Revealed
		{
			public string Name;
			public GameObject Root;
			public bool RootWasActive;
			public readonly List<GameObject> Objects = new List<GameObject>();
			public readonly List<Renderer> Renderers = new List<Renderer>();
		}
		private readonly Dictionary<int, Revealed> _revealed = new Dictionary<int, Revealed>();

		// Display names of the people this module had to un-hide, i.e. the ones whose avatar
		// VRChat was suppressing for us \u2014 which is what being blocked looks like from this side.
		// Published so the ESP can label them; a HashSet because the ESP reads it every frame per
		// player and the roster is rebuilt constantly.
		private static readonly HashSet<string> Blockers = new HashSet<string>(StringComparer.Ordinal);
		private static readonly object BlockGate = new object();

		// True when `name` is someone we detected as blocking us.
		//
		// This is INFERRED, not reported: VRChat never tells a client it has been blocked. All we
		// know is that their avatar was force-hidden for us after we had already seen it active,
		// which is the signature of a block \u2014 but a heavy avatar-hiding safety setting can look
		// the same. Worth saying plainly wherever this is shown to the user.
		public static bool HasBlockedYou(string name)
		{
			if (string.IsNullOrEmpty(name)) return false;
			lock (BlockGate) return Blockers.Contains(name);
		}

		public static int BlockerCount { get { lock (BlockGate) return Blockers.Count; } }

		// Players seen active at least once (candidates for a real "was visible, now hidden").
		private readonly HashSet<int> _seenActive = new HashSet<int>();
		// Hidden since when — a hide must persist to count as a block.
		private readonly Dictionary<int, float> _hiddenSince = new Dictionary<int, float>();
		private readonly HashSet<string> _notified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private string _noticeName;
		private float _noticeUntil;
		private GUIStyle _banner;
		private bool _wasEnabled;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.AntiBlockEnabled.Value)
				{
					if (_wasEnabled) { RestoreAll(); _wasEnabled = false; }
					return;
				}
				_wasEnabled = true;

				if (Time.realtimeSinceStartup - _lastScan < ScanInterval) return;
				_lastScan = Time.realtimeSinceStartup;
				Scan();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[AntiBlock] update threw: {e}"); }
		}

		public override void OnShutdown() => RestoreAll();

		private void Scan()
		{
			var players = VRCPlayerApi.AllPlayers;
			if (players == null) return;
			int count;
			try { count = players.Count; } catch { return; }

			var present = new HashSet<int>();
			float now = Time.realtimeSinceStartup;

			for (int i = 0; i < count; i++)
			{
				try
				{
					VRCPlayerApi api = players[i];
					if (api == null || api.isLocal) continue;
					GameObject go = api.gameObject;
					if (go == null) continue;
					int id = go.GetInstanceID();
					present.Add(id);

					if (_revealed.ContainsKey(id))
					{
						// Already revealed: only re-assert the exact objects we turned on, and
						// only if VRChat switched them off again. No subtree sweeps.
						Reassert(_revealed[id]);
						continue;
					}

					if (go.activeSelf)
					{
						_seenActive.Add(id);
						_hiddenSince.Remove(id);
						continue;
					}

					// Hidden. Only a player we positively saw active before can be a blocker.
					if (!_seenActive.Contains(id)) continue;
					if (!_hiddenSince.TryGetValue(id, out float since)) { _hiddenSince[id] = now; continue; }
					if (now - since < ConfirmSeconds) continue;      // still might be an avatar load

					Reveal(api, go, id);
					_hiddenSince.Remove(id);
				}
				catch { }
			}

			// Anyone who left the instance: forget them, and drop bookkeeping for dead objects.
			Prune(present);
		}

		// Snapshot what is off right now, turn exactly that on, and remember it.
		private void Reveal(VRCPlayerApi api, GameObject root, int id)
		{
			var rec = new Revealed { Name = SafeName(api), Root = root, RootWasActive = root.activeSelf };
			try
			{
				var trs = root.GetComponentsInChildren<Transform>(true);
				if (trs != null)
					for (int i = 0; i < trs.Length; i++)
					{
						var t = trs[i];
						if (t == null || t.gameObject == root) continue;
						if (!t.gameObject.activeSelf) rec.Objects.Add(t.gameObject);
					}
				var rends = root.GetComponentsInChildren<Renderer>(true);
				if (rends != null)
					for (int i = 0; i < rends.Length; i++)
						if (rends[i] != null && !rends[i].enabled) rec.Renderers.Add(rends[i]);
			}
			catch { }

			try { root.SetActive(true); } catch { }
			for (int i = 0; i < rec.Objects.Count; i++) { try { if (rec.Objects[i] != null) rec.Objects[i].SetActive(true); } catch { } }
			for (int i = 0; i < rec.Renderers.Count; i++) { try { if (rec.Renderers[i] != null) rec.Renderers[i].enabled = true; } catch { } }

			_revealed[id] = rec;
			if (!string.IsNullOrEmpty(rec.Name)) { lock (BlockGate) Blockers.Add(rec.Name); }
			if (_notified.Add(rec.Name))
			{
				_noticeName = rec.Name;
				_noticeUntil = Time.realtimeSinceStartup + 6f;
				VRChatArchiveModPlugin.Logger.LogInfo($"[AntiBlock] revealed hidden player: {rec.Name}.");
			}
		}

		// Re-apply ONLY the snapshot, never a fresh subtree sweep.
		private static void Reassert(Revealed rec)
		{
			try
			{
				if (rec.Root != null && !rec.Root.activeSelf) rec.Root.SetActive(true);
				for (int i = 0; i < rec.Objects.Count; i++)
				{
					var g = rec.Objects[i];
					if (g != null && !g.activeSelf) g.SetActive(true);
				}
				for (int i = 0; i < rec.Renderers.Count; i++)
				{
					var r = rec.Renderers[i];
					if (r != null && !r.enabled) r.enabled = true;
				}
			}
			catch { }
		}

		private void Prune(HashSet<int> present)
		{
			List<int> gone = null;
			foreach (var kv in _revealed)
				if (!present.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
			if (gone != null)
				foreach (int id in gone) { Undo(id); }

			if (_seenActive.Count > 0)
			{
				List<int> stale = null;
				foreach (int id in _seenActive) if (!present.Contains(id)) (stale ??= new List<int>()).Add(id);
				if (stale != null) foreach (int id in stale) { _seenActive.Remove(id); _hiddenSince.Remove(id); }
			}
		}

		// Put a player's visibility back exactly as VRChat had it.
		private void Undo(int id)
		{
			if (!_revealed.TryGetValue(id, out Revealed rec)) return;
			_revealed.Remove(id);
			try
			{
				for (int i = 0; i < rec.Renderers.Count; i++) { try { if (rec.Renderers[i] != null) rec.Renderers[i].enabled = false; } catch { } }
				for (int i = 0; i < rec.Objects.Count; i++) { try { if (rec.Objects[i] != null) rec.Objects[i].SetActive(false); } catch { } }
				if (rec.Root != null && !rec.RootWasActive) rec.Root.SetActive(false);
			}
			catch { }
		}

		private void RestoreAll()
		{
			var keys = new List<int>(_revealed.Keys);
			foreach (int id in keys) Undo(id);
			_revealed.Clear();
			_seenActive.Clear();
			_hiddenSince.Clear();
			_notified.Clear();
		}

		// A new instance means every id and every judgement is stale.
		public override void OnSceneLoaded(int buildIndex) => RestoreAll();

		public override void OnGui()
		{
			try
			{
				if (!ModConfig.AntiBlockEnabled.Value) return;
				if (Event.current.type != EventType.Repaint) return;
				if (Time.realtimeSinceStartup > _noticeUntil || string.IsNullOrEmpty(_noticeName)) return;

				if (_banner == null)
					_banner = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleCenter };

				float w = 460f, h = 44f;
				var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.20f, w, h);
				GuiKit.Fill(r, new Color(0.06f, 0.03f, 0.02f, 0.92f));
				GuiKit.Fill(new Rect(r.x, r.y, r.width, 2f), new Color(1f, 0.55f, 0.2f, 1f));
				GuiKit.Fill(new Rect(r.x, r.yMax - 2f, r.width, 2f), new Color(1f, 0.55f, 0.2f, 1f));
				_banner.normal.textColor = new Color(1f, 0.72f, 0.35f);
				GUI.Label(r, $"{_noticeName} hid from you  —  kept visible", _banner);
				GUI.color = Color.white;
			}
			catch { }
		}

		private static string SafeName(VRCPlayerApi api)
		{
			try { return api.displayName ?? "?"; } catch { return "?"; }
		}
	}
}
