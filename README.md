# VRChat Archive Mod

A client-side **BepInEx 6 (IL2CPP)** plugin for VRChat — built for preserving and organizing avatars, with a set of local, quality-of-life tools around it.

> **Unofficial** and **local-only**. Not affiliated with VRChat Inc. Loading third-party code into VRChat may violate its Terms of Service — see [Disclaimer](#disclaimer).

---

## Overview

**VRChat Archive Mod** is a client-side BepInEx 6 (IL2CPP) plugin for VRChat, built for one purpose: preserving and organizing avatars, plus a set of quality-of-life tools that make browsing, archiving, and inspecting content easier. It is **local-only by design**. Nothing it does is transmitted to other players in your instance — every visualization, movement tweak, favourite list, and diagnostic runs on your machine and affects only your view. Where it does talk to a network, it talks to the companion VRChat Archive desktop client over `127.0.0.1` or to the project's own site APIs, never to the game's other clients.

The mod is organized around a **module system**: each feature is a self-contained unit implementing a common `IModule` lifecycle contract, registered once at startup and driven by a central `ModuleManager`. Every user-facing setting is a BepInEx `ConfigEntry` bound and documented in a single `ModConfig`, so the whole feature surface can be inspected, edited, and synced from one place — including by the desktop client, which drives the mod through a polling HTTP control channel.

Because VRChat ships obfuscated IL2CPP, a large part of the codebase is defensive: `NativeGuard` verifies that native pointers are actually mapped before they are dereferenced, `FieldOffsetFix` corrects the field-offset slot that Il2CppInterop reads so field access stops crashing the game, and a single gate sits in front of the crash-prone delegate bridge so that when the runtime is unsafe a feature degrades to "unavailable" instead of taking the whole process down. The guiding philosophy throughout is: preserve what would otherwise be lost, keep everything local, and never trade the user's session stability for a feature.

---

## Architecture

The mod is a standard **BepInEx 6 IL2CPP plugin**. `VRChatArchiveModPlugin` is the entry point: on load it initializes configuration, installs the IL2CPP compatibility fixes, registers every feature module, and injects a persistent runtime driver GameObject into the scene.

**Module lifecycle.** `IModule` is the abstract contract every feature implements. It defines lifecycle callbacks — initialize, UI-ready, per-frame `Update`/`LateUpdate`/`FixedUpdate`, `OnGUI`, scene-loaded, and shutdown. `ModuleManager` is the central registry and per-frame dispatcher: it iterates the registered modules each frame, wraps every callback in an exception guard (with rate-limited error logging so a misbehaving module cannot flood the log or crash the loop), and can attach a per-module `Stopwatch` profiler for cost measurement. `ModRunner` is the IL2CPP-injected `MonoBehaviour` on that persistent GameObject; it pumps the module update loop and re-asserts cursor state across `Update`, `OnGUI`, and `LateUpdate`.

**Configuration.** `ModConfig` is the single source of truth for settings. Every toggle, threshold, and slider (anti-crash limits, ESP/radar options, movement parameters, tags, favourites, UI, and so on) is a BepInEx `ConfigEntry`, bound and documented in one place. This makes the entire feature set introspectable — `ConfigWatch` even subscribes to BepInEx `SettingChanged` to log *who* changed a value (the desktop client, the in-game menu, or an unknown source) and when.

**IL2CPP safety patterns.** VRChat's obfuscated IL2CPP runtime is hostile to naive interop, so several patterns run underneath everything:

- **NativeGuard** — uses `VirtualQuery` to confirm a native/IL2CPP pointer is mapped and readable before dereferencing it, preventing the uncatchable access-violation crashes that occur when code touches a dead proxy.
- **FieldOffsetFix** — locates the correct `FieldInfo` offset slot (verified against four known `System.Delegate` fields) and Harmony-patches `il2cpp_field_get_offset` so field reads return the right offset instead of crashing.
- **NestedTypeFix** — patches nested-type resolution to walk the `Il2CppClass` struct directly (self-declaring the array offset) rather than calling VRChat's renamed native export, restoring features that depend on nested types.
- **Il2CppDelegates / UiClick** — a single gate in front of Il2CppInterop's crash-prone delegate conversion; when the bridge is unsafe it returns null and refuses to wire listeners, so dependent features report "missing" rather than destabilizing the game.

Supporting these are cached accessors (`PlayerRef`, `ApiUsers`, `QuickMenu`), shared visual toolkits (`GuiKit`, `Hud`, `MenuCard`, `Overlay`), and diagnostics plumbing (`FeatureHealth`, `Redact`, `Unwrap`) that the feature modules build on.

---

## Modules

### Core

Foundation modules — the plugin entry, lifecycle, configuration, IL2CPP safety, shared UI toolkits, and the control channel to the desktop client.

- **VRChatArchiveModPlugin** — BepInEx 6 IL2CPP entry point; initializes config, installs the field-offset/nested-type fixes, registers all modules, and injects the runtime driver GameObject.
- **IModule** — abstract lifecycle contract (initialize, UI-ready, update/late/fixed, GUI, scene-loaded, shutdown) every feature implements.
- **ModuleManager** — central registry and per-frame dispatcher, with per-call exception guards, rate-limited error logging, and an optional per-module `Stopwatch` profiler.
- **ModRunner** — IL2CPP-injected `MonoBehaviour` on a persistent GameObject that pumps the module update loop and re-asserts cursor state.
- **ModConfig** — central BepInEx-backed configuration binding and documenting every setting (anti-crash thresholds, ESP/radar, movement, tags, favourites, UI, etc.).
- **NativeGuard** — `VirtualQuery`-checks a native/IL2CPP pointer is mapped and readable before dereference, preventing uncatchable access-violation crashes on dead proxies.
- **FieldOffsetFix** — finds the correct `FieldInfo` offset slot (verified against four `System.Delegate` fields) and Harmony-patches `il2cpp_field_get_offset`.
- **NestedTypeFix** — patches nested-type resolution to walk the `Il2CppClass` struct directly instead of calling VRChat's renamed export.
- **Il2CppDelegates** — single gate in front of Il2CppInterop's crash-prone `ConvertDelegate`, returning null when the bridge is unsafe so dependent features degrade to "missing".
- **UiClick** — funnels all uGUI button/slider listener wiring through one guarded delegate-conversion point, refusing to wire when the delegate bridge is unavailable.
- **PlayerRef** — reflection-based, aggressively cached access to the local player `Component`, `VRCPlayerApi`, and its velocity setter, guarded against dead proxies.
- **ApiUsers** — cached resolver mapping a `VRCPlayerApi` to its `APIUser` (and VRC+/18+/platform badges), cached per player for one second to avoid per-frame reflection cost.
- **QuickMenu** — caches VRChat's QuickMenu and Main Menu canvases so modules find them once instead of scanning every `Transform` each frame.
- **TrustKit** — single source of truth for VRChat trust-rank colours (plus the rainbow signature-user override), shared by ESP, panels, and the PLAYERS tab.
- **FeatureHealth** — thread-safe registry where a module reports its live state (ok/idle/broken) keyed by setting id, serialized into the client sync so a switch can explain why it appears to do nothing.
- **ConfigWatch** — subscribes to BepInEx `SettingChanged` and logs who changed a config value (client, menu/code, or unknown) and when.
- **Redact** — strips secrets (key/password/token/session/licence args and long base64 blobs) out of logs and diagnostics before a user shares them.
- **Unwrap** — unwraps nested `TargetInvocationException`/type-initializer chains to report the real innermost cause and its originating frame.
- **AssetLoader** — loads and caches the mod's branding/icon images (embedded DLL resources) into `Texture2D` for the IMGUI menu, degrading to null on a bad resource.
- **WavAudio** — minimal 16-bit PCM WAV → Unity `AudioClip` decoder shared by loading-screen music and the spawn stinger.
- **GuiKit** — immediate-mode (IMGUI) drawing kit (rounded panels, glows, toggles, sliders, segmented controls) that renders the mod's overlay without touching VRChat's UI.
- **Hud** — shared HUD chrome (palette, resolution-based scaling, panel/header/accent-bar/zebra drawing) so every corner HUD renders consistently.
- **MenuCard** — recipe for turning a cloned VRChat menu card into a mod button/toggle: strip foreign root components, rewire the click, paint aura/rim/label, lay out icon and label.
- **Menu** — full IMGUI mod menu (draggable window, tab rail, stat cards, toggles/sliders/credits), now permanently sealed since its pages moved to the desktop client, while still owning cursor capture, the Alt free-cursor toggle, and the "world scripts blocked" warning.
- **Overlay** — declarative IMGUI menu system where a page is a list of foldouts and widgets each bound directly to a `ConfigEntry`, drawn per frame with no GameObjects.
- **MenuExclusiveModule** — makes the mod's IMGUI menu and VRChat's QuickMenu mutually exclusive, deactivating the QuickMenu canvas while the mod menu is open and restoring it exactly on close.
- **VaAuth** — resolves how VA tag/favourite/soundboard writes authenticate (desktop-client loopback bridge, in-mod login, or none) and performs those authenticated HTTP calls.
- **ModControlModule** — the polling HTTP control channel: the mod polls the client's LocalBridge on `127.0.0.1:8791`, sends its schema/settings/roster/events/Udon-manager payloads, and applies queued commands on the Unity main thread.

### Favorites & Archive

Unlimited local avatar favourites plus the machinery that grafts a real "VRCHAT ARCHIVE" presence into VRChat's own menus. Because VRChat's newer Voyager world/social pages cannot be injected into, some of these render the mod's own overlay grids instead.

- **FavoritesModule** — maintains unlimited avatar favourites in three layers (ids, metadata, disk-cached thumbnails) through the desktop client's bridge, with optimistic local add/remove.
- **KindFavoritesModule** — generalises the favourites pipeline for worlds, users, and groups via a parameterised provider so the FAVORIS tab shows every type with the same cards.
- **UserFavoritesModule** — holds the user-id list behind the social ARCHIVE FAVORITES, refreshed via the client bridge by shape-agnostically scraping `usr_` ids out of the response.
- **WorldFavoritesModule** — holds the world-id list behind the world ARCHIVE FAVORITES, refreshed the same way by scraping `wrld_` ids.
- **ArchiveCategoryModule** — publishes a real "VRCHAT ARCHIVE" category into VRChat's avatar menu by reassigning the collections panel's observable category list, filling its native grid with Archive favourites as native avatar cards.
- **ArchiveHijackModule** — takes over an existing non-VRC+ category by renaming its sidebar row/header and swapping the grid to Archive favourites, feeding ids VRChat fetches itself (with dead-favourite auto-cleanup).
- **ArchiveFavListModule** — injects a synthetic "VRCHAT ARCHIVE" `FavoriteListModel` into `VRC.Core.API.Favorites._avatars`, populated from the Archive entirely in memory with no network request.
- **ArchiveFavGridModule** — renders Archive world and social favourites as the mod's own grafted overlay grid (cloned sidebar row toggling an overlay of cells), since Voyager pages cannot be injected.
- **ArchiveFavButtonModule** — adds cloned "Save/Remove to Archive" and "Get metadata" buttons to the avatar detail pane, and augments Apply to wear-by-id when the client declines cloning.
- **ArchiveSidebarRowModule** — clones a sidebar item as a fallback "VRCHAT ARCHIVE" row opening the mod's FAVORITES tab, removing itself once the native category is published.
- **WorldFavListModule** — defines the `FavListInjector` base plus World/User/Avatar subclasses that borrow a real `FavoriteListModel` by sidebar slot, rename it, and fill it with Archive ids.

### ESP & Visualization

Local, screen-only overlays for reading a room — player highlights, a radar, instance panels, and the mod's menu theming. None of it is visible to anyone else.

- **EspModule** — screen-space player ESP drawing a trust-rank-coloured box, bone skeleton, name, and distance via IMGUI, with frustum culling and per-frame caching.
- **CapsuleEspModule** — draws a glowing 3D capsule around each remote player by cloning VRChat's own SelectRegion mesh (or a scaled primitive fallback) into a transparent object fed to VRChat's `HighlightsFX`, tinted by trust rank.
- **HighlightEspModule** — glows the real geometry of remote players, portals, and pickups using VRChat's own `HighlightsFX` post-effect, resolved and driven entirely by reflection with per-feature self-gating.
- **RadarModule** — corner minimap plotting a trust-rank-coloured dot per nearby player oriented to the local view (forward = up), optionally over the world map with names and an X/Y/Z readout; toggled with Right-Shift+M.
- **RadarMapCamera** — static helper maintaining a disabled, hand-rendered top-down orthographic camera (world geometry only) drawing into a `RenderTexture` for the radar's map background.
- **InstancePanelsModule** — two anchored overlay panels showing the live instance roster (left) and a join/leave/avatar-change/video-URL feed (right) diffed from the shared roster, with optional join-notifier toasts.
- **WingPlayersModule** — appends a live player-roster section into VRChat's native left QuickMenu wing by cloning, stripping, and pooling a styled row per player, each clickable to open that player.
- **MenuSkinModule** — replaces the QuickMenu wallpaper with the Archive image (both crossfade halves) and optionally hides VRChat's translucent veil layers so the picture shows.
- **MenuThemeModule** — recolours the QuickMenu and MainMenu into the Archive's gradient with a throttled timer, adds an inner-glow rim to cards, and restores VRChat's original colours on toggle-off.
- **OverlayMenuModule** — declares the mod's IMGUI overlay-menu pages (Movement, Visuals, Status) once at startup with controls bound directly to their `ConfigEntry`s; toggled with Right-Shift+M.
- **ProfilerHudModule** — top-right HUD listing per-module frame cost (ms/s) alongside fps and worst-frame time, enabling measurement only while shown; toggled with Right-Shift+P.

### Movement & Fun

Self-only locomotion and novelty features. Every movement tool moves *you* through VRChat's own sanctioned player APIs, restoring captured original values when switched off; the fun tools play locally and send nothing to other players (unless a feature explicitly offers a global path).

- **MovementModule** — local desktop fly plus noclip with click-teleport and arrow-key rotation, pinning the player transform each frame and disabling the local player's solid colliders.
- **SpeedModule** — overrides local walk/run/strafe/jump locomotion through VRChat's per-player setters, re-applying ten times a second and restoring captured originals on off.
- **GravityModule** — reversibly disables gravity for yourself (`SetGravityStrength`) and/or the whole world (`Physics.gravity`), re-asserting after respawns and restoring on shutdown, client-side only.
- **ForceJumpModule** — one-shot upward-velocity launch (RightShift+J or a client-supplied force) through `VRCPlayerApi.SetVelocity`, preserving horizontal momentum.
- **ForceGrabModule** — raycasts at a `VRC_Pickup` (or any locked/non-pickup object when enabled) under the crosshair, takes SDK ownership, carries it in front of the camera, and forwards Udon pickup/use events — never grabbing anything held by another player.
- **OrbitModule** — circles the local player around or perches them on top of another player each frame via `TeleportTo`/transform writes (and hotkeys the object-orbit gag), moving only yourself.
- **ObjectOrbitModule** — pulls loose world props into a spinning ring around you or another player, locally by default or synced via `VRC_Pickup` ownership, restoring every object's original state on stop/world change/shutdown.
- **SoundboardModule** — networked soundboard that broadcasts only a *key* to a server feed every mod client polls, then plays the matching embedded WAV locally with the listener's own volume/master toggle and display-name attribution.
- **SpawnSoundModule** — plays an embedded "spawn stinger" WAV once locally each time you finish loading into an instance, as a 2D `AudioSource` that sends nothing.
- **VideoModule** — finds the world's Udon video players and points one at a chosen URL on *your* client only (validated through VRChat's `TryCreateAllowlistedVRCUrl`), never taking ownership or syncing.
- **VideoUrlModule** — injects a URL into the world's video players and plays it *synced* to the whole instance, driving `BaseVRCVideoPlayer.LoadURL` plus the Udon `VRCUrl` variable and play events, taking ownership only on genuinely networked objects.
- **BadAppleModule** — plays *Bad Apple!!* in the VRChat chatbox as shadow-art over OSC (`/chatbox/input`) from DLL-embedded pre-baked frame data, on a background thread with configurable cadence.
- **EraLoadingModule** — draws a recreated VRChat 2017/2018-era loading screen with era music over the modern one, detecting loading passively by watching the game's own loading popup.

### Udon Tools & Anti-Crash

Passive Udon observation, per-client Udon control, and defensive anti-crash/anti-block features. The network-log and Udon-log tools only observe; the anti-crash and anti-block tools only affect what *you* see and never touch anything networked.

- **AntiCrashModule** — throttled-polls loaded avatars via `VRCAvatarDescriptor` and destroys crasher-tier components (excess particle systems, lights, audio sources, cloth, PhysBones, contacts) above configurable thresholds set far over VRChat's own ratings.
- **AntiBlockModule** — locally reveals players whose avatars VRChat force-hides after a block (inferred from a "was active, now hidden" signature), re-enabling only the exact objects VRChat switched off and restoring them on unflag/disable/scene-change, without touching anything networked.
- **UdonLogModule** — Harmony-hooks `UdonBehaviour.RunProgram(string)` to passively log all nearby Udon events in a live ring-buffer console with adaptive flood muting, plus an optional off-by-default client-side crasher guard whose prefix can skip abusive/flooding events on your own client only.
- **UdonManagerModule** — switches a world's Udon behaviours on/off one at a time on your own client, inspects each behaviour's entry points and public variables, edits variables, and runs events locally (with a separate, deliberately explicit global broadcast path), remembering exactly what it disabled so RESTORE undoes only its own changes.
- **NetworkLogModule** — a single receive-side Harmony postfix on `VRCNetworkingClient.OnEvent` that passively records inbound Photon events (code, sender, sampled payload shape) into a console and optional file, snapshotting values immediately and never transmitting, raising, or replaying anything.
- **AssetBundlePatchModule** — Harmony prefix on `UnityWebRequestAssetBundle.GetAssetBundle` that clears the encryption key on every path so VRChat's asset-bundle cache is written in the clear (and optionally redirects to a local cached file), which is what makes local archiving possible.
- **WatchlistModule** — flags configured VRChat user ids with an animated rainbow ESP box and join banner, and independently announces arriving VRChat Archive members with a gradient banner — purely local visualization/alerts built on data the client already receives.

### Tags & Players

Community player-tagging systems and native QuickMenu integration for acting on the player you have selected.

- **VaTagsModule** — the VRChatArchive community player-tagging system: keeps a live instance roster, syncs styled tags keyed by VRChat user id from the site's authenticated `/api/va-tags` endpoint, draws animated nameplate plates, and powers the PLAYERS tab (teleport, clone/wear avatar, add/remove tags).
- **FewTagsModule** — downloads the community FewTags user-tag database and renders each tagged user's tags as plates cloned from the game's own nameplate popup, filtering slurs locally before drawing. *(See [Credits](#credits--third-party) — derived from Fewdys's FewTags.)*
- **UserMenuModule** — adds "Mod Features", "Clone Avatar", and "Copy Avatar Id" cards to VRChat's per-user QuickMenu page, plus an IMGUI submenu (orbit/sit/ring), all acting on the currently selected user.
- **QuickMenuTabModule** — re-purposes VRChat's unused DevTools tab into a native "VRChat Archive" QuickMenu tab with cloned, Launchpad-styled button tiles, navigable sub-pages, and hand-built native-style sliders.
- **QuickMenuConsoleModule** — clones VRChat's FPS/ping debug panel into the Launchpad page to render a themed, native-looking Udon-events console.

### Dev & Probe Tooling

Read-only instrumentation used to reverse-engineer VRChat's obfuscated structures and diagnose issues. These probes create, modify, or raise nothing in-game — they observe and dump. Also included are the offline Python tools used to bake the chatbox art assets.

- **DevToolsModule** — dev instrument providing a worst-frame spike hunter, a live searchable/editable `ModConfig` editor, and an IL2CPP type/member finder, all read-only toward the game.
- **DiagnosticsModule** — writes a compact, size-capped, credential-redacted diagnostics report (machine/settings header, the plugin's own log lines, Unity errors, health snapshots, explicit PROBLEM lines) small enough for a tester to send back.
- **CaptureModule** — on-demand, strictly read-only dumper that writes rich snapshots of live VRChat objects (menu tree, loading popup, audio, Udon programs/entry points, favourites, instance/player metadata, UI API objects) to disk, sending nothing.
- **DelegateProbe** — read-only diagnostic dumping `System.Delegate`'s `FieldInfo` words to compare Il2CppInterop's reported offset against IL2CPP's real one, so a crash can be attributed without writing anything.
- **AutoProbe** — read-only, stopwatch-budgeted reflection probe that gathers the live `FavoriteArea` data layer and the real network-callable Udon event surface in one bounded run.
- **AvatarListProbe** — read-only runtime probe dumping the avatar-menu content sections, item shapes, `DataModel<ApiAvatar>` candidates, the live `FavoriteArea`, and the menu tree so injection code can target VRChat's real (obfuscated) structures.
- **MenuPanelProbe** — read-only, budget-limited probe dumping the live content panels/grids/lists of whatever menu is open, resolving obfuscated component types to find injection anchors.
- **MenuCaptureModule** — automatically and continuously records the structure and cell templates of interesting menu pages as you open them, bounded (node/depth/file caps) and read-only.
- **MenuRecorderModule** — off-by-default module that continuously walks the live menu canvases while browsing and writes each newly-seen active object (path, real component types, rect, TMP label, sprite) exactly once.
- **UiProbeModule** — on-demand (F8) read-only UI-tree dumper listing interactable controls and full canvas trees to locate native VRChat (Voyager) buttons/tabs to clone, plus an F9 panel-probe hotkey.
- **LogCaptureModule** — passive Unity log sink mirroring every game/plugin log line (redacted) to rotated files plus a small filtered menu-only companion log, hooking nothing.
- **AudioWatchModule** — purely observational recorder polling `AudioSource`s for a rising `isPlaying` edge and logging which clips actually played, to identify VRChat's real UI click sound, never playing/stopping/muting.
- **tools/bake_badapple.py** — CLI tool sampling a video into a compact base-36 text-frame grid sized to the measured chatbox constraints.
- **tools/chatbox_probe.py** — CLI tool sending OSC measurement patterns and baked frames to VRChat's chatbox input (UDP `127.0.0.1:9000`) to size the bubble and test cadence.
- **tools/pick_charset.py** — CLI tool building a grayscale glyph ramp by measuring each candidate glyph's ink coverage in a CJK font.

---

## Building

### Prerequisites

- **.NET 6 SDK** (or newer) — the project targets .NET 6.
- **BepInEx 6 (IL2CPP build)** installed against your VRChat installation, providing the loader and the IL2CPP interop layer the plugin runs on.

### Supplied dependencies (not shipped in this repository)

Two directories are deliberately **not included** here and must be supplied locally before the project will build and run:

- **`libs/`** — the proprietary VRChat, Unity, and BepInEx / Il2CppInterop reference assemblies the plugin compiles against (`libs/bepinex/*.dll` and `libs/interop/*.dll`, as referenced by the `.csproj`). These are not ours to redistribute — obtain them from your own BepInEx install and the interop assemblies BepInEx generates against your VRChat, and place them under `libs/`.
- **`ressources/`** — the media embedded as DLL resources (branding images, audio, baked chatbox frames, era-loading textures). This content is third-party / VRChat-owned and is **not redistributed** here (see [Credits](#credits--third-party)); supply your own equivalents, or remove the matching `<EmbeddedResource>` entries from the `.csproj`. The `tools/*.py` scripts regenerate the chatbox art from a source video you provide.

### Build

```bash
dotnet build -c Release
```

### Deploy

Copy the built DLL into your VRChat BepInEx plugins folder, then launch VRChat through BepInEx:

```
<VRChat>/BepInEx/plugins/VRChatArchiveMod.dll
```

The plugin logs to the BepInEx console/log. If the module list does not register, check that `libs/` matched your interop assemblies and that the IL2CPP fixes reported success in the log. (`DEPLOY.bat` in the repo is an example of the author's own copy-to-plugins step — edit the paths for your machine.)

---

## Credits & Third-party

This mod stands on other people's work. Attribution and licensing notes:

- **FewTags** by **Fewdys** — `FewTagsModule` integrates with, and is a close derivation of, the community **FewTags** / *FewTags-Rewrite* mod (its tag database is fetched at runtime, not bundled). Credit for the tag system and its plate technique goes to Fewdys and the FewTags contributors; if you redistribute a build, check FewTags' own license and keep this attribution.
- **Inigo Quilez** — a retired ("liquid") menu overlay in `MenuSkinModule` is a C# port of Íñigo Quílez's domain-warping fbm shader (see [iquilezles.org/articles/warp](https://iquilezles.org/articles/warp)). It is currently dead code; it is kept credited and should retain his attribution if used.
- **Reverse-engineering references** — a few modules (e.g. `CapsuleEspModule`, some IL2CPP-offset work) document techniques observed in other VRChat tools. No third-party code is copied for those; the comments exist so the behaviour can be understood and maintained.
- **Embedded media** — the loading-screen artwork/music (`EraLoadingModule`), soundboard/spawn clips (`SoundboardModule`, `SpawnSoundModule`), and the *Bad Apple!!* chatbox frames (`BadAppleModule`) are the property of their respective owners (VRChat Inc., the respective artists, Team Shanghai Alice, etc.). They are **not distributed** in this repository — see [Building](#building).
- **BepInEx**, **HarmonyX**, and **Il2CppInterop** — the loader and interop foundation this plugin is built on.

---

## Disclaimer

- **Unofficial.** This project is not affiliated with, endorsed by, or supported by VRChat Inc. "VRChat" and related marks belong to their owners.
- **Modding may violate the VRChat Terms of Service.** Loading third-party code into the VRChat client can breach VRChat's ToS and could put your account at risk. Use it with that understanding; you are responsible for how you run it.
- **Local-only and archival by intent.** The mod is designed to run entirely on your own machine for preservation and quality-of-life purposes. It does not transmit anything to other players in your instance — visualizations, movement, and diagnostics are client-side only, and its network traffic is limited to the companion desktop client on loopback and the project's own site APIs. It is not a tool for affecting other users' sessions, and should not be used to do so.
- **No warranty.** Provided as-is. VRChat updates its obfuscated IL2CPP runtime frequently; the compatibility fixes are best-effort and may need updating when the game changes.

---

## License

Released under the [MIT License](LICENSE). © 2026 KaichiSama / Kawaii Studio.

Third-party components and embedded media remain under their own licenses — see [Credits & Third-party](#credits--third-party).
