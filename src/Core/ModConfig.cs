using BepInEx.Configuration;

namespace VRChatArchiveMod.Core
{
	// Central configuration, backed by BepInEx's config file
	// (BepInEx/config/org.vrchatarchive.mod.cfg). Default caps mirror the
	// Munchen client's anti-crash limits — high enough that heavy-but-legit
	// avatars are untouched, low enough to stop genuine crasher avatars.
	public static class ModConfig
	{
		// --- Anti-Crash: master + per-vector toggles ---
		public static ConfigEntry<bool> AntiCrashEnabled;
		public static ConfigEntry<bool> ClampParticles;
		public static ConfigEntry<bool> ClampLights;
		public static ConfigEntry<bool> ClampAudioSources;
		public static ConfigEntry<bool> ClampCloth;
		public static ConfigEntry<bool> ClampPhysBones;
		public static ConfigEntry<bool> ClampContacts;
		public static ConfigEntry<bool> AntiBlockEnabled;
		// Passive record of INBOUND Photon events. Receive-side only — the module never sends.
		public static ConfigEntry<bool> NetworkLogEnabled;
		public static ConfigEntry<bool> NetworkLogToFile;
		public static ConfigEntry<bool> EventsShowUdon;      // DISPLAY gate, not recording
		public static ConfigEntry<bool> EventsShowNetwork;
		public static ConfigEntry<bool> NetworkInterestingOnly;
		public static ConfigEntry<int> NetworkBulkPerSecond;
		// Anti-Udon: client-side self-defence against world scripts behaving like crashers.
		public static ConfigEntry<bool> UdonBlockCrashers;
		public static ConfigEntry<bool> UdonBlockAll;
		public static ConfigEntry<int> UdonCrasherPerSecond;
		public static ConfigEntry<int> UdonFloodPerSecond;
		public static ConfigEntry<bool> UdonLogEnabled;
		public static ConfigEntry<bool> UdonLogFrameEvents;
		public static ConfigEntry<bool> UdonLogInterestingOnly;
		public static ConfigEntry<bool> QMConsoleEnabled;
		public static ConfigEntry<bool> QMTabEnabled;   // native VRChat QuickMenu tab
		public static ConfigEntry<bool> UserMenuEnabled; // MOD FEATURES card on VRChat's per-user menu
		public static ConfigEntry<bool> WingPlayersEnabled; // instance roster inside VRChat's left wing
		public static ConfigEntry<bool> MenuSkinEnabled;
		public static ConfigEntry<bool> MenuSkinClearVeil;
		public static ConfigEntry<bool> MenuThemeEnabled;
		// (ArchiveFavListEnabled removed — the Archive category is always on. See Init.)
		public static ConfigEntry<bool> ForceNewSection;       // add our own row instead of borrowing
		public static ConfigEntry<bool> WorldFavListEnabled;
		// WHICH list to take over, by its displayed name. Named rather than detected: two attempts
		// at picking an "empty" one both stole a full one, because a list's contents are not in
		// _favorites at the moment we look — VRChat shows 25/100 from somewhere else entirely.
		public static ConfigEntry<int> WorldListSlot;
		public static ConfigEntry<int> SocialListSlot;   // extra world favourites list
		public static ConfigEntry<bool> UserFavListEnabled;    // extra social/friends favourites list
		public static ConfigEntry<bool> AvatarFavListEnabled;  // extra avatar list (experimental)
		// (ArchiveAutoClean removed 2026-08-28 — always on. See Init.)
		public static ConfigEntry<string> ArchiveCategoryName;   // which shelf we borrow   // synthetic category in VRChat's own avatar menu
		public static ConfigEntry<bool> MenuExclusive;
		public static ConfigEntry<bool> MenuRecorder;   // temporary: records the menu as you browse it
		public static ConfigEntry<bool> MenuCaptureEnabled;  // records each menu PAGE you open, with cell templates
		public static ConfigEntry<bool> ModControlEnabled;   // let the desktop client drive the mod's settings
		// Soundboard: a member triggers a clip, every other mod plays it locally.
		public static ConfigEntry<bool> SoundboardEnabled;
		// Gravity: LOCAL only. There is no API to change anybody else's.
		public static ConfigEntry<bool> GravityPlayerOff;
		public static ConfigEntry<bool> GravityWorldOff;
		public static ConfigEntry<float> SoundboardVolume;
		public static ConfigEntry<float> SoundboardPollSeconds;
		public static ConfigEntry<bool> DebugMode;      // verbose diagnostics build behaviour
		public static ConfigEntry<bool> ShowDevTab;     // the DEV tools tab in the menu
		public static ConfigEntry<bool> AudioWatchEnabled; // record which clips actually play
		public static ConfigEntry<string> QMConsolePink;
		public static ConfigEntry<string> QMConsoleViolet;
		public static ConfigEntry<bool> UdonLogOverlay;
		public static ConfigEntry<int> UdonLogOverlayLines;

		// --- Anti-Crash: crasher-territory thresholds (Munchen defaults) ---
		public static ConfigEntry<int> MaxParticleSystems;  // count of ParticleSystem components
		public static ConfigEntry<int> MaxLights;           // MaxLightSources = 8
		public static ConfigEntry<int> MaxAudioSources;     // MaxAudioSources = 150
		public static ConfigEntry<int> MaxCloth;            // MaxCloth = 75
		public static ConfigEntry<int> MaxPhysBones;        // VRCPhysBone count cap
		public static ConfigEntry<int> MaxContacts;         // VRCContact sender/receiver count cap

		// --- Scan cadence ---
		public static ConfigEntry<int> ScanIntervalFrames;  // poll every N frames

		// --- AssetBundle archiving ---
		public static ConfigEntry<string> ArchiveFolder;     // empty => <BepInEx>/VRChatArchiveMod/Bundles

		// --- FewTags (community nameplate tags, database by Fewdys) ---
		public static ConfigEntry<bool> FewTagsEnabled;
		public static ConfigEntry<string> FewTagsDbUrl;
		public static ConfigEntry<int> FewTagsUpdateMinutes;
		public static ConfigEntry<int> FewTagsMaxTagsPerUser;
		public static ConfigEntry<bool> FewTagsShowHeader;
		public static ConfigEntry<bool> FewTagsShowBigPlates;
		public static ConfigEntry<bool> FewTagsFilterSlurs;   // hide abusive third-party tags locally
		public static ConfigEntry<float> FewTagsBaseY;      // first plate height above the nameplate
		public static ConfigEntry<float> FewTagsSpacing;    // vertical gap between stacked plates (legacy layout)
		public static ConfigEntry<float> FewTagsSpacingExpanded;   // ...and on the Fragments layout
		public static ConfigEntry<float> FewTagsBaseYExpanded;     // first-plate height, Fragments layout

		// --- VA Tags (VRChatArchive player tagging — PLAYERS tab, site API) ---
		public static ConfigEntry<bool> VaTagsEnabled;
		public static ConfigEntry<string> VaTagsApiBase;
		public static ConfigEntry<int> VaTagsUpdateMinutes;
		public static ConfigEntry<bool> VaTagsShowPlates;
		public static ConfigEntry<float> VaTagsPlateY;

		// --- Watchlist (special users → super-RGB ESP box + join notification) ---
		public static ConfigEntry<bool> WatchlistEnabled;
		public static ConfigEntry<string> WatchlistUserIds;   // comma-separated usr_ ids
		// Member-join banner: clean pink→violet pill by default; RGB = animated rainbow (for testing).
		public static ConfigEntry<bool> MemberNotifyRgb;
		// Players whose capsule cycles the spectrum instead of showing a trust colour.
		public static ConfigEntry<string> RainbowUserIds;
		public static ConfigEntry<float> RainbowSpeed;

		// --- Instance panels (player list + join/leave log) ---
		public static ConfigEntry<bool> InstancePanelsEnabled;
		public static ConfigEntry<float> InstancePanelsScale;   // overall size + font scale

		// --- Join notifier (transient on-screen toast on join/leave) ---
		public static ConfigEntry<bool> JoinNotifierEnabled;
		public static ConfigEntry<bool> JoinNotifierShowLeave;

		// --- Radar (top-down player map, same position data as ESP) ---
		public static ConfigEntry<bool> RadarEnabled;
		public static ConfigEntry<float> RadarRange;   // metres shown edge-to-centre
		public static ConfigEntry<float> RadarSize;    // on-screen diameter (px)
		public static ConfigEntry<bool> RadarNames;    // player name beside each blip
		// Optional top-down camera that draws the actual world under the radar blips.
		public static ConfigEntry<bool> RadarMap;
		public static ConfigEntry<int> RadarMapResolution;
		public static ConfigEntry<int> RadarMapEveryFrames;
		public static ConfigEntry<float> RadarMapHeight;
		public static ConfigEntry<float> RadarMapOpacity;
		public static ConfigEntry<bool> RosterPositions;   // live X/Y/Z per player in the HUD roster
		// --- Orbit / Sit (self-movement relative to another player) ---
		public static ConfigEntry<float> OrbitRadius;
		public static ConfigEntry<float> OrbitSpeed;
		public static ConfigEntry<float> OrbitHeight;
		public static ConfigEntry<float> SitHeight;
		// Object orbit: the world's loose props spun around a player. Local-only gag.
		public static ConfigEntry<int> ObjOrbitCount;
		public static ConfigEntry<float> ObjOrbitRange;
		public static ConfigEntry<float> ObjOrbitRadius;
		public static ConfigEntry<float> ObjOrbitSpeed;
		public static ConfigEntry<float> ObjOrbitHeight;
		public static ConfigEntry<float> ObjOrbitMaxSize;
		public static ConfigEntry<bool> ObjOrbitSpin;
		public static ConfigEntry<bool> ObjOrbitSynced;

		// --- Full game log capture (passive Unity log sink → file; never touches Photon) ---
		public static ConfigEntry<bool> AllowIl2CppDelegates;
		public static ConfigEntry<bool> LogCaptureEnabled;
		public static ConfigEntry<bool> LogCaptureStackTraces;

		// --- Bad Apple chatbox player (OSC) ---
		public static ConfigEntry<bool> BadAppleLoop;
		public static ConfigEntry<int> BadAppleIntervalMs;
		public static ConfigEntry<string> BadAppleOscHost;
		public static ConfigEntry<int> BadAppleOscPort;
		public static ConfigEntry<string> BadAppleCharset;

		// --- Menu input capture ---
		public static ConfigEntry<bool> MenuCaptureInput;
		public static ConfigEntry<bool> AltFreeCursor;      // Left Alt = free the cursor + movement

		// --- Menu appearance (persisted) ---
		public static ConfigEntry<int> UiAccent;             // accent color preset index
		public static ConfigEntry<float> UiScale;            // overall menu scale

		// --- Movement (local, self-only) ---
		public static ConfigEntry<bool> FlyEnabled;
		// Locomotion, through VRChat's own per-player setters. Local only.
		public static ConfigEntry<bool> SpeedEnabled;
		// One switch per kind of movement. A single master meant turning on "custom speed" to change
		// walking also took over running and jumping, which is not what anybody wants.
		public static ConfigEntry<bool> WalkMod;
		public static ConfigEntry<bool> RunMod;
		public static ConfigEntry<bool> JumpMod;
		public static ConfigEntry<float> WalkSpeed;
		public static ConfigEntry<float> RunSpeed;
		public static ConfigEntry<float> StrafeSpeed;
		public static ConfigEntry<float> JumpImpulse;
		public static ConfigEntry<bool> NoclipEnabled;     // pass through walls (collision off)
		public static ConfigEntry<bool> ClickTpEnabled;    // right-hold + left-click = teleport to aim
		public static ConfigEntry<float> ClickTpMaxDistance;
		public static ConfigEntry<float> FlySpeed;
		public static ConfigEntry<float> FlyBoostSpeed;
		public static ConfigEntry<float> FlyRotateSpeed;
		public static ConfigEntry<bool> ArrowRotateEnabled;   // arrow-key rotation while flying (deg/sec)

		// --- Era (2017 / 2018 / current look) ---
		public static ConfigEntry<bool> LoadingScreenEnabled;
		public static ConfigEntry<bool> LoadingMusic;
		public static ConfigEntry<float> LoadingMusicVolume;

		// --- Spawn stinger (plays once when you finish loading into an instance) ---
		public static ConfigEntry<bool> SpawnSoundEnabled;
		public static ConfigEntry<float> SpawnSoundVolume;

		// --- Custom nameplates (2018-style frame drawn over the world) ---

		// --- ESP (visualization only) ---
		public static ConfigEntry<bool> EspEnabled;
		public static ConfigEntry<bool> EspBox;
		public static ConfigEntry<bool> EspSkeleton;   // bone-to-bone wireframe instead of a flat capsule
		public static ConfigEntry<float> ForceGrabRange;
		public static ConfigEntry<float> ForceJumpForce;
		public static ConfigEntry<bool> ForceGrabAnyObject;
		public static ConfigEntry<bool> EspCapsule;
		public static ConfigEntry<bool> EspThroughWalls;   // real 3D capsule lit by VRChat's own glow
		public static ConfigEntry<bool> EspPortals;   // world portals
		public static ConfigEntry<bool> EspItems;     // grabbable pickups
		public static ConfigEntry<bool> EspName;
		public static ConfigEntry<bool> EspDistance;
		public static ConfigEntry<float> EspMaxDistance;
		public static ConfigEntry<bool> EspHighlight;   // outline glow via VRChat's HighlightsFX

		public static void Init(ConfigFile cfg)
		{
			AntiCrashEnabled = cfg.Bind("AntiCrash", "Enabled", true,
				"Master switch for the avatar anti-crash protection.");

			ClampParticles = cfg.Bind("AntiCrash", "ClampParticles", true,
				"Neutralize particle-bomb avatars (excessive particle systems).");
			ClampLights = cfg.Bind("AntiCrash", "ClampLights", true,
				"Strip excessive real-time Lights from loaded avatars.");
			ClampAudioSources = cfg.Bind("AntiCrash", "ClampAudioSources", true,
				"Strip excessive AudioSources (audio-spam crashers).");
			ClampCloth = cfg.Bind("AntiCrash", "ClampCloth", true,
				"Strip excessive Cloth components.");
			ClampPhysBones = cfg.Bind("AntiCrash", "ClampPhysBones", true,
				"Strip excessive VRCPhysBone components (CPU-spike crashers).");
			ClampContacts = cfg.Bind("AntiCrash", "ClampContacts", true,
				"Strip excessive VRCContact senders/receivers (contact-flood crashers).");

			MaxParticleSystems = cfg.Bind("AntiCrash.Thresholds", "MaxParticleSystems", 256,
				"Maximum ParticleSystem components allowed on one avatar.");
			MaxLights = cfg.Bind("AntiCrash.Thresholds", "MaxLights", 8,
				"Maximum real-time Light components allowed on one avatar (Munchen default: 8).");
			MaxAudioSources = cfg.Bind("AntiCrash.Thresholds", "MaxAudioSources", 150,
				"Maximum AudioSource components allowed on one avatar (Munchen default: 150).");
			MaxCloth = cfg.Bind("AntiCrash.Thresholds", "MaxCloth", 75,
				"Maximum Cloth components allowed on one avatar (Munchen default: 75).");
			MaxPhysBones = cfg.Bind("AntiCrash.Thresholds", "MaxPhysBones", 256,
				"Maximum VRCPhysBone components allowed on one avatar.");
			MaxContacts = cfg.Bind("AntiCrash.Thresholds", "MaxContacts", 256,
				"Maximum VRCContact sender+receiver components allowed on one avatar.");

			ScanIntervalFrames = cfg.Bind("AntiCrash", "ScanIntervalFrames", 30,
				"How often (in frames) to scan for newly-loaded avatars. Lower = faster reaction, higher cost.");

			ArchiveFolder = cfg.Bind("BundleArchive", "Folder", "",
				"Destination folder for archived bundles. Leave blank for <BepInEx>/VRChatArchiveMod/Bundles.");

			FewTagsEnabled = cfg.Bind("FewTags", "Enabled", true,
				"Show FewTags community nameplate tags (database by Fewdys, github.com/Fewdys/FewTags).");
			FewTagsDbUrl = cfg.Bind("FewTags", "DatabaseUrl", "https://raw.githubusercontent.com/Fewdys/FewTags/main/FewTags.json",
				"URL of the FewTags JSON database.");
			FewTagsUpdateMinutes = cfg.Bind("FewTags", "UpdateMinutes", 10,
				"How often (minutes) to re-download the tag database.");
			FewTagsMaxTagsPerUser = cfg.Bind("FewTags", "MaxTagsPerUser", 5,
				"Maximum tag lines shown above one player's nameplate.");
			FewTagsShowHeader = cfg.Bind("FewTags", "ShowHeader", true,
				"Show the rainbow '- FewTags -' header plate (red 'Malicious User' for flagged accounts).");
			FewTagsShowBigPlates = cfg.Bind("FewTags", "ShowBigPlates", true,
				"Show the large free-text plate some users have above their tags.");
			FewTagsFilterSlurs = cfg.Bind("FewTags", "FilterSlurs", true,
				"Hide FewTags entries containing slurs. The FewTags database is third-party and anyone can "
				+ "write to it, so this filters what YOUR client displays; it does not change the database.");
			FewTagsBaseY = cfg.Bind("FewTags", "BaseY", 119.05f,
				"Height of the first tag plate above the nameplate. Raise if tags overlap the name.");
			FewTagsSpacing = cfg.Bind("FewTags", "Spacing", 28f,
				"Vertical gap between stacked tag plates on the LEGACY nameplate layout (Quick Stats).");
			FewTagsBaseYExpanded = cfg.Bind("FewTags", "BaseYExpanded", 205f,
				"Height of the FIRST tag plate on the CURRENT nameplate layout "
				+ "(NameplateFragment/ExpandedInfo). That plate is much taller than on the old "
				+ "layout, so the legacy 119 started the stack INSIDE the nameplate instead of "
				+ "above it. Raise to push the whole stack further up.");
			FewTagsSpacingExpanded = cfg.Bind("FewTags", "SpacingExpanded", 78f,
				"Vertical gap between stacked tag plates on the CURRENT nameplate layout "
				+ "(NameplateFragment/ExpandedInfo). The plates are taller there, so the legacy gap of "
				+ "28 makes them overlap each other and the player's name.");

			VaTagsEnabled = cfg.Bind("VaTags", "Enabled", true,
				"VRChatArchive community player tags: PLAYERS tab, nameplate plates, tag DB sync. "
				+ "Tags are VRChatArchive metadata — only mod users see them; VRChat itself is never touched.");
			VaTagsApiBase = cfg.Bind("VaTags", "ApiBase", "https://vrchatarchive.org",
				"Base URL of the VRChatArchive tag API (owner/local testing: http://127.0.0.1:8081).");
			VaTagsUpdateMinutes = cfg.Bind("VaTags", "UpdateMinutes", 5,
				"How often (minutes) to re-download the shared tag database.");
			VaTagsShowPlates = cfg.Bind("VaTags", "ShowPlates", true,
				"Render each tagged user's VA tags on a plate above their nameplate.");
			VaTagsPlateY = cfg.Bind("VaTags", "PlateY", 91.05f,
				"Height of the VA tag plate above the nameplate (sits below the FewTags stack).");

			WatchlistEnabled = cfg.Bind("Watchlist", "Enabled", true,
				"Highlight specific users with an animated rainbow ESP box and pop a notification when they join your instance.");
			RainbowUserIds = cfg.Bind("ESP", "RainbowUsers", "",
				"Comma-separated usr_ ids drawn with a cycling rainbow capsule instead of a trust "
				+ "colour, on every client running this mod. It is a signature: it changes how OTHER "
				+ "people see that player, not how that player sees themselves — your own capsule is "
				+ "never drawn for you. People without the mod see nothing either way.");
			RainbowSpeed = cfg.Bind("ESP", "RainbowSpeed", 0.35f,
				"How fast the rainbow cycles, in full loops per second.");

			WatchlistUserIds = cfg.Bind("Watchlist", "UserIds", "",
				"Comma-separated VRChat user ids to watch.");
			MemberNotifyRgb = cfg.Bind("Watchlist", "MemberNotifyRgb", false,
				"Member-join banner style: false = clean pink→violet pill, true = animated RGB rainbow.");

			InstancePanelsEnabled = cfg.Bind("InstancePanels", "Enabled", true,
				"Show the instance player list (left) and join/leave log (right). Right-Shift+L toggles.");
			InstancePanelsScale = cfg.Bind("InstancePanels", "Scale", 1.4f,
				"Size of the two overlay panels and their text (1.0 = original, 2.0 = double). "
				+ "Raise this if the panels are hard to read.");

			JoinNotifierEnabled = cfg.Bind("JoinNotifier", "Enabled", false,
				"Transient join/leave toast in the middle of the screen. OFF by default: the INSTANCE LOG "
				+ "panel already lists every join and leave with a timestamp, so the toast only repeated it "
				+ "over the world. Switch it on if you want the popup as well.");
			JoinNotifierShowLeave = cfg.Bind("JoinNotifier", "ShowLeave", true,
				"Also toast on leaves (off = joins only).");

			RadarEnabled = cfg.Bind("Radar", "Enabled", true,
				"Top-down radar showing every player around you (same position data as ESP). Right-Shift+M toggles.");

			AntiBlockEnabled = cfg.Bind("AntiBlock", "Enabled", false,
				"Anti-block: keep users who blocked you VISIBLE locally. You stay blocked and nothing networked changes — it only re-reveals their avatar on your screen and notifies you once.");

			UdonBlockCrashers = cfg.Bind("AntiUdon", "BlockCrashers", true,
				"Suspend a single Udon event for 10s when it fires at crasher-tier rate (400+ calls in one "
				+ "second). Local only: your client stops running that ONE event; nothing is sent and no "
				+ "other player is affected.");
			UdonCrasherPerSecond = cfg.Bind("AntiUdon", "CrasherPerSecond", 400,
				"Calls-per-second at which a single Udon event is treated as a crasher and suspended. "
				+ "Raise it when a legitimate world pump trips the guard \u2014 a busy video player can poll "
				+ "get_VideoPlayerType hundreds of times a second, and suspending it breaks the video FOR YOU.");
			UdonFloodPerSecond = cfg.Bind("AntiUdon", "FloodPerSecond", 2500,
				"Global flood ceiling: the TOTAL Udon events per second, across every script, above which "
				+ "the whole dispatch is treated as a crasher and non-lifecycle events are suspended for a "
				+ "few seconds. This is the net the per-event guard cannot be: a crasher that spreads its "
				+ "calls over many event NAMES never trips any single per-name counter, but it cannot hide "
				+ "from the total. Set well above a busy world (a full instance idles a few hundred/s), so "
				+ "only a genuine flood crosses it. 0 disables the global net.");
			UdonBlockAll = cfg.Bind("AntiUdon", "BlockAll", false,
				"PANIC: stop running world Udon events entirely. OFF by default because Udon IS the world — "
				+ "doors, pens, video players and seats stop working for you and you desync from what everyone "
				+ "else runs. Join/leave and start/enable events are still allowed so worlds do not wedge.");
			UdonLogEnabled = cfg.Bind("UdonLog", "Enabled", true,
				"UDON tab: live console of the Udon events happening around you (who did what). Read-only — it observes events your client already runs and never sends or blocks any.");
			UdonLogFrameEvents = cfg.Bind("UdonLog", "ShowFrameEvents", false,
				"Also log per-frame events (_update, _lateUpdate, …). Very noisy and costly — off by default.");
			UdonLogInterestingOnly = cfg.Bind("UdonLog", "InterestingOnly", true,
				"Hide the polling noise (get_*/set_* property reads, internal numeric jumps) and keep only meaningful events: interactions, pickups, custom and network events.");

			QMConsoleEnabled = cfg.Bind("QMConsole", "Enabled", true,
				"Inject a native VRChat-Archive console into VRChat's own QuickMenu (KARMA-style), showing the Udon events. Local UI only.");
			QMTabEnabled = cfg.Bind("QuickMenu", "NativeTab", true,
				"Add a VRChat Archive tab to VRChat's own QuickMenu tab strip (clones a disabled built-in tab; never modifies the game's own objects).");
			UserMenuEnabled = cfg.Bind("QuickMenu", "UserMenuCard", true,
				"Add a MOD FEATURES card to VRChat's per-user menu, opening the mod's PLAYERS tab with that user selected.");
			WingPlayersEnabled = cfg.Bind("QuickMenu", "WingPlayers", true,
				"Show the instance roster inside VRChat's own left wing menu instead of a floating overlay window.");
			NetworkLogEnabled = cfg.Bind("NetworkLog", "Enabled", true,
				"Record the Photon network events this client RECEIVES. Listen-only: the mod never sends, "
				+ "raises or replays an event.");
			NetworkLogToFile = cfg.Bind("NetworkLog", "ToFile", false,
				"Also write every received event to BepInEx/VRChatArchiveMod/network/. Always on in debug mode.");
			// DISPLAY, not recording. UdonLogEnabled controls whether events are captured at all;
			// these two decide which of the two SOURCES the console shows, so the feed can be Udon
			// only, network only, or both, without losing what is being recorded underneath.
			EventsShowUdon = cfg.Bind("Udon", "ShowUdonRows", true,
				"Show Udon events in the console feed.");
			EventsShowNetwork = cfg.Bind("NetworkLog", "ShowInEventsConsole", true,
				"Interleave the received Photon events into the EVENTS console next to the Udon events, "
				+ "so one panel shows everything happening around you.");
			NetworkInterestingOnly = cfg.Bind("NetworkLog", "InterestingOnly", true,
				"Hide the bulk traffic and keep the rare events. VRChat does not publish what its event "
				+ "codes mean, so this does NOT filter by a guessed name \u2014 it filters by measured RATE: a code "
				+ "that has ever exceeded BulkPerSecond is continuous sync and gets hidden, everything else is "
				+ "kept. Self-calibrating, and it cannot mislabel an event it does not understand.");
			NetworkBulkPerSecond = cfg.Bind("NetworkLog", "BulkPerSecond", 20,
				"Peak events-per-second above which a Photon event code counts as bulk sync traffic and is "
				+ "hidden by InterestingOnly.");
			MenuSkinEnabled = cfg.Bind("QuickMenu", "SkinBackground", true,
				"Replace VRChat's QuickMenu backdrop with the VRChat Archive image.");
			// Two crossing sheets of scrolling noise over the wallpaper. Not a refraction shader
			// (those cannot be loaded into VRChat Il2Cpp UI) but the same flowing-caustics look, at
			// the cost of a couple of uvRect writes per frame while the menu is open.
			MenuSkinClearVeil = cfg.Bind("QuickMenu", "ClearBackgroundVeil", true,
				"Hide the translucent full-panel layers VRChat draws OVER the menu wallpaper, which is "
				+ "what washes the image out. Only flat see-through fills covering nearly the whole panel "
				+ "are touched; every one is named in the log and put back when this is switched off.");
			// ARCHIVE FAVOURITES IS NOT A SETTING ANY MORE.
			//
			// It was a ConfigEntry kept "so it can still be turned off by editing this file if a
			// VRChat update ever makes it misbehave". That escape hatch is exactly what cost the
			// feature: the value ended up false — reachable from the desktop client's settings page
			// like any other switch — and the whole thing vanished with no error anywhere, because
			// every module gated on it returns silently. Hours were spent looking for a bug in code
			// that was simply switched off.
			//
			// The Archive category IS the favourites feature. A switch whose only correct position
			// is on is not a choice, it is a way to break the mod by accident.

			// WHICH CATEGORY WE TAKE OVER, and it is not a cosmetic choice. "SDK Test Avatars" holds
			// LOCAL SDK builds, so VRChat's Apply takes a different route for anything sitting in it —
			// which is why Apply behaved oddly there. A VRC+ favourites slot has exactly the semantics
			// we want: "avatars I saved in order to wear them".
			ArchiveCategoryName = cfg.Bind("Favorites", "CategoryToBorrow", "SDK Test Avatars",
				"Name of the avatar-menu category the Archive list takes over. Its own contents are "
				+ "hidden while the mod runs and come back when it is off. Falls back to SDK Test Avatars, "
				+ "then to the last category, if this name is not present. VRC+ slots are never borrowed.");

			WorldFavListEnabled = cfg.Bind("Favorites", "WorldList", true,
				"Add a VRCHAT ARCHIVE list to VRChat's WORLD favourites, beside Favorite Worlds 1-4. "
				+ "Nothing of VRChat's is overwritten — one more list appears under the others. Needs "
				+ "the desktop client, which is what holds the account key.");

			ForceNewSection = cfg.Bind("Favorites", "ForceNewSection", false,
				"Add a NEW row of our own instead of borrowing an empty one. The sidebar is a page "
				+ "that builds itself on navigation, and a previous attempt made every world list "
				+ "vanish — so this arms a watchdog: anything that throws in the next 8 seconds is "
				+ "written to the log and the row is pulled straight back out. Use it to find the "
				+ "reason, not as the normal mode.");

			WorldListSlot = cfg.Bind("Favorites", "WorldListSlot", 3,
				"WHICH world list to take over, by position, 0-based and in the order the sidebar "
				+ "shows them: 0 = Favorite Worlds 1 … 3 = Favorite Worlds 4. Position rather than "
				+ "name because VRChat only stores a name for lists you renamed yourself — the rest "
				+ "are worlds2/worlds3/worlds4 internally and the menu derives the label.");

			SocialListSlot = cfg.Bind("Favorites", "SocialListSlot", 2,
				"WHICH friend group to take over, by position: 0 = Group 1, 1 = Group 2, "
				+ "2 = Favorite Friends 3.");

			UserFavListEnabled = cfg.Bind("Favorites", "SocialList", true,
				"Add a VRCHAT ARCHIVE list to VRChat's SOCIAL favourites, beside your friend groups. "
				+ "Nothing of VRChat's is overwritten. Needs the desktop client.");

			AvatarFavListEnabled = cfg.Bind("Favorites", "AvatarList", false,
				"Add a VRCHAT ARCHIVE list to the AVATAR favourites, the same way as worlds and social. "
				+ "The avatar sidebar is composed differently from the worlds one, so this may inject "
				+ "cleanly and still not appear — the AVATARS tab's borrowed category is what covers "
				+ "that case. If BOTH show up you will see the name twice: turn one of them off.");

			// AUTO-CLEAN IS ALWAYS ON, AND NOT A SETTING ANY MORE.
			//
			// Same reasoning as NativeCategory: a broken favourite is what the feature exists to
			// repair, and a switch whose only correct position is "on" is only ever a way to break
			// the mod by accident. Removing the ConfigEntry also removes the toggle from the
			// desktop client automatically (its MOD SETTINGS page is built by reflection over
			// ModConfig's fields).

			MenuThemeEnabled = cfg.Bind("QuickMenu", "ArchiveTheme", true,
				"Repaint VRChat's QuickMenu in the Archive's pink/violet instead of its stock teal, and "
				+ "turn its text violet. Cards are tinted by where they sit on screen so a page sweeps "
				+ "pink to violet — a single UI image cannot hold a gradient on its own. Reversible: "
				+ "switching this off restores VRChat's own colours without a restart.");
			MenuExclusive = cfg.Bind("QuickMenu", "OneMenuAtATime", true,
				"Hide VRChat's QuickMenu while the mod menu is open, and put it back when it closes. The two draw through different systems (IMGUI vs uGUI) and share no ordering, so without this they stack on the same pixels and both take clicks.");
			MenuRecorder = cfg.Bind("Debug", "MenuRecorder", false,
				"TEMPORARY debugging aid: while on, every menu object you BROWSE is appended to one "
				+ "capture file — path, components, rect, the TMP label and the sprite name. A one-shot "
				+ "dump only ever holds the page that was open, and carried no text at all, which is "
				+ "why objects could not be matched to what is on screen. Read-only, off by default.");
			// OFF by default. This walks a menu page and writes a dump file, on the main thread —
			// a visible hitch every time you open a page you have not opened before. It is a tool
			// for working out what VRChat's menus contain, not something a normal session should
			// be paying for, and it shipped switched on.
			MenuCaptureEnabled = cfg.Bind("Debug", "MenuCapture", false,
				"Records every VRChat menu PAGE you open to its own file under captures/pages — the "
				+ "full hierarchy with REAL component type names, plus a template section describing "
				+ "each distinct cell and what drives it. Rebuilding VRChat's own sections means "
				+ "cloning VRChat's own cells, and a one-shot dump only ever holds the pages that "
				+ "happened to be open: the social grid cloned a WORLD card because the friends list "
				+ "was closed when the dump ran. Read-only, bounded, and on by default so a normal "
				+ "session produces the captures without anyone remembering to press anything.");
			ModControlEnabled = cfg.Bind("Client", "ModControl", true,
				"Let the VRChat Archive desktop client read and change these settings while the game "
				+ "runs. The mod polls the client's loopback bridge once a second and applies whatever you "
				+ "changed there — the game process never opens a port of its own. This is what makes the "
				+ "client's MOD SETTINGS page work; with it off the mod is controlled only from in game.");
			SoundboardEnabled = cfg.Bind("Soundboard", "Enabled", true,
				"HEAR the soundboard. Switch this off and other members can no longer make sound come out of your headset — you can still trigger clips yourself.");
			SoundboardVolume = cfg.Bind("Soundboard", "Volume", 0.5f,
				"How loud incoming clips are. YOURS, not the sender's: nobody else can turn this up.");
			SoundboardPollSeconds = cfg.Bind("Soundboard", "PollSeconds", 3f,
				"How often the mod asks the server what was triggered. Lower is snappier and chattier.");
			GravityPlayerOff = cfg.Bind("Gravity", "PlayerOff", false,
				"Float: sets YOUR gravity to zero through the SDK's own SetGravityStrength. Local — gravity is simulated per client, so nobody else is affected and there is no API to change theirs.");
			GravityWorldOff = cfg.Bind("Gravity", "WorldOff", false,
				"Also zero Physics.gravity, so loose objects float. Local only, and it can genuinely break a world FOR YOU — lifts, physics puzzles and anything that relies on falling stop working.");
			AudioWatchEnabled = cfg.Bind("Debug", "AudioWatch", false,
				"Record which AudioClips actually play, with the object that played them. Observational "
				+ "only. Always on in debug mode; this is how we found VRChat's real UI sounds.");
			ShowDevTab = cfg.Bind("Debug", "ShowDevTab", true,
				"Show the DEV tab in the mod menu: the capture/dump tools, the live profiler, the "
				+ "config editor and the object browser. Separate from Debug/Enabled on purpose — "
				+ "that one also turns on the profiler and verbose logging, which cost frame time.");

			DebugMode = cfg.Bind("Debug", "Enabled", false,
				"Debug build behaviour: per-module profiler on, health snapshot every 5s instead of 30s, verbose "
				+ "module logging, and Unity warnings captured too. Also switches on automatically when a file "
				+ "named DEBUG exists in BepInEx/VRChatArchiveMod/.");
			QMConsolePink = cfg.Bind("QMConsole", "Pink", "#FF6AD5",
				"Primary theme colour (client pink). Hex.");
			QMConsoleViolet = cfg.Bind("QMConsole", "Violet", "#8143E6",
				"Secondary theme colour (client violet). Hex.");
			UdonLogOverlay = cfg.Bind("UdonLog", "Overlay", true,
				"Draw the Udon console in-world (top-left) as well as in the menu. Local overlay only — nobody else sees it.");
			UdonLogOverlayLines = cfg.Bind("UdonLog", "OverlayLines", 12,
				"How many recent Udon events the in-world overlay shows.");
			RadarRange = cfg.Bind("Radar", "RangeMeters", 50f,
				"Distance from the radar centre to its edge, in metres.");
			RadarSize = cfg.Bind("Radar", "SizePixels", 380f,
				"Radar size on screen, in design pixels for a 1080p screen — it is multiplied by the "
				+ "HUD scale, so it keeps the same on-screen proportion at 1440p and 4K.");
			RadarNames = cfg.Bind("Radar", "ShowNames", true,
				"Write each player's name next to their blip, in their trust colour. Only the players "
				+ "actually inside the radar range are labelled \u2014 the ones clamped to the rim would just "
				+ "pile their names on top of each other.");
			RadarMap = cfg.Bind("Radar", "ShowMap", false,
				"Draw the WORLD under the radar blips, from a camera parked above you. OFF by default: this is the one option here that genuinely costs frames, since it renders the scene a second time.");
			RadarMapResolution = cfg.Bind("Radar", "MapResolution", 256,
				"Size of the map render, in pixels. It is shown inside a small radar, so bigger mostly buys cost rather than detail.");
			RadarMapEveryFrames = cfg.Bind("Radar", "MapEveryFrames", 4,
				"Render the map every N frames instead of every frame. The world does not move much between frames; you do, and your position comes from the blips.");
			RadarMapHeight = cfg.Bind("Radar", "MapCameraHeight", 60f,
				"How high above you the map camera sits, in metres. Too low and a ceiling is all you see.");
			RadarMapOpacity = cfg.Bind("Radar", "MapOpacity", 0.75f,
				"How strongly the map shows through. Lower keeps the blips the loudest thing on the radar.");
			RosterPositions = cfg.Bind("Hud", "RosterPositions", true,
				"Show each player's live X/Y/Z under their name in the PLAYERS panel.");
			OrbitRadius = cfg.Bind("Orbit", "RadiusMeters", 2.0f,
				"How far from the player you circle, in metres.");
			OrbitSpeed = cfg.Bind("Orbit", "DegreesPerSecond", 60f,
				"Orbit speed. Negative values circle the other way.");
			OrbitHeight = cfg.Bind("Orbit", "HeightOffset", 0.5f,
				"Height above the player's feet while orbiting, in metres.");
			SitHeight = cfg.Bind("Orbit", "SitHeightOffset", 0.1f,
				"Clearance above the top of the target's avatar mesh when sitting on them.");
			ObjOrbitCount = cfg.Bind("ObjectOrbit", "Count", 24,
				"How many of the world's props to pull into the ring.");
			ObjOrbitRange = cfg.Bind("ObjectOrbit", "SearchRange", 25f,
				"How far around the centre to look for props, in metres. Nearest are taken first.");
			ObjOrbitRadius = cfg.Bind("ObjectOrbit", "RingRadius", 3f,
				"Radius of the ring the objects fly in, in metres.");
			ObjOrbitSpeed = cfg.Bind("ObjectOrbit", "DegreesPerSecond", 70f,
				"How fast the ring turns. Negative spins the other way.");
			ObjOrbitHeight = cfg.Bind("ObjectOrbit", "HeightOffset", 1.2f,
				"Height of the ring above the centre's feet, in metres.");
			ObjOrbitMaxSize = cfg.Bind("ObjectOrbit", "MaxObjectSize", 4f,
				"Largest object, in metres, that counts as a prop. This is the guard that stops the "
				+ "floor, the walls and the skybox being torn out of the world along with the furniture.");
			ObjOrbitSpin = cfg.Bind("ObjectOrbit", "Tumble", true,
				"Also tumble each object on its own axis while it orbits.");
			ObjOrbitSynced = cfg.Bind("ObjectOrbit", "Synced", false,
				"EVERYONE SEES IT. Takes ownership of real pickups through the SDK's Networking.SetOwner and lets VRChat broadcast their position, the way it already does when a player carries "
				+ "something. Restricted to VRC_Pickup objects that nobody is holding — world geometry, doors and seats are never touched — and every position is restored on stop. OFF by default, because unlike the local mode this one is other people's business too.");

			AllowIl2CppDelegates = cfg.Bind("Compatibility", "AllowIl2CppDelegates", false,
				"Let features build il2cpp delegates through Il2CppInterop. OFF because on this VRChat "
				+ "build that call takes the whole game down with an access violation it is not possible "
				+ "to catch. Turning it on costs full log capture, the diagnostics Unity feed, video-URL "
				+ "detection and avatar-card clicks -- but only turn it on if the crash is fixed.");

			LogCaptureEnabled = cfg.Bind("LogCapture", "Enabled", true,
				"Capture ALL of the game's Unity log output to a file (BepInEx/VRChatArchiveMod/logs). "
				+ "Passive Unity log listener only — never touches Photon or any networking.");
			LogCaptureStackTraces = cfg.Bind("LogCapture", "IncludeStackTraces", true,
				"Also write stack traces for errors/exceptions/asserts.");

			BadAppleLoop = cfg.Bind("BadApple", "Loop", true,
				"Restart Bad Apple from the top when it finishes instead of clearing the chatbox.");
			BadAppleIntervalMs = cfg.Bind("BadApple", "IntervalMs", 0,
				"Milliseconds per chatbox message. 0 = default (200). Frames are baked every 50 ms "
				+ "and sampled, so any cadence keeps the full 3:39 runtime. The spam filter is "
				+ "enforced by the network/receiving clients — if viewers' bubbles freeze or mute, "
				+ "raise this. Experimental floor: 100.");
			BadAppleOscHost = cfg.Bind("BadApple", "OscHost", "127.0.0.1",
				"Host receiving the OSC chatbox messages (VRChat's OSC input).");
			BadAppleOscPort = cfg.Bind("BadApple", "OscPort", 9000,
				"UDP port of VRChat's OSC input (default 9000).");
			BadAppleCharset = cfg.Bind("BadApple", "Charset", "",
				"Glyph ramp used as pixels, lightest to darkest, any length (levels are rescaled). "
				+ "Empty = the baked 32-hanzi ramp measured by tools/pick_charset.py.");

			MenuCaptureInput = cfg.Bind("UI", "CaptureInput", true,
				"While the mod menu is open, keep keyboard/mouse for the mod: frees the cursor, "
				+ "stops VRChat's UI from receiving clicks (so its own menu can't open), and "
				+ "neutralises movement input. Everything is restored when the menu closes. "
				+ "Turn off if it ever conflicts with another mod. NOTE: the Alt free-cursor "
				+ "(AltFreeCursor) overrides this while it is on, so you can walk with the menu up.");

			AltFreeCursor = cfg.Bind("UI", "AltFreeCursor", true,
				"Left Alt detaches the mouse from VRChat so you can move the cursor — and walk — "
				+ "freely, without opening the menu. Tap again to hand control back to the game. "
				+ "While it is on, the menu no longer freezes you in place.");

			UiAccent = cfg.Bind("UI", "AccentPreset", 3,
				"Menu accent color for the CONTROLS (buttons, toggles, sliders): 0=Cyan, 1=Crimson, "
				+ "2=Green, 3=Violet, 4=Amber. Defaults to Violet so the controls match the Archive "
				+ "chrome; the window frame itself is always Archive violet whatever you pick here.");
			UiScale = cfg.Bind("UI", "Scale", 1.0f,
				"Overall menu scale (0.7 - 1.4).");

			SpeedEnabled = cfg.Bind("Movement", "CustomSpeed", false,
				"Override your own walk / run / strafe / jump through VRChat's own per-player setters "
				+ "(the same ones a world's Udon uses). LOCAL only — there is no API to change anybody "
				+ "else's, and nothing is sent. Switching it off restores the world's own values.");

			WalkMod = cfg.Bind("Movement", "WalkMod", false,
				"Take over WALKING speed. Off leaves the world's own value alone.");
			RunMod = cfg.Bind("Movement", "RunMod", false,
				"Take over RUNNING speed (shift).");
			JumpMod = cfg.Bind("Movement", "JumpMod", false,
				"Take over JUMP strength. Some worlds set jump to 0 on purpose — this overrides that.");

			WalkSpeed = cfg.Bind("Movement", "WalkSpeed", 2f,
				"Walking speed. VRChat's default is 2.");
			RunSpeed = cfg.Bind("Movement", "RunSpeed", 4f,
				"Running speed (shift). VRChat's default is 4.");
			StrafeSpeed = cfg.Bind("Movement", "StrafeSpeed", 2f,
				"Sideways speed. VRChat's default is 2.");
			JumpImpulse = cfg.Bind("Movement", "JumpImpulse", 3f,
				"Jump strength. VRChat's default is 3; 0 in worlds where jumping is disabled.");

			FlyEnabled = cfg.Bind("Movement", "Fly", false,
				"Local desktop fly. Left-Ctrl+F toggles. Move: WASD + E/Q (up/down), Shift = faster.");
			NoclipEnabled = cfg.Bind("Movement", "Noclip", false,
				"Pass through walls while flying (disables your collider). Left-Ctrl+N toggles.");
			ClickTpEnabled = cfg.Bind("Movement", "ClickTeleport", false,
				"Hold right mouse + left click to teleport to the surface you're aiming at. Local, self-only.");
			ClickTpMaxDistance = cfg.Bind("Movement", "ClickTeleportMaxDistance", 120f,
				"Maximum click-teleport distance (metres).");
			FlySpeed = cfg.Bind("Movement", "FlySpeed", 10f,
				"Fly speed (units per second).");
			FlyBoostSpeed = cfg.Bind("Movement", "FlyBoostSpeed", 18f,
				"Fly speed while holding Shift.");
			FlyRotateSpeed = cfg.Bind("Movement", "FlyRotateSpeed", 90f,
				"Arrow-key rotation speed while flying (degrees per second). Left/Right = turn, Up/Down = tilt.");
			ArrowRotateEnabled = cfg.Bind("Movement", "ArrowRotate", true,
				"Left/Right arrow keys turn your player (works on the ground, not just while flying). "
				+ "Speed follows RotateSpeed.");

			LoadingScreenEnabled = cfg.Bind("LoadingScreen", "Enabled", true,
				"Draw the classic 2017 VRChat loading screen over the game's own while a world loads.");
			LoadingMusic = cfg.Bind("LoadingScreen", "Music", true,
				"Play the 2017 loading music ('PartiallyOffline', from the real build's loading scene).");
			LoadingMusicVolume = cfg.Bind("LoadingScreen", "MusicVolume", 0.55f,
				"Volume of the loading music, 0 to 1.");

			SpawnSoundEnabled = cfg.Bind("SpawnSound", "Enabled", true,
				"Play a short stinger ('The Spawn Dark Squad') once each time you finish loading into an instance.");
			SpawnSoundVolume = cfg.Bind("SpawnSound", "Volume", 0.6f,
				"Volume of the spawn stinger, 0 to 1.");

			EspEnabled = cfg.Bind("ESP", "Enabled", false,
				"Draw a box around remote players, colored by trust rank. Visualization only — no targeting/tracking.");

			// Its state has disagreed with what the client shows; make every change say where it came from.
			Core.ConfigWatch.Watch(EspEnabled);
			EspBox = cfg.Bind("ESP", "Box", true, "Draw the player box.");
			EspSkeleton = cfg.Bind("ESP", "Skeleton", false,
				"Draw a bone-to-bone skeleton over players (humanoid avatars only) instead of just the flat box.");
			// The box is drawn from head and feet, so it is a guess at the avatar's shape: a giant
			// Bounded on purpose: the point is to save the walk across a room, not to let one key
			// collect every loose object in a world from the spawn point.
			ForceGrabRange = cfg.Bind("Fun", "ForceGrabRange", 30f,
				"How far Force Grab (RightShift+G) will reach for a pickup, in metres. Objects held "
				+ "by another player are never taken.");
			ForceJumpForce = cfg.Bind("Fun", "ForceJumpForce", 8f,
				"Force Jump (RightShift+J, or the client's FUN button): upward launch speed in metres/second. "
				+ "A normal VRChat jump is about 3. Ordinary movement \u2014 others see you jump, nobody else's "
				+ "client is touched. Clamped 1\u201350.");
			ForceGrabAnyObject = cfg.Bind("Fun", "ForceGrabAnyObject", true,
				"Force Grab also takes objects a world locked or never made pickups \u2014 a prop bolted in place, "
				+ "a mesh with no VRC_Pickup. That path is a LOCAL carry: it moves the object on your own screen "
				+ "only, takes no ownership and sends nothing, so it cannot be taken from anyone or change world "
				+ "state. Off restores the old behaviour (real pickups only).");

			// The capsule is a real GameObject cloned from the player's own SelectRegion and lit by
			// HighlightsFX, so it turns and occludes like the avatar. Screen-space shapes cannot.
			EspThroughWalls = cfg.Bind("ESP", "ThroughWalls", true,
				"Draw capsules over the world instead of letting geometry hide them. ON is what makes "
				+ "ESP useful — the player you cannot see is the point. The cost is that capsules no "
				+ "longer hide each other either, so a crowd becomes overlapping glows in mixed "
				+ "colours. Turn it OFF in busy worlds: capsules then sit in the scene properly, "
				+ "occlude one another, and each keeps its own colour.");

			EspCapsule = cfg.Bind("ESP", "Capsule", false,
				"Glowing 3D capsule around each player, in their trust colour, using VRChat's own "
				+ "highlight effect - the one it uses for grabbable objects.");

			EspPortals = cfg.Bind("ESP", "Portals", false,
				"Mark world portals through walls, with their distance.");
			EspItems = cfg.Bind("ESP", "Items", false,
				"Mark grabbable pickups through walls, with their distance.");

			EspName = cfg.Bind("ESP", "Name", true, "Show the player's display name.");
			EspDistance = cfg.Bind("ESP", "Distance", true, "Show distance in meters.");
			EspMaxDistance = cfg.Bind("ESP", "MaxDistance", 0f, "Max draw distance in meters. 0 = unlimited (all ESP types).");
			EspHighlight = cfg.Bind("ESP", "Highlight", false,
				"Glow the outline of each remote player's actual avatar mesh, using VRChat's own "
				+ "highlight effect. Follows their real shape and rotation instead of a flat box. "
				+ "Experimental — off by default.");
		}
	}
}
