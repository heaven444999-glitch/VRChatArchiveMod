using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using VRChatArchiveMod.Core;
using VRChatArchiveMod.Modules;

namespace VRChatArchiveMod
{
	// Entry point for VRCHAT ARCHIVE MOD as a BepInEx 6 (IL2CPP) plugin.
	// Ported from the MelonLoader-based Munchen client to run on current VRChat.
	[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
	public class VRChatArchiveModPlugin : BasePlugin
	{
		internal static ManualLogSource Logger;
		internal static Harmony HarmonyInstance;

		public override void Load()
		{
			Logger = base.Log;
			Logger.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} loading (BepInEx IL2CPP)...");

			ModConfig.Init(Config);
			HarmonyInstance = new Harmony(PluginInfo.Guid);

			// Before ANY module runs: without this, every type initializer that needs a nested
			// il2cpp type throws, and a type initializer only ever throws once � the failure is
			// cached for the life of the process, so repairing it later would be too late.
			// FIRST: every field access in the process reads through this. Until it is repaired,
			// anything touching a field_* member reads unmapped memory and ends the process.
			FieldOffsetFix.Install();

			NestedTypeFix.Install();


			// Register feature modules here as each wave is ported.
			RegisterModules();
			ModuleManager.InitializeAll();

			// Spin up the persistent runtime driver (per-frame update pump).
			InjectRuntimeDriver();

			Logger.LogInfo($"{PluginInfo.Name} loaded. Protections & QoL port in progress.");
		}

		private static void RegisterModules()
		{
			// Registered first: it subscribes to the log so it captures every module below.
			ModuleManager.Register(new DiagnosticsModule());
			// Instruments only � the spike hunter costs one float compare per frame when idle.
			ModuleManager.Register(new AntiCrashModule());
			// SELF HIDE: your own avatar not drawn on your screen (AntiCrash/SelfHide).
			ModuleManager.Register(new SelfHideModule());
			// FLOAT OBJECTS: gravity off on every pickup body (FUN toggle).
			ModuleManager.Register(new ObjectGravityModule());
			// CUSTOM USERNAME: what the world's Udon scripts are told your name is (local write).
			ModuleManager.Register(new SpoofModule());
			// MENU BACKGROUNDS: the VRC+ backgrounds (parallax included) shown locally. Flips one bool
			// on the game's own BackgroundOption assets — no API model is touched, nothing is sent.
			ModuleManager.Register(new VrcPlusBackgroundsModule());
			ModuleManager.Register(new LaunchpadConsoleModule());
			ModuleManager.Register(new CacheWatchModule());
			// WHO BLOCKED ME: fills the sets the player lists tag with. Draws nothing, asks once.
			ModuleManager.Register(new BlockedByProbeModule());
			// NSFW FILTER: keyword-named renderers on other avatars switched off (PROTECTION toggle).
			ModuleManager.Register(new NsfwFilterModule());
			// Receive-side Photon filter: drops crasher event codes before VRChat ever sees them.
			ModuleManager.Register(new PhotonGuardModule());
			// Send-side counter: what THIS client pushes per second, printed on every scene change.
			ModuleManager.Register(new NetSendModule());
			// TEMPORARY: find what carries player voice on this build. Remove once identified.
			ModuleManager.Register(new VoiceProbeModule());
			ModuleManager.Register(new FewTagsModule());
			ModuleManager.Register(new MovementModule());
			ModuleManager.Register(new SpeedModule());
			ModuleManager.Register(new VideoModule());
			ModuleManager.Register(new OrbitModule());
			ModuleManager.Register(new ObjectOrbitModule());
			// Wear another player's pose (bone rotations + gestures), local VRIK paused so it is what gets sent.
			ModuleManager.Register(new MimicPoseModule());
			// Relay another player's voice packets as your own outgoing voice (nothing moves).
			ModuleManager.Register(new VoiceMimicModule());
			ModuleManager.Register(new ForceGrabModule());
			ModuleManager.Register(new ForceJumpModule());
			// GHOST: hold the local player's network serializer off (frozen for everyone else).
			ModuleManager.Register(new GhostModule());
			ModuleManager.Register(new ForcePickupModule());
			ModuleManager.Register(new PlayerGrabModule());
			ModuleManager.Register(new MarkModule());
			ModuleManager.Register(new MenuThemeModule());
			// (MenuExclusiveModule deleted 2026-09-01: it hid VRChat's QuickMenu behind the mod's
			// TAB menu, and that menu is sealed � there was nothing left for it to do.)
			ModuleManager.Register(new SoundboardModule());
			ModuleManager.Register(new GravityModule());
			ModuleManager.Register(new FavoritesModule());
			// SUPERSEDED by AvatarFavListModule. Two modules writing the same _avatars collection
			// raced each other, and this one injected while the collection was still EMPTY � before
			// VRChat had built its own lists � which is the most likely reason a forced section made
			// every list vanish.
			// ModuleManager.Register(new ArchiveFavListModule());
			// Worlds: an EXTRA list rather than a borrowed slot � the worlds sidebar renders
			// straight from _worlds, so a new entry there actually shows up.
			// WORLD AND USER FAVOURITES ARE THE CLIENT'S JOB NOW � disarmed 2026-08-26.
			//
			// The desktop client's FAVORIS page does all four kinds properly: real cards, filters,
			// thumbnails cached on disk, JOIN / GET VRCW / VRCX / website. Grafting a second, worse
			// version of that into VRChat's own menus earned nothing and cost a grid to keep
			// working against a menu that fights back. The MOD keeps AVATARS only, because wearing
			// one is something only the game can do.
			//
			// These three fed the removed pieces and were left polling the bridge for nobody:
			//   WorldFavoritesModule / UserFavoritesModule -> ids for the grafted grids
			//   KindFavoritesModule                        -> cards for the old TAB FAVORIS tab
			// ModuleManager.Register(new WorldFavoritesModule());
			// ModuleManager.Register(new UserFavoritesModule());
			// ModuleManager.Register(new KindFavoritesModule());
			// DEAD PATH, disarmed 2026-08-26. WorldFavListModule / UserFavListModule inject ids into
			// VRChat's FavoriteArea (ReplaceFavoritesIndexed). The data lands � the log said "filled
			// with 54 member(s)" � but the Voyager worlds/social pages rebuild their grid from a
			// server fetch on selection, so the screen still shows (0). Proven not to render. The
			// working replacement is ArchiveFavGridModule below, which builds our OWN grid (FavCat).
			// WorldFavoritesModule / UserFavoritesModule stay: they are the id SOURCE the grid reads.
			// ModuleManager.Register(new WorldFavListModule());
			// ModuleManager.Register(new UserFavListModule());
			// DISARMED 2026-09-01 with its Favorites/AvatarList switch. It was off by default and
			// never seen to render (the avatar sidebar is composed elsewhere); the AVATARS tab's
			// borrowed category (ArchiveHijackModule) is the working path. This line is now the
			// on/off switch � the module's Enabled reads true.
			// ModuleManager.Register(new AvatarFavListModule());
			// ARCHIVE FAVORITES for worlds + social. This is the WORKING path the note above points at:
			// our own grid, rather than pushing ids into a page that rebuilds itself from the server.
			// It had been commented out along with the dead injectors it replaces, which left the
			// feature with no implementation at all.
			// DISARMED 2026-08-28 (owner) : plus de sections WORLD ni USER dans ARCHIVE FAVORITES.
			// Le mod garde uniquement les FAVORIS AVATARS (via ArchiveHijackModule, plus haut) ;
			// worlds/users sont pris en charge par la page FAVORIS du client desktop.
			// ModuleManager.Register(new ArchiveFavGridModule());
			ModuleManager.Register(new ArchiveFavButtonModule());
			// DISARMED 2026-08-25 � hard crash of the game on opening the avatar menu, with no
			// [ArchiveCat] line in the log at all, i.e. the process died before the module could
			// report anything. That points at the native side: constructing the game's generic
			// Il2Cpp observables/fetchables by hand, or the Harmony patch on the obfuscated
			// selection handler. A crash on opening a menu is not a bug to iterate on live, so it
			// stays off until the cause is identified from the dump.
			// ModuleManager.Register(new ArchiveCategoryModule());
			// Takes over an existing category instead of inventing one � see the file header for
			// why that difference is the whole point.
			ModuleManager.Register(new ArchiveHijackModule());
			ModuleManager.Register(new UdonManagerModule());
			// The desktop client drives the mod. Its OWN module on purpose: hanging it off another
			// module's update would mean disabling that feature silently killed client control.
			ModuleManager.Register(new ModControlModule());
			// (EspModule, the screen-space box/skeleton/name overlay, was removed 2026-09-04: the
			// glows below and the 3D capsule are the ESP now, each with its own switch.)
			// CAPSULE BEFORE HIGHLIGHT, on purpose: OnSceneLoaded runs in registration order, and
			// the capsule module needs HighlightEspModule's effect still resolved to un-light its
			// capsules before that module forgets the effect for the new world.
			// 2D screen ESP (box / name / distance / skeleton): back on 2026-09-04, its own switch (ESP/Enabled).
			ModuleManager.Register(new EspModule());
			ModuleManager.Register(new CapsuleEspModule());
			ModuleManager.Register(new HighlightEspModule());
			ModuleManager.Register(new SpawnSoundModule());
			ModuleManager.Register(new RadarModule());
			ModuleManager.Register(new InstancePanelsModule());
			ModuleManager.Register(new VaTagsModule());
			ModuleManager.Register(new VideoUrlModule());
			ModuleManager.Register(new WatchlistModule());
			ModuleManager.Register(new AntiBlockModule());
			ModuleManager.Register(new UdonLogModule());
			ModuleManager.Register(new NetworkLogModule());
			// Hooks the bundle download, so it wants to be in place before the first avatar loads.
			ModuleManager.Register(new AssetBundlePatchModule());
			// Immediate-mode menu: declared once, drawn per frame, no GameObjects.
			ModuleManager.Register(new OverlayMenuModule());
			// The Archive tab grafted into VRChat's own QuickMenu. It stays: a tab you can reach
			// inside the headset is the one piece of UI the desktop client cannot replace.
			ModuleManager.Register(new QuickMenuTabModule());
			ModuleManager.Register(new UserMenuModule());
			ModuleManager.Register(new WingPlayersModule());
			// The other half of the pair: players in the LEFT wing, the instance log in the RIGHT one.
			ModuleManager.Register(new WingLogModule());
			// DUMP MENU TREE. Registered because it MUST be: its Request() only raises a flag, and the
			// walk happens in OnUpdate — which the manager never calls for a module it does not hold.
			// Unregistered, the button reported success (the client toasts on send, not on completion)
			// and nothing whatsoever happened.
			ModuleManager.Register(new UiTreeDumpModule());
			ModuleManager.Register(new MenuSkinModule());
			ModuleManager.Register(new ProfilerHudModule());
			ModuleManager.Register(new BadAppleModule());
		}

		private static void InjectRuntimeDriver()
		{
			try
			{
				ClassInjector.RegisterTypeInIl2Cpp<ModRunner>();
				var host = new GameObject("VRChatArchiveMod");
				host.hideFlags = HideFlags.HideAndDontSave;
				Object.DontDestroyOnLoad(host);
				host.AddComponent<ModRunner>();
				Logger.LogInfo("Runtime driver injected.");
			}
			catch (System.Exception e)
			{
				Logger.LogError($"Failed to inject runtime driver: {e}");
			}
		}
	}

	internal static class PluginInfo
	{
		public const string Guid = "org.vrchatarchive.mod";
		public const string Name = "VRCHAT ARCHIVE MOD";
		public const string Version = "3.9.30";
	}
}


