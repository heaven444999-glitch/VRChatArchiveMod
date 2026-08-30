using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// THE CONTROL CHANNEL — the desktop client drives the mod.
	//
	// The mod's own TAB menu is being retired: an IMGUI panel drawn inside VRChat can never be as
	// good as a real desktop window, and every setting already lives in a BepInEx ConfigEntry that
	// something re-reads every frame. So the client becomes the place you change things, and this is
	// the wire between them.
	//
	// DIRECTION MATTERS. The desktop app is the SERVER (LocalBridge listens on 127.0.0.1:8791) and
	// the mod is an HTTP CLIENT — the game process opens no port of its own. So control cannot be
	// pushed to us: we POLL, the same way the soundboard already does. Each sync sends what the mod
	// currently is (settings + who is in the instance) and receives whatever the client wants
	// changed since last time.
	//
	// ITS OWN MODULE, deliberately. VaAuth.Poll used to be driven from VaTagsModule.OnUpdate, below
	// an early-out on VaTagsEnabled — bolting control onto that would mean switching VA tags off
	// silently killed the client's control of the mod.
	public class ModControlModule : IModule
	{
		public override string Name => "ModControl";

		public static string Status = "idle";
		public static bool Linked;              // the client answered our last sync
		private static int _applied;            // settings changed by the client this session

		private float _next;
		private long _seq;                      // highest sequence seen, for reporting only
		private int _failed;                    // commands from the client we could not apply

		// Bounded memory of what has actually been applied. 256 covers far more than one poll can
		// ever carry, and unlike a high-water mark it does not care if the numbering restarts.
		private readonly System.Collections.Generic.HashSet<long> _recent = new System.Collections.Generic.HashSet<long>();
		private readonly System.Collections.Generic.Queue<long> _recentOrder = new System.Collections.Generic.Queue<long>();

		private void Remember(long seq)
		{
			if (seq == 0 || !_recent.Add(seq)) return;
			_recentOrder.Enqueue(seq);
			while (_recentOrder.Count > 256) _recent.Remove(_recentOrder.Dequeue());
		}
		private bool _schemaSent;
		private bool _busy;

		// ------------------------------------------------------------------ main-thread work
		//
		// WHERE A COMMAND ARRIVES IS NOT WHERE IT CAN RUN. Apply() executes in the continuation of an
		// awaited HTTP call, and this process has no Unity synchronisation context — so that
		// continuation is a thread-pool thread. The Udon manager cannot live there: a scan is
		// Resources.FindObjectsOfTypeAll over every loaded object, and running an event calls straight
		// into the world's script. Unity refuses both off its main thread, and not always by throwing
		// something a catch can see.
		//
		// So a Udon command does not act when it arrives. It queues here, and OnUpdate — which IS the
		// main thread — runs it on the next frame.
		private static readonly Queue<Action> MainWork = new Queue<Action>();

		private static void OnMain(Action work)
		{
			if (work == null) return;
			lock (MainWork)
			{
				// A backstop, not a policy: nothing here should ever queue 64 deep, and a queue that grew
				// without bound would mean a frame that never comes. Said out loud rather than dropped
				// quietly — the client clears its own queue the moment it hands a command over, so a
				// silent drop here would be the last trace the request ever existed.
				if (MainWork.Count >= 64)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[ModControl] main-thread queue full, command dropped.");
					return;
				}
				MainWork.Enqueue(work);
			}
		}

		private static void PumpMain()
		{
			while (true)
			{
				Action work;
				lock (MainWork)
				{
					if (MainWork.Count == 0) return;
					work = MainWork.Dequeue();
				}
				try { work(); }
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ModControl] queued command failed: " + e.Message); }
			}
		}

		// THE UDON PAGE'S DATA, always built on the main thread and held until a sync takes it.
		//
		// It deliberately does NOT ride every sync. The list only changes when the client asks for
		// something — a scan, a switch, a restore — and a world can hold a thousand behaviours, so
		// sending it once a second would be a hundred kilobytes a second describing something that did
		// not move. Built once when it changes, taken once by the next sync.
		private static string _udonPayload;

		// THE ENTRY POINTS OF THE LAST BEHAVIOUR ASKED ABOUT, kept rather than consumed. Clearing
		// these once they had been sent looked tidier and lost the answer: click a row (which asks
		// for its events), flip a switch before the next sync, and the switch's own rebuild
		// overwrote the payload with one that no longer carried them — leaving the client's detail
		// panel waiting for an answer it had already been given. They are small, the client keys
		// them by id, and re-sending the same list costs nothing.
		private static int _udonEventsId;
		private static List<string> _udonEventList;
		private static int _udonVarsId;
		private static List<UdonManagerModule.Var> _udonVarList;
		// The last "run this event and tell me what changed" result, kept like the events/vars blocks.
		private static int _udonDiffId;
		private static string _udonDiffEvent;
		private static List<UdonManagerModule.VarChange> _udonDiffList;
		private static float _worldWatch;
		private static string _lastWorldId = "";
		private const int MaxUdonRows = 1500;

		// ADAPTIVE. A flat one-second poll meant a switch could take two seconds to take effect —
		// queued just after one sync, applied on the next. The client raises a "hot" flag while
		// somebody is actually using a mod page, and we answer five times a second for as long as
		// that lasts. Idle, it costs exactly what it did before.
		private const float IdleSec = 1f;
		private const float HotSec = 0.2f;
		private const float RepollSec = 0.03f;   // when the client long-polls, just re-ask; the client waits
		private float _interval = IdleSec;

		// A CHANGE MADE IN GAME MUST NOT WAIT FOR THE NEXT POLL.
		//
		// "hot" only ever came from the CLIENT: it raises the flag while someone is using its window,
		// and the mod then polls five times a second. Nothing did the mirror image. So flipping a
		// tile in VRChat — where the client is by definition idle — sat until the next one-second
		// poll before the client heard about it, which is the direction that felt broken.
		//
		// BepInEx raises SettingChanged on every REAL change, so there is nothing to poll for: the
		// event arms an immediate sync. Subscribing costs one handler; hashing every setting each
		// frame to notice the same thing would cost far more.
		private static bool _dirty;
		private bool _subscribed;

		private void SubscribeOnce()
		{
			if (_subscribed) return;
			_subscribed = true;
			try
			{
				var file = ModConfig.EspEnabled?.ConfigFile;   // any entry: they all share one file
				if (file != null) file.SettingChanged += (_, __) => _dirty = true;
			}
			catch { }
		}

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.ModControlEnabled.Value) return;

				// Anything the client asked for that has to touch Unity runs HERE, on the frame — not
				// on the socket continuation that received it. Ahead of the link check on purpose, so
				// work already accepted still completes if the client goes away mid-flight.
				PumpMain();

				if (!VaAuth.InsideClient) { Linked = false; Status = "waiting for the desktop client"; return; }

				SubscribeOnce();

				float now = Time.realtimeSinceStartup;

				// THE WORLD ID ARRIVES LATE, and something has to notice.
				//
				// On a scene change the mod pushes an empty Udon list straight away, but RoomManager
				// does not know the new world for another second or two — so that push carries no
				// world id, and a client waiting to apply a per-world profile would wait for ever,
				// because nothing else pushes until somebody presses a button. Watching costs almost
				// nothing (CurrentInstance caches for a second of its own) and this only rebuilds the
				// payload when the answer actually changed.
				if (now >= _worldWatch)
				{
					_worldWatch = now + 2f;
					string wid = "";
					try { wid = VaTagsModule.CurrentInstance().WorldId ?? ""; } catch { }
					if (wid != _lastWorldId)
					{
						_lastWorldId = wid;
						_udonPayload = BuildUdon();
					}
				}

				// A pending change jumps the queue rather than waiting out the interval.
				if (_dirty && !_busy) { _dirty = false; _next = 0f; }
				if (now < _next || _busy) return;
				_next = now + _interval;
				_busy = true;
				_ = SyncAsync();
			}
			catch (Exception e) { Status = "failed: " + e.Message; }
		}

		// A world change destroys every Udon behaviour we knew about, and UdonManagerModule clears
		// its own list on the same event. Rebuilding the payload through the queue rather than here
		// means it is built on the NEXT frame — after every module has seen the scene change — so it
		// cannot depend on which of us was registered first.
		public override void OnSceneLoaded(int buildIndex)
		{
			_schemaSent = false;
			OnMain(() =>
			{
				_udonEventList = null;   // the behaviour they belonged to no longer exists
				_udonVarList = null;
				_udonPayload = BuildUdon();
			});
		}

		private async System.Threading.Tasks.Task SyncAsync()
		{
			try
			{
				// The schema goes once per session: it is the list of every setting with its type, so
				// the client can build real controls instead of guessing. Values ride on every sync.
				if (!_schemaSent)
				{
					var (okS, _, _) = await VaAuth.PostBridgeAsync("/mod/schema", BuildSchema());
					if (okS) _schemaSent = true;
				}

				var (ok, raw, code) = await VaAuth.PostBridgeAsync("/mod/sync", BuildSync());
				if (!ok)
				{
					Linked = false;
					Status = "client did not answer (" + code + ")";
					// An old client has no /mod route at all. Re-announcing the schema on the next
					// success is cheap, and stops a stale schema from outliving a client restart.
					_schemaSent = false;
					return;
				}

				Linked = true;
				Apply(raw);

				// If the client long-polls (lp:true), IT does the waiting: it holds the request open
				// until a toggle is flipped, so a flip comes back the instant it happens. We just poll
				// again promptly and let the client's hold set the real pace - an idle poll blocks on
				// the client for ~0.8s rather than spinning. An older client that answers instantly has
				// no lp flag, so we fall back to the timer instead of hammering it.
				bool longPoll = raw != null && raw.IndexOf("\"lp\":true", StringComparison.Ordinal) >= 0;
				if (longPoll) _interval = RepollSec;
				else _interval = raw != null && raw.IndexOf("\"hot\":true", StringComparison.Ordinal) >= 0
					? HotSec : IdleSec;
			}
			catch (Exception e) { Linked = false; Status = "sync failed: " + e.Message; }
			finally { _busy = false; }
		}

		// ------------------------------------------------------------------ outgoing
		// SETTINGS THE CLIENT MUST NOT OFFER. Left out of the schema so no control is drawn, AND
		// refused on the way in, so an older client that still remembers one cannot set it either.
		// Two things land here. Values dictated by what the game actually contains, where a free
		// text box can only produce a typo that silently falls back to something else — and the
		// SIGNATURE lists: the user ids this mod announces on everyone else's client. Handing those
		// to the user turns "who am I told about" into "who do I get to follow", and lets them
		// delete the very announcement they are the audience for. Not theirs to edit.
		private static bool IsInternal(string section, string key)
			=> (string.Equals(section, "Favorites", StringComparison.OrdinalIgnoreCase)
			    && string.Equals(key, "CategoryToBorrow", StringComparison.OrdinalIgnoreCase))
			   // The whole Watchlist section: the ids watched, whether the watch runs at all, and
			   // the banner style that comes with it.
			   || string.Equals(section, "Watchlist", StringComparison.OrdinalIgnoreCase)
			   // The other half of the same signature: who gets the rainbow capsule.
			   || (string.Equals(section, "ESP", StringComparison.OrdinalIgnoreCase)
			       && string.Equals(key, "RainbowUsers", StringComparison.OrdinalIgnoreCase));

		private static string BuildSchema()
		{
			var sb = new StringBuilder(16 * 1024);
			sb.Append("{\"schema\":[");
			bool first = true;
			foreach (var e in DevToolsModule.Config())
			{
				if (e == null || e.Raw == null) continue;
				if (IsInternal(e.Section, e.Key)) continue;   // not offered to the client at all
				if (!first) sb.Append(',');
				first = false;
				sb.Append("{\"section\":").Append(Json(e.Section))
				  .Append(",\"key\":").Append(Json(e.Key))
				  .Append(",\"type\":").Append(Json(e.Type))
				  .Append(",\"desc\":").Append(Json(Describe(e)));

				// A numeric setting carries its range so the client can draw a slider with the same
				// bounds the mod enforces, rather than an unbounded text box.
				try
				{
					var range = e.Raw.Description?.AcceptableValues as BepInEx.Configuration.AcceptableValueBase;
					if (range != null) sb.Append(",\"range\":").Append(Json(range.ToDescriptionString()));
				}
				catch { }
				sb.Append('}');
			}
			sb.Append("]}");
			return sb.ToString();
		}

		private static string Describe(DevToolsModule.Entry e)
		{
			try { return e.Raw.Description?.Description ?? ""; } catch { return ""; }
		}

		private static string BuildSync()
		{
			var sb = new StringBuilder(16 * 1024);
			sb.Append("{\"values\":{");
			bool first = true;
			foreach (var e in DevToolsModule.Config())
			{
				if (e == null || e.Raw == null) continue;
				if (!first) sb.Append(',');
				first = false;
				sb.Append(Json(e.Section + "/" + e.Key)).Append(':').Append(Json(DevToolsModule.ValueOf(e)));
			}
			sb.Append('}');

			// WHAT EACH SETTING IS ACTUALLY DOING, beside what it is set to. Small — only the
			// features that have something to say appear — and it is the half that was missing every
			// time a toggle "did not work": the value was right and nothing said the effect was not.
			sb.Append(",\"health\":");
			Core.FeatureHealth.AppendJson(sb);

			sb.Append(",\"roster\":[");

			// WHO IS IN THE INSTANCE, with the avatar id read from the game itself. This is what lets
			// the client's Force Clone work off the mod instead of parsing logs: an id read from the
			// live player is exact, and it exists even for avatars no database has ever indexed.
			try
			{
				var roster = VaTagsModule.Roster;
				bool f2 = true;
				lock (roster)
				{
					foreach (var p in roster)
					{
						if (p == null) continue;
						if (!f2) sb.Append(',');
						f2 = false;
						sb.Append("{\"name\":").Append(Json(p.Name))
						  .Append(",\"userId\":").Append(Json(p.UserId))
						  .Append(",\"avatarId\":").Append(Json(p.AvatarId))
						  .Append(",\"avatarName\":").Append(Json(p.AvatarName))
						  .Append(",\"platform\":").Append(Json(p.Platform))
						  .Append(",\"release\":").Append(Json(ReleaseCached(p)))
						  .Append(",\"trust\":").Append(Json(p.TrustColor))
						  .Append(",\"plus\":").Append(p.Plus ? "true" : "false")
						  .Append(",\"adult\":").Append(p.Adult ? "true" : "false")
						  .Append(",\"playerId\":").Append(p.PlayerId)
						  .Append(",\"isLocal\":").Append(p.IsLocal ? "true" : "false")
						  .Append(",\"isOwner\":").Append(p.IsOwner ? "true" : "false")
						  .Append(",\"isMaster\":").Append(p.IsMaster ? "true" : "false");

						// The tags this player carries, so the client can show and edit them without
						// a second round trip to the server for data the mod already holds.
						sb.Append(",\"tags\":[");
						try
						{
							var tags = VaTagsModule.TagsOf(p.UserId);
							if (tags != null)
							{
								for (int t = 0; t < tags.Length; t++)
								{
									if (t > 0) sb.Append(',');
									var tg = tags[t];
									sb.Append("{\"text\":").Append(Json(tg.Text))
									  .Append(",\"color\":").Append(Json(tg.Color))
									  .Append(",\"b\":").Append(tg.B ? "true" : "false")
									  .Append(",\"i\":").Append(tg.I ? "true" : "false")
									  .Append(",\"u\":").Append(tg.U ? "true" : "false")
									  .Append(",\"fx\":").Append(Json(tg.Fx)).Append('}');
								}
							}
						}
						catch { }
						sb.Append("]}");
					}
				}
			}
			catch { }

			// Live state the client needs to draw its toggles in the right position — an orbit
			// switch that does not know it is already on is just a button that lies.
			string orbitMode = "off", orbitTarget = "";
			try
			{
				orbitMode = OrbitModule.Current.ToString().ToLowerInvariant();
				orbitTarget = OrbitModule.TargetUid ?? "";
			}
			catch { }

			// Same for the object ring, so the client's toggle shows the state that is really on.
			bool objOrbit = false; string objOrbitCenter = ""; int objOrbitCount = 0;
			try
			{
				objOrbit = ObjectOrbitModule.Active;
				objOrbitCenter = ObjectOrbitModule.CenterName ?? "";
				objOrbitCount = ObjectOrbitModule.Count;
			}
			catch { }

			sb.Append("],\"events\":[").Append(BuildEvents()).Append(']')
			  .Append(",\"results\":[").Append(TakeResults()).Append(']')
			  .Append(",\"seq\":").Append(0)
			  .Append(",\"applied\":").Append(_applied)
			  .Append(",\"world\":").Append(Json(SafeWorld()))
			  .Append(",\"orbitMode\":").Append(Json(orbitMode))
			  .Append(",\"orbitTarget\":").Append(Json(orbitTarget))
			  .Append(",\"objectOrbit\":").Append(objOrbit ? "true" : "false")
			  .Append(",\"objectOrbitCenter\":").Append(Json(objOrbitCenter))
			  .Append(",\"objectOrbitCount\":").Append(objOrbitCount)
			  // TAKEN, not copied: whoever gets this sync gets the Udon list, and the next sync carries
			  // null instead of repeating it. Interlocked because BuildSync runs on the main thread on
			  // an ordinary poll but on a pool thread when the schema POST above had to await first.
			  .Append(",\"udon\":").Append(System.Threading.Interlocked.Exchange(ref _udonPayload, null) ?? "null")
			  .Append('}');
			return sb.ToString();
		}

		// UDON AND NETWORK EVENTS, forwarded to the client's console.
		//
		// ONLY WHAT IS NEW. The mod keeps a rolling log; sending all of it once a second would push
		// the same hundreds of lines over and over and the console would show each one repeatedly.
		// A high-water mark on the event clock means each line crosses exactly once.
		private static float _lastEventAt;
		private const int MaxEventsPerSync = 60;

		// ANSWERS TO ONE-SHOT REQUESTS.
		//
		// An action normally just happens — wear this, teleport there. A metadata dump is different:
		// it has an ANSWER, and the answer has to get back across a channel where only the mod ever
		// speaks first. So results are queued here and ride out on the next sync, exactly like
		// events do.
		private static readonly Queue<string> Results = new Queue<string>();

		private static void QueueResult(string kind, string id, string json)
		{
			lock (Results)
			{
				Results.Enqueue("{\"kind\":" + Json(kind) + ",\"id\":" + Json(id) + ",\"json\":" + Json(json) + "}");
				while (Results.Count > 8) Results.Dequeue();   // nobody needs a backlog of dumps
			}
		}

		private static string TakeResults()
		{
			lock (Results)
			{
				if (Results.Count == 0) return "";
				var sb = new StringBuilder(4096);
				bool first = true;
				while (Results.Count > 0)
				{
					if (!first) sb.Append(',');
					first = false;
					sb.Append(Results.Dequeue());
				}
				return sb.ToString();
			}
		}

		// THE WORLD'S SCRIPTS, as the client's UDON MANAGER page draws them.
		//
		// Reads .enabled live off each behaviour rather than trusting what the last scan recorded:
		// a world switches its own scripts on and off constantly, and a row showing OFF because we
		// once saw it OFF is a row that lies. MUST run on the main thread — every caller queues.
		private static string BuildUdon()
		{
			var sb = new StringBuilder(16 * 1024);
			var rows = UdonManagerModule.Snapshot();

			// THE WORLD'S OWN ID, not the scene name the rest of the sync carries. The client keys
			// its per-world profiles on this: two worlds can ship a scene called the same thing, and
			// a profile that fires in the wrong world is worse than no profile at all.
			string worldId = "", worldName = "";
			try
			{
				var inst = VaTagsModule.CurrentInstance();
				worldId = inst.WorldId ?? "";
				worldName = inst.WorldName ?? "";
			}
			catch { }

			sb.Append("{\"status\":").Append(Json(UdonManagerModule.Status))
			  .Append(",\"worldId\":").Append(Json(worldId))
			  .Append(",\"worldName\":").Append(Json(worldName))
			  .Append(",\"total\":").Append(rows.Count)
			  .Append(",\"off\":").Append(UdonManagerModule.DisabledByUs)
			  .Append(",\"items\":[");

			int n = 0;
			for (int i = 0; i < rows.Count && n < MaxUdonRows; i++)
			{
				var e = rows[i];
				if (e == null || e.B == null) continue;
				bool on;
				try { on = e.B.enabled; } catch { continue; }   // destroyed since the scan

				// A broken world can leave a transform at NaN, and "NaN" is not a JSON number — it
				// would take the whole sync down with it rather than just this row.
				float d = e.Dist;
				if (float.IsNaN(d) || float.IsInfinity(d)) d = 0f;
				if (n > 0) sb.Append(',');
				n++;
				sb.Append("{\"id\":").Append(e.Id)
				  .Append(",\"n\":").Append(Json(e.Short))
				  .Append(",\"p\":").Append(Json(e.Path))
				  .Append(",\"d\":").Append(d.ToString("F1", CultureInfo.InvariantCulture))
				  .Append(",\"on\":").Append(on ? "true" : "false")
				  .Append(",\"ours\":").Append(e.OffByUs ? "true" : "false").Append('}');
			}
			// NO SILENT CAP. If a world holds more behaviours than one message should carry, the
			// client is told how many it is actually looking at rather than left to assume the list
			// is complete.
			sb.Append("],\"shown\":").Append(n);

			// The entry points of one behaviour, when the client asked for them. Read once per
			// selection and never during a scan: it costs a reflected call per behaviour.
			if (_udonEventList != null)
			{
				sb.Append(",\"events\":{\"id\":").Append(_udonEventsId).Append(",\"list\":[");
				for (int i = 0; i < _udonEventList.Count; i++)
				{
					if (i > 0) sb.Append(',');
					string ev = _udonEventList[i];
					// g = VRChat would let this one be networked. A '_' event never can be.
					sb.Append("{\"n\":").Append(Json(ev))
					  .Append(",\"g\":").Append(UdonManagerModule.IsGlobalEvent(null, ev) ? "true" : "false").Append('}');
				}
				sb.Append("]}");
			}

			// The variables of one behaviour, on the same terms as its events: only when asked for,
			// and kept afterwards so an unrelated rebuild cannot swallow the answer. canWrite is the
			// mod's own verdict after resolving the setter — the client draws no SET button without
			// it, because a button that silently does nothing is worse than none.
			if (_udonVarList != null)
			{
				sb.Append(",\"vars\":{\"id\":").Append(_udonVarsId)
				  .Append(",\"canWrite\":").Append(UdonManagerModule.CanWrite ? "true" : "false")
				  .Append(",\"list\":[");
				for (int i = 0; i < _udonVarList.Count; i++)
				{
					if (i > 0) sb.Append(',');
					var v = _udonVarList[i];
					sb.Append("{\"n\":").Append(Json(v.Name))
					  .Append(",\"t\":").Append(Json(v.Type))
					  .Append(",\"v\":").Append(Json(v.Value))
					  .Append(",\"e\":").Append(v.Editable ? "true" : "false").Append('}');
				}
				sb.Append("]}");
			}

			// WHAT THE LAST RUN CHANGED. Sent once after a udonRunDiff and kept like events/vars so a
			// later rebuild cannot swallow the answer; the client keys it by id+event.
			if (_udonDiffList != null)
			{
				sb.Append(",\"runDiff\":{\"id\":").Append(_udonDiffId)
				  .Append(",\"event\":").Append(Json(_udonDiffEvent ?? ""))
				  .Append(",\"changes\":[");
				for (int i = 0; i < _udonDiffList.Count; i++)
				{
					if (i > 0) sb.Append(',');
					var c = _udonDiffList[i];
					sb.Append("{\"n\":").Append(Json(c.Name))
					  .Append(",\"b\":").Append(Json(c.Before))
					  .Append(",\"a\":").Append(Json(c.After)).Append('}');
				}
				sb.Append("]}");
			}

			return sb.Append('}').ToString();
		}

        // EVERYTHING VRCHAT HOLDS about a player, read off the live APIUser. This is the record the
        // game itself is using, so it carries things no public page shows — the trust flags, the
        // platform, the current avatar's image — without a single API call.
		private static string DumpUser(VaTagsModule.PlayerEntry p)
		{
			var sb = new StringBuilder(2048).Append("{\n");
			void F(string k, string v) { sb.Append("  ").Append(Json(k)).Append(": ").Append(Json(v ?? "")).Append(",\n"); }

			F("displayName", p.Name);
			F("userId", p.UserId);
			F("avatarId", p.AvatarId);
			F("avatarName", p.AvatarName);
			F("platform", p.Platform);
			F("trustColor", p.TrustColor);
			sb.Append("  \"playerId\": ").Append(p.PlayerId).Append(",\n");
			sb.Append("  \"isLocal\": ").Append(p.IsLocal ? "true" : "false").Append(",\n");
			sb.Append("  \"isMaster\": ").Append(p.IsMaster ? "true" : "false").Append(",\n");
			sb.Append("  \"isOwner\": ").Append(p.IsOwner ? "true" : "false").Append(",\n");
			sb.Append("  \"vrcPlus\": ").Append(p.Plus ? "true" : "false").Append(",\n");
			sb.Append("  \"ageVerified\": ").Append(p.Adult ? "true" : "false").Append(",\n");

			try
			{
				object apiUser = FewTagsModule.GetMemberByTypeName(p.Player, "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
				var u = (apiUser as Il2CppSystem.Object)?.TryCast<VRC.Core.APIUser>();
				// Same dead-proxy rule as DumpAvatar: a property read on a non-live APIUser is an
				// access violation the try/catch cannot catch. Gate before reading anything.
				if (u != null && Core.NativeGuard.Alive(u))
				{
					try { F("bio", u.bio); } catch { }
					try { F("statusDescription", u.statusDescription); } catch { }
					try { F("status", u.status.ToString()); } catch { }
					try { F("currentAvatarImageUrl", u.currentAvatarImageUrl); } catch { }
					try { F("currentAvatarThumbnailImageUrl", u.currentAvatarThumbnailImageUrl); } catch { }
					try { F("profilePicOverride", u.profilePicOverride); } catch { }
					try { F("userIcon", u.userIcon); } catch { }
					try { F("last_platform", u.last_platform); } catch { }
					try
					{
						var tags = u.tags;
						sb.Append("  \"tags\": [");
						if (tags != null)
							for (int i = 0; i < tags.Count; i++)
							{
								if (i > 0) sb.Append(", ");
								sb.Append(Json(tags[i]));
							}
						sb.Append("],\n");
					}
					catch { }
				}
			}
			catch { }

			// The VA tags we hold for them, since the point of this dump is "everything known".
			try
			{
				var t = VaTagsModule.TagsOf(p.UserId);
				sb.Append("  \"vaTags\": [");
				if (t != null)
					for (int i = 0; i < t.Length; i++)
					{
						if (i > 0) sb.Append(", ");
						sb.Append(Json(t[i].Text));
					}
				sb.Append("]\n");
			}
			catch { sb.Append("  \"vaTags\": []\n"); }

			return sb.Append('}').ToString();
		}

		// The avatar record, from VRChat's own cache. Not fetched here: if the game has not loaded
		// it the honest answer is to say so rather than block the game on a web request.
		private static string DumpAvatar(string avatarId)
		{
			var sb = new StringBuilder(2048).Append("{\n");
			void F(string k, string v) { sb.Append("  ").Append(Json(k)).Append(": ").Append(Json(v ?? "")).Append(",\n"); }
			F("id", avatarId);
			try
			{
				var a = VRC.Core.API.FromCacheOrNew<VRC.Core.ApiAvatar>(avatarId);

				// A DEAD PROXY IS AN ACCESS VIOLATION, NOT AN EXCEPTION. Reading any property of an
				// ApiAvatar whose native object is not live crashes the whole process — the try/catch
				// around each read below cannot save it (see the dead-proxy note in the mod's memory).
				// So the object is checked for liveness BEFORE anything is read off it, and .Populated
				// itself is a read, so it must come after this gate too.
				if (a == null || !Core.NativeGuard.Alive(a))
				{
					sb.Append("  \"note\": \"private avatar - no metadata available\"\n");
					return sb.Append('}').ToString();
				}

				bool done = false; try { done = a.Populated; } catch { }
				if (!done)
				{
					sb.Append("  \"note\": \"private avatar - no metadata available\"\n");
					return sb.Append('}').ToString();
				}
				// STRING PROPERTIES ONLY. ArchiveHijack reads exactly these off an ApiAvatar every
				// day without crashing, which is the proof they are safe. The ones that USED to be
				// here and are gone — version, created_at, updated_at, tags — each read a nested
				// il2cpp value (a struct, a DateTime, an Il2Cpp list) whose own liveness the
				// NativeGuard.Alive(a) gate does NOT cover, and reading a dead nested object is the
				// same uncatchable access violation. They are not worth crashing the game for a
				// metadata dump; the fields that matter (name, author, release, image) stay.
				try { F("name", a.name); } catch { }
				try { F("authorName", a.authorName); } catch { }
				try { F("authorId", a.authorId); } catch { }
				try { F("releaseStatus", a.releaseStatus); } catch { }
				try { F("description", a.description); } catch { }
				try { F("imageUrl", a.imageUrl); } catch { }
				try { F("thumbnailImageUrl", a.thumbnailImageUrl); } catch { }
				// close the JSON with a stable last field so the trailing comma above is valid.
				sb.Append("  \"source\": \"mod\"\n");
			}
			catch (Exception e) { sb.Append("  \"error\": ").Append(Json(e.Message)).Append('\n'); }
			return sb.Append('}').ToString();
		}

		private static string BuildEvents()
		{
			var sb = new StringBuilder(4096);
			float newest = _lastEventAt;
			int n = 0;
			try
			{
				if (ModConfig.UdonLogEnabled.Value)
				{
					foreach (var e in UdonLogModule.Entries)
					{
						if (e == null || e.Time <= _lastEventAt) continue;
						if (e.Time > newest) newest = e.Time;
						if (n >= MaxEventsPerSync) continue;
						if (n++ > 0) sb.Append(',');
						sb.Append("{\"k\":\"udon\",\"t\":").Append(Json(e.Clock))
						  .Append(",\"who\":").Append(Json(e.User))
						  .Append(",\"col\":").Append(Json(e.UserColor))
						  .Append(",\"obj\":").Append(Json(e.Obj))
						  .Append(",\"ev\":").Append(Json(e.Event))
						  .Append(",\"rep\":").Append(e.Repeats).Append('}');
					}
				}

				if (ModConfig.NetworkLogEnabled.Value)
				{
					foreach (var e in NetworkLogModule.Snapshot())
					{
						if (e == null || e.Time <= _lastEventAt) continue;
						if (e.Time > newest) newest = e.Time;
						if (n >= MaxEventsPerSync) continue;
						if (n++ > 0) sb.Append(',');
						sb.Append("{\"k\":\"net\",\"t\":").Append(Json(e.Clock))
						  .Append(",\"code\":").Append(e.Code)
						  .Append(",\"sender\":").Append(e.Sender)
						  .Append(",\"rep\":").Append(e.Repeats).Append('}');
					}
				}
			}
			catch { }
			_lastEventAt = newest;
			return sb.ToString();
		}

		private static string SafeWorld()
		{
			try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
			catch { return ""; }
		}

		// ------------------------------------------------------------------ incoming
		//
		// The client answers a sync with the commands it has queued since our last one. Each carries
		// a sequence number so a reply that arrives twice cannot apply the same change twice.
		private void Apply(string raw)
		{
			try
			{
				if (string.IsNullOrEmpty(raw)) return;
				using var doc = JsonDocument.Parse(raw);
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object) return;

				if (!root.TryGetProperty("commands", out var cmds) || cmds.ValueKind != JsonValueKind.Array)
				{ Status = Linked ? "linked" : Status; return; }

				int done = 0;
				foreach (var c in cmds.EnumerateArray())
				{
					if (c.ValueKind != JsonValueKind.Object) continue;

					long seq = 0;
					try { if (c.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number) seq = s.GetInt64(); } catch { }

					// NOT a high-water mark. The client's counter restarts at zero every time the client
					// restarts, while ours lives as long as the VRChat session -- so after a client restart
					// every new command arrives numbered BELOW what we had already seen and a "seq <= _seq"
					// test drops them all, silently, until the counter climbs back. That is what made the
					// toggles feel unreliable: the client shows the new value while the mod never got it.
					// A bounded set of recently applied numbers still catches a genuine repeat and is immune
					// to the counter going backwards.
					if (seq != 0 && _recent.Contains(seq)) continue;

					string kind = Str(c, "kind");
					string cid = Str(c, "id");
					Core.ConfigWatch.ApplyingFrom = "from the client";
					bool applied;
					try
					{
						applied = kind == "set" ? ApplySet(cid, Str(c, "value"))
							: kind == "action" && ApplyAction(cid, Str(c, "value"));
					}
					finally { Core.ConfigWatch.ApplyingFrom = null; }

					if (applied)
					{
						done++;
						VRChatArchiveModPlugin.Logger.LogInfo("[ModControl] applied " + kind + " " + cid + " = " + Str(c, "value"));
						Remember(seq);
					}
					else
					{
						// Deliberately NOT remembered: a command we could not apply must stay eligible.
						// And it is said out loud -- the client clears its queue the moment it hands a
						// command over, so a failure here is the last trace the change ever existed.
						_failed++;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[ModControl] could not apply " + kind + " '" + cid + "' from the client"
							+ (kind == "set" ? " (unknown setting, or the value did not fit its type)" : "") + ".");
					}

					if (seq > _seq) _seq = seq;
				}

				if (done > 0) { _applied += done; Status = "linked — " + _applied + " change(s) from the client" + (_failed > 0 ? ", " + _failed + " refused" : ""); }
				else if (Linked) Status = "linked";
			}
			catch (Exception e) { Status = "reply not understood: " + e.Message; }
		}

		// id is "Section/Key". The value arrives as text and is converted to the setting's real type
		// — BoxedValue rejects a string handed to a float setting, which would silently do nothing.
		private static bool ApplySet(string id, string value)
		{
			try
			{
				if (string.IsNullOrEmpty(id)) return false;
				int slash = id.IndexOf('/');
				if (slash <= 0) return false;
				string section = id.Substring(0, slash), key = id.Substring(slash + 1);
				if (IsInternal(section, key)) return false;   // never settable from outside the game

				foreach (var e in DevToolsModule.Config())
				{
					if (e?.Raw == null) continue;
					if (!string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
					if (!string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) continue;

					object boxed = Convert(e.Raw.SettingType, value);
					if (boxed == null) return false;
					e.Raw.BoxedValue = boxed;
					return true;
				}
			}
			catch { }
			return false;
		}

		private static object Convert(Type t, string v)
		{
			try
			{
				if (t == null) return null;
				if (t == typeof(bool))
					return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
				if (t == typeof(float))
					return float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
				if (t == typeof(int))
					return int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture);
				if (t == typeof(string)) return v;
				if (t.IsEnum) return Enum.Parse(t, v, true);
			}
			catch { }
			return null;
		}

		// One-shot things that are not a setting: wear an avatar, clone a player, reset movement.
		private static bool ApplyAction(string id, string value)
		{
			try
			{
				switch (id)
				{
					case "wear":
						if (string.IsNullOrEmpty(value)) return false;
						VaTagsModule.WearById(value, "");
						return true;

					// Everything that acts ON A PLAYER takes their user id and looks the live entry up
					// here, rather than the client trying to describe a player it cannot hold.
					case "clone":
						{
							var p = FindPlayer(value);
							if (p == null) return false;
							VaTagsModule.CloneAvatar(p);
							return true;
						}

					case "teleport":
						{
							var p = FindPlayer(value);
							if (p == null) return false;
							VaTagsModule.TeleportTo(p);
							return true;
						}

					case "orbit":
						{
							var p = FindPlayer(value);
							if (p == null) return false;
							OrbitModule.Toggle(OrbitModule.Mode.Orbit, p);
							return true;
						}

					case "sit":
						{
							var p = FindPlayer(value);
							if (p == null) return false;
							OrbitModule.Toggle(OrbitModule.Mode.Sit, p);
							return true;
						}

					// Objects orbiting a player — the ring feature that already existed in the
					// in-game menu but had no way in from the client.
					case "objectOrbit":
						{
							var p = FindPlayer(value);
							if (p == null) return false;
							ObjectOrbitModule.ToggleOnPlayer(p);
							return true;
						}

					case "objectOrbitStop":
						ObjectOrbitModule.Stop("stopped from the client");
						return true;

					// Push a URL into every video player in the world. value = the URL.
					//
					// MUST run on the Unity main thread. ApplyAction runs on a THREAD POOL thread (the
					// await continuation in SyncAsync has no Unity SynchronizationContext), and Inject
					// calls FindObjectsOfType / LoadURL / SetProgramVariable / SetOwner -- native Unity
					// calls that Unity refuses off the main thread, "not always by a catchable exception"
					// (see the channel-traps note). That off-thread native call IS the intermittent crash:
					// it happened to work when BuildSync had put us on main, and took the game down when
					// it did not. Queued like every udon command, so the real result comes back in the
					// next sync's status line ("applied" here just means accepted).
					case "videoUrl":
						{
							string vurl = value;
							OnMain(() =>
							{
								// NO QueueResult here: the `results` queue is a SHARED channel that
								// PlayersPage.DrainDumps() drains wholesale and copies to the clipboard as
								// "Avatar metadata copied" (channel-traps PIÈGE 2). Routing the video status
								// through it produced that nonsensical toast. The status lives in the mod
								// log (VideoUrlModule.LastStatus); the FUN page shows its own confirmation.
								try { VideoUrlModule.Inject(vurl); }
								catch (Exception ex) { VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] inject threw: " + ex.Message); }
							});
							return true;   // accepted; runs on the next main-thread pump
						}

					case "orbitStop":
						OrbitModule.Stop("stopped from the client");
						return true;

					case "resetMovement":
						SpeedModule.ResetToWorld();
						return true;

					// Force jump — a one-shot upward launch of the local player. value = optional force
					// override (metres/second); empty means use the configured default. Ordinary local
					// movement, queued to the main thread like everything that touches the player.
					case "forceJump":
						{
							float f = 0f;
							if (!string.IsNullOrEmpty(value))
								float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
							OnMain(() => ForceJumpModule.Launch(f));
							return true;
						}

					// Force grab: toggle grab/drop of whatever the crosshair is on. Aim happens in the
					// game, so the button is really a drop / re-grab \u2014 the reach and the lock override live
					// in the mod. Queued to the main thread; it raycasts and drives transforms.
					case "forceGrab":
						OnMain(() => ForceGrabModule.Toggle());
						return true;

					// The client just added or removed a favourite. Without this the in-game
					// sections would show the old list until their own slow refresh came round,
					// which reads as the change not having worked.
					// Metadata dumps. The answer goes back through the results queue, which the
					// client copies to the clipboard.
					case "dumpUser":
						{
							var p = FindPlayer(value);
							if (p == null) return false;
							QueueResult("user", value, DumpUser(p));
							return true;
						}

					case "dumpAvatar":
						{
							if (string.IsNullOrEmpty(value)) return false;
							QueueResult("avatar", value, DumpAvatar(value));
							return true;
						}

					// The client just wrote a tag. Without this the game keeps showing the old set
					// until the five-minute refresh, which reads as the write not having worked.
					case "refreshTags":
						VaTagsModule.RequestRefresh();
						return true;

					case "refreshFavs":
						_ = FavoritesModule.RefreshAsync();
						_ = WorldFavoritesModule.RefreshAsync();
						_ = UserFavoritesModule.RefreshAsync();
						return true;

					// ---- UDON MANAGER ------------------------------------------------------------
					//
					// The page lives in the desktop client; the work happens here. Every one of these is
					// QUEUED for the main thread rather than performed now — see OnMain. "Applied"
					// therefore means accepted; what the behaviour actually did comes back as the status
					// line in the next payload, which is the half the client shows.
					case "udonScan":
						OnMain(() => { UdonManagerModule.Rescan(); _udonPayload = BuildUdon(); });
						return true;

					case "udonRestore":
						OnMain(() => { UdonManagerModule.RestoreAll(); _udonPayload = BuildUdon(); });
						return true;

					// "<instanceId>:1" — switch one script on or off in THIS client.
					case "udonToggle":
						{
							int c = (value ?? "").LastIndexOf(':');
							if (c <= 0 || !int.TryParse(value.Substring(0, c), out int bid)) return false;
							bool on = value.Substring(c + 1) == "1";
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								if (e != null) UdonManagerModule.SetEnabled(e, on);
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// "1:<id>,<id>,…" — the bulk switch behind DISABLE / ENABLE ALL SHOWN. The client
					// sends the ids it is showing, because the filter that produced them lives there.
					case "udonMany":
						{
							int c = (value ?? "").IndexOf(':');
							if (c <= 0) return false;
							bool on = value.Substring(0, c) == "1";
							string[] parts = value.Substring(c + 1).Split(',');
							OnMain(() =>
							{
								var batch = new List<UdonManagerModule.Entry>(parts.Length);
								foreach (string s in parts)
									if (int.TryParse(s, out int one))
									{
										var e = UdonManagerModule.ById(one);
										if (e != null) batch.Add(e);
									}
								UdonManagerModule.SetMany(batch, on);
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// The entry points of one script, answered on the next sync.
					case "udonEvents":
						{
							if (!int.TryParse(value, out int bid)) return false;
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								_udonEventsId = bid;
								_udonEventList = e != null ? UdonManagerModule.EntryPoints(e) : new List<string>();
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// The variables of one script, answered on the next sync.
					case "udonVars":
						{
							if (!int.TryParse(value, out int bid)) return false;
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								_udonVarsId = bid;
								_udonVarList = e != null
									? UdonManagerModule.Variables(e)
									: new List<UdonManagerModule.Var>();
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// "<id>|<name>|<value>". The VALUE may itself contain a '|' — a world's string variable
					// can hold anything — so only the first two separators are separators.
					case "udonSetVar":
						{
							string raw = value ?? "";
							int p1 = raw.IndexOf('|');
							if (p1 <= 0) return false;
							int p2 = raw.IndexOf('|', p1 + 1);
							if (p2 < 0) return false;
							if (!int.TryParse(raw.Substring(0, p1), out int bid)) return false;
							string vname = raw.Substring(p1 + 1, p2 - p1 - 1);
							string vtext = raw.Substring(p2 + 1);
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								if (e != null)
								{
									UdonManagerModule.SetVariable(e, vname, vtext);
									// RE-READ IMMEDIATELY. The point of writing is seeing whether it took, and a
									// synced variable owned by somebody else can already have snapped back to
									// theirs by now. Showing the value we asked for would hide exactly that.
									_udonVarsId = bid;
									_udonVarList = UdonManagerModule.Variables(e);
								}
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// "<id>|<event>". TWO COMMANDS, not one with a flag: udonRun executes the event in
					// this client and nothing leaves the process, while udonRunGlobal broadcasts it to
					// everyone in the instance over VRChat's own networking. A mistyped value must never
					// be able to turn the first into the second.
					case "udonRun":
					case "udonRunGlobal":
						{
							int bar = (value ?? "").IndexOf('|');
							if (bar <= 0 || !int.TryParse(value.Substring(0, bar), out int bid)) return false;
							string ev = value.Substring(bar + 1);
							bool global = id == "udonRunGlobal";
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								if (e != null)
								{
									if (global) UdonManagerModule.RunGlobal(e, ev);
									else UdonManagerModule.RunLocal(e, ev);
								}
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// "<id>|<event>". Runs the event LOCALLY and reports which of the behaviour's
					// variables changed -- the "what does this event do" answer. Never networked.
					case "udonRunDiff":
						{
							int bar = (value ?? "").IndexOf('|');
							if (bar <= 0 || !int.TryParse(value.Substring(0, bar), out int dbid)) return false;
							string ev = value.Substring(bar + 1);
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(dbid);
								_udonDiffId = dbid;
								_udonDiffEvent = ev;
								_udonDiffList = e != null ? UdonManagerModule.RunAndDiff(e, ev) : new List<UdonManagerModule.VarChange>();
								if (e != null) { _udonVarsId = dbid; _udonVarList = UdonManagerModule.Variables(e); }
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// A dump, by job name. The client's DEVTOOLS section lists these; the mod owns
					// what each one actually captures, so only the name crosses the wire.
					case "dump":
						{
							if (string.IsNullOrEmpty(value)) return false;
							if (!Enum.TryParse(value, true, out CaptureModule.Job job)) return false;
							CaptureModule.Request(job);
							return true;
						}
				}
			}
			catch { }
			return false;
		}

		// ONE LOOKUP PER AVATAR, not per sync. The release status only changes when somebody
		// switches avatar, so it is cached against the avatar id and re-read when that changes —
		// otherwise every player would be re-resolved once a second for an answer that is stable.
		private static readonly Dictionary<string, string> ReleaseCache = new Dictionary<string, string>(StringComparer.Ordinal);

		private static string ReleaseCached(VaTagsModule.PlayerEntry p)
		{
			try
			{
				string key = (p.UserId ?? "") + "|" + (p.AvatarId ?? "");
				if (string.IsNullOrEmpty(p.AvatarId)) return "";
				if (ReleaseCache.TryGetValue(key, out string cached) && !string.IsNullOrEmpty(cached)) return cached;
				string rs = VaTagsModule.ReleaseStatusOf(p);
				if (!string.IsNullOrEmpty(rs))
				{
					// Bounded: an instance turns over, and this must not grow for a whole session.
					if (ReleaseCache.Count > 400) ReleaseCache.Clear();
					ReleaseCache[key] = rs;
				}
				return rs;
			}
			catch { return ""; }
		}

		private static VaTagsModule.PlayerEntry FindPlayer(string userId)
		{
			try
			{
				if (string.IsNullOrEmpty(userId)) return null;
				var roster = VaTagsModule.Roster;
				lock (roster)
				{
					foreach (var p in roster)
					{
						if (p == null) continue;
						if (string.Equals(p.UserId, userId, StringComparison.Ordinal)) return p;
					}
				}
			}
			catch { }
			return null;
		}

		private static string Str(JsonElement e, string prop)
			=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

		// Minimal JSON string encoder — the mod builds its payloads by hand everywhere else too.
		private static string Json(string s)
		{
			if (s == null) return "\"\"";
			var sb = new StringBuilder(s.Length + 2);
			sb.Append('"');
			foreach (char c in s)
			{
				switch (c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
						else sb.Append(c);
						break;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}
	}
}
