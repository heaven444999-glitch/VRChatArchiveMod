# Changelog

All notable changes to **VRChat Archive Mod** are recorded here.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this project
aims to follow [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).

## Scope of this file, and what it cannot tell you

The public repository was opened at **3.5.0**. Everything before that — and every version between 3.5.0
and 3.9.20 — was developed in a private tree, and none of it left a commit, a tag or a release here.
There are, as of this file, **no git tags and no GitHub releases** in this repository, so there is
nothing to reconstruct an intermediate history from. Rather than invent one, this file documents the two
versions that public history actually evidences, and says plainly where the gap is.

Two consequences worth stating up front:

- Dates below are **commit dates**, not publication dates. No release process produced anything datable.
- The 3.9.20 entry is a **description of the feature surface the public tree contains at that version**,
  not a change-by-change diff against the version immediately before it. Where a genuine 3.5.0 → 3.9.20
  delta is verifiable from the repository itself (files added, files removed, fixes named in the sync
  commit), it is marked as such.

---

## [Unreleased]

Nothing has changed in the shipped code since the 3.9.20 sync: `git diff 0153028..HEAD -- src` is empty,
and so is the diff for `AssetBundlePatch.cs`, `VRChatArchiveMod.csproj` and `tools/`. The commits that
follow it are documentation and repository hygiene only.

### Added

- `docs/img/logo.png`, `docs/img/mod-ingame.webp` and `docs/img/badapple-thumb.jpg`, and a README that
  uses them: a landing page with a "What it does" summary, a screenshots section, and a card linking the
  Bad Apple object show. The video thumbnail is committed rather than hot-linked so GitHub's image proxy
  is not in the path.

### Removed

- `DEPLOY.bat` is no longer tracked, and is now ignored. It was the author's copy-the-DLL-into-plugins
  step and carried absolute paths across three of the author's drives. The README describes the deploy
  step in prose instead. Note that the file is still reachable in this repository's earlier commits;
  removing it from the working tree does not remove it from history.

---

## [3.9.20] — 2026-09-07

Set by the commit *Sync public source with v3.9.20* (`0153028`), which brought the public tree up to the
build that shipped that day. `<Version>` in `VRChatArchiveMod.csproj` and `PluginInfo.Version` in
`src/Plugin.cs` both read `3.9.20`.

This is one squashed sync, not a release. The section below therefore describes **what the tree contains
at 3.9.20**, grouped by subsystem, and then lists the parts of the 3.5.0 → 3.9.20 change that are
verifiable from the repository.

### The feature surface at 3.9.20

The tree holds 114 tracked files: 100 under `src/` (`Plugin.cs`, 36 files in `src/Core/`, 63 in
`src/Modules/`), plus `AssetBundlePatch.cs`, three Python tools in `tools/`, and documentation.
`src/Plugin.cs` → `RegisterModules()` is the authority on what is *live*: a number of the archive
injection modules are present in source but registered commented-out, each with a paragraph explaining
why, and this file does not claim they are active.

**Archiving and favourites** — `FavoritesModule`, `KindFavoritesModule`, `UserFavoritesModule`,
`WorldFavoritesModule`, `ArchiveCategoryModule`, `ArchiveHijackModule`, `ArchiveFavListModule`,
`ArchiveFavGridModule`, `ArchiveFavButtonModule`, `WorldFavListModule`, `CacheWatchModule`,
`AssetBundlePatchModule`, `Core/ArchiveFeed`. Unlimited local favourites in three layers (ids, metadata,
disk-cached thumbnails); several competing techniques for grafting a real "VRCHAT ARCHIVE" presence into
VRChat's own menus, because the newer Voyager pages cannot be injected into and need the mod's own
overlay grid instead; Save/Remove-to-Archive buttons on the avatar detail pane. The Harmony prefix on
`UnityWebRequestAssetBundle.GetAssetBundle` that clears the cache encryption key is what makes local
archiving possible at all.

**QuickMenu UI and theming** — `QuickMenuTabModule`, `UserMenuModule`, `WingPlayersModule`,
`WingLogModule`, `InstancePanelsModule`, `LaunchpadConsoleModule`, `MenuSkinModule`, `MenuThemeModule`,
`OverlayMenuModule`, `VrcPlusBackgroundsModule`, and `Core/PanelSkin`, `Core/SidePanel`,
`Core/MenuDonor`, `Core/MenuCard`, `Core/Toast`. VRChat's unused DevTools tab re-purposed into a native
"VRChat Archive" tab; the live roster on the left wing and the instance log on the right, as panels
**built** beside the wings rather than cloned from a VRChat page — a cloned page carries a `UIPage`, and
VRChat then refuses to open the QuickMenu at all; the Launch Pad's 1024x280 promo-carousel slot
re-purposed into the archiver and cache console; wallpaper and gradient theming with exact restore when
switched off.

**Safety, anti-crash and anti-abuse** — `AntiCrashModule`, `PhotonGuardModule`, `AntiBlockModule`,
`NsfwFilterModule`, `WatchlistModule`, `BlockedByProbeModule`. The anti-crash pass clamps particle,
light, audio, cloth, PhysBone, contact, polygon, material and shader bombs above thresholds set far over
VRChat's own ratings, and journals every clamp so switching it off restores the avatar exactly. Photon
Guard drops hostile **inbound** events (per-code block list, per-actor-and-code rolling rate limit)
before VRChat dispatches them, and never sends, raises, edits or replays anything. Anti-Block locally
re-reveals avatars VRChat force-hides after a block, re-enabling only the exact objects it switched off.
`BlockedByProbeModule` reads `ApiPlayerModeration.FetchAllAgainstMe` to fill the "who blocked me" sets
and draws nothing.

**Visualization and ESP, screen-only** — `EspModule`, `CapsuleEspModule`, `HighlightEspModule`,
`RadarModule`, `RadarMapCamera`, `ProfilerHudModule`, and `Core/GuiKit`, `Core/Hud`, `Core/Overlay`,
`Core/TrustKit`. Screen-space box, skeleton, name and distance ESP with frustum culling; glowing
capsules cloned from VRChat's own SelectRegion mesh fed to `HighlightsFX`; real-geometry highlights for
players, portals and pickups; a trust-rank-coloured radar over a hand-rendered orthographic map camera;
a per-module frame-cost HUD. None of this leaves the local screen.

**Movement, objects and body** — `MovementModule`, `SpeedModule`, `GravityModule`, `ObjectGravityModule`,
`ForceJumpModule`, `ForceGrabModule`, `ForcePickupModule`, `PlayerGrabModule`, `OrbitModule`,
`ObjectOrbitModule`, `GhostModule`, `SelfHideModule`, `MimicPoseModule`, `VoiceMimicModule`,
`VoiceProbeModule`, and `Core/ForceJoin`, `Core/PlayerRef`. Fly and noclip, locomotion overrides
re-applied ten times a second with captured originals restored on off, self and world gravity, a one-shot
`SetVelocity` launch, Force Pickup (which unlocks a world's locked pickups so your own hands take them,
as opposed to Force Grab driving a transform), cooperative player grab and throw where the held player's
own client moves itself, orbit rings, Ghost (disables the local `FlatBufferNetworkSerializer` so nothing
about your body is serialized outbound), Self Hide (renderers off, bundle still loaded), and pose
mimicry through `HumanBodyBones` in `LateUpdate`. `ForceJoin` navigates by id through
`VRC.SDKBase.Networking.GoToRoom` — the same call a world's Udon portal makes; the instance still
decides whether it will have you.

**Media and the object show** — `BadAppleModule`, `MarkModule`, `SoundboardModule`, `SpawnSoundModule`,
`VideoModule`, `VideoUrlModule`, and `Core/BadAppleAudio`, `Core/WavAudio`, `Core/AssetLoader`, plus
`tools/bake_badapple.py`, `tools/chatbox_probe.py` and `tools/pick_charset.py`. *Bad Apple!!* as chatbox
shadow art over OSC from pre-baked embedded frames; `MarkModule` turning the world's loose pickups into
the pixels for the same frames, with the soundtrack as the clock so the picture cannot drift while the
networked mode waits on object ownership; a networked soundboard that broadcasts only a *key* and plays
the matching embedded WAV locally; and two video paths that are deliberately not interchangeable —
`VideoModule` points a world's player at a URL on **your screen only**, through VRChat's own
`TryCreateAllowlistedVRCUrl` so the allowlist applies exactly as it does for a world author, while
`VideoUrlModule` takes ownership and writes the script's **synced** variable, which the world's own
sync then carries to the whole instance.

**Udon and network observation** — `UdonLogModule`, `UdonManagerModule`, `NetworkLogModule`,
`NetSendModule`, and `Core/UdonSymbols`, `Core/Il2CppSeq`. A passive Harmony hook on
`UdonBehaviour.RunProgram(string)` with adaptive flood muting and an off-by-default client-side crasher
guard; per-client Udon behaviour on/off, entry-point and public-variable inspection and editing, local
event execution with a deliberately separate explicit global broadcast path, and a RESTORE that undoes
only its own changes; a receive-side postfix on `VRCNetworkingClient.OnEvent` that records inbound Photon
events without transmitting, raising or replaying anything; and a send-side counter that reports what
this client pushes onto the wire, per second and per event code.

**Diagnostics, IL2CPP survival and desktop-client integration** — `DiagnosticsModule`,
`UiTreeDumpModule`, `ModControlModule`, and `Core/NativeGuard`, `Core/FieldOffsetFix`,
`Core/NestedTypeFix`, `Core/Il2CppDelegates`, `Core/UiClick`, `Core/CrashTrail`, `Core/FeatureHealth`,
`Core/ConfigWatch`, `Core/ConfigRegistry`, `Core/Redact`, `Core/Unwrap`, `Core/ModConfig`,
`Core/ModuleManager`, `Core/ModRunner`, `Core/IModule`, `Core/VaAuth`, `Core/ApiUsers`,
`Core/QuickMenu`. `NativeGuard` `VirtualQuery`-checks pointers before dereference; `FieldOffsetFix`
Harmony-patches `il2cpp_field_get_offset`; `NestedTypeFix` walks `Il2CppClass` directly instead of
calling VRChat's renamed export; one gate sits in front of Il2CppInterop's `ConvertDelegate` so a feature
degrades to "unavailable" rather than killing the process; `CrashTrail` flushes breadcrumbs to disk
before each risky il2cpp call, so an access violation — which throws nothing and unwinds nothing — still
leaves a file whose last line names what died. `ModControlModule` polls the desktop client's LocalBridge
on `127.0.0.1:8791` and applies commands on the Unity main thread, with `ConfigRegistry` as the schema.

**Community tagging** — `VaTagsModule`, `FewTagsModule`, `Core/VaAuth`. The VRChat Archive tagging
system (live roster, styled tags keyed by VRChat user id, animated nameplate plates, the PLAYERS tab
actions) and the FewTags database rendered as plates cloned from the game's own nameplate popup, with
slurs filtered locally before drawing. FewTags is a close derivation of Fewdys's mod; attribution is in
`NOTICE` and in the README's Credits section.

### What other players can see

Most of the surface above is local to your screen, but not all of it, and this file will not claim
otherwise. Opt-in features that act on the shared instance rather than on your own view include
`MarkModule` and `ObjectOrbitModule` in their networked modes (both take SDK ownership of real pickups
and move them for real), `VideoUrlModule` (plays a URL synced to the whole instance),
`VoiceMimicModule`, `BadAppleModule`'s chatbox output (VRChat broadcasts the chatbox to everyone),
`GhostModule` (suppresses your outbound serialization, which changes what others see of you), and
`SoundboardModule` (sends a key and your VRChat display name to this project's own API so other mod
users can play the clip). The module headers in `src/` state this per feature, in several cases in
capitals.

### Added since 3.5.0 (verifiable from the tree)

`src/Modules/` went from 59 files to 63: 18 added, 14 removed.

- New modules: `BlockedByProbeModule`, `CacheWatchModule`, `ForcePickupModule`, `GhostModule`,
  `LaunchpadConsoleModule`, `MarkModule`, `MimicPoseModule`, `NetSendModule`, `NsfwFilterModule`,
  `ObjectGravityModule`, `PhotonGuardModule`, `PlayerGrabModule`, `SelfHideModule`, `UiTreeDumpModule`,
  `VoiceMimicModule`, `VoiceProbeModule`, `VrcPlusBackgroundsModule`, `WingLogModule`.
- New core files (`src/Core/` went from 26 to 36): `ArchiveFeed`, `BadAppleAudio`, `ConfigRegistry`,
  `CrashTrail`, `ForceJoin`, `Il2CppSeq`, `MenuDonor`, `PanelSkin`, `SidePanel`, `Toast`,
  `UdonSymbols`.

### Changed since 3.5.0

- The QuickMenu side panels are **built** rather than cloned. Three earlier attempts cloned one of
  VRChat's own wing pages; each failed for its own measured reason, and a cloned page carries a `UIPage`
  that stops the QuickMenu opening.
- The Bad Apple object show is clocked off the soundtrack's playback position (`Core/BadAppleAudio`)
  instead of `Time.realtimeSinceStartup`, because the networked mode holds on frame 0 while it takes
  ownership of objects and a wall-clock counter drifts away from the music during that wait.
- `Core/ConfigRegistry` was lifted out of the deleted `DevToolsModule` into its own file, and is now the
  backbone the desktop client's settings page walks.
- `Core/Menu` lost its DEV tab along with `DevToolsModule` — the `Tab` enum went from ten entries to
  nine — and shed 433 lines against 132 added. The IMGUI menu itself was already retired before the
  source was published (`Visible` can be set false and never true, so nothing can open it); its pages
  live in the desktop client, and what still runs in that file is cursor capture, the Alt free-cursor
  hold and the "world scripts are blocked" warning.

### Removed since 3.5.0

- The dead probe and capture modules: `ArchiveSidebarRowModule`, `AudioWatchModule`, `AutoProbe`,
  `AvatarListProbe`, `CaptureModule`, `DevToolsModule`, `EraLoadingModule`, `LogCaptureModule`,
  `MenuCaptureModule`, `MenuExclusiveModule`, `MenuPanelProbe`, `MenuRecorderModule`,
  `QuickMenuConsoleModule`, `UiProbeModule`, plus `src/Core/DelegateProbe`.

### Fixed since 3.5.0

Only the three fixes the sync commit names have public evidence; there is no way to attribute any other
fix to a version.

- **MenuCard** — "the label holder" is the TMP's parent, which on cards that have no `TextLayoutParent`
  *is the card*. `LayoutCard` then pulled the card out of its grid and stretched it into a full-width
  plate over the page.
- **UserMenuModule** — donor buttons must be judged on their own state, not on `activeInHierarchy`,
  which is false for every child of a hidden page. The picker kept falling back to VRChat's disabled
  button, so every card came out grey.
- **AntiCrash** — a new amplified-mesh vector: 10142 triangles from 26 vertices is an index buffer
  written to redraw the same points thousands of times, and it slipped under every triangle and material
  budget.

---

## Versions between 3.5.0 and 3.9.20

**Not documented, and not recoverable.** Public history steps straight from 3.5.0 to 3.9.20 in a single
squashed sync commit. Whatever point releases existed in between were built before the source was
published; they left no commit, no tag, no release and no artefact in this repository, so their count,
their numbering, their dates and their contents cannot be established from it. No entry is written for
them, because any such entry would be fabricated.

---

## [3.5.0] — 2026-08-30

Initial public release (`5f08293`), 96 tracked files: `.gitignore`, `LICENSE`, `README.md`,
`VRChatArchiveMod.csproj`, `AssetBundlePatch.cs`, `BADAPPLE_TEST.bat`, `DEPLOY.bat`, the three Python
tools in `tools/`, and `src/` with `Plugin.cs`, 26 files in `src/Core/` and 59 in `src/Modules/`.

### Added

- The module system the project is built on: `IModule` as the abstract lifecycle contract, and
  `ModuleManager` as the registry and per-frame dispatcher that wraps every callback in an exception
  guard with rate-limited error logging.
- `ModConfig` as the single source of truth for settings, every one of them a BepInEx `ConfigEntry`
  bound and documented in one place.
- The Il2CppInterop safety layer: `NativeGuard` and `FieldOffsetFix`, plus `NestedTypeFix` and the
  delegate-conversion gate.
- `libs/` and `ressources/` deliberately excluded from the repository, with the reason recorded in
  `.gitignore` and the README. They are not the project's to redistribute.

### Note on the version number in this release

At `5f08293` the two version strings disagreed: `VRChatArchiveMod.csproj` said `3.5.0` while
`src/Plugin.cs` still had `PluginInfo.Version = "3.4.0"`, so a build from that commit would have printed
*v3.4.0* in the BepInEx log. The commit message and the csproj agree on 3.5.0, and this file follows
them. `0153028` aligned both strings to 3.9.20. There was no 3.4.0 public release.

### Changed

- `c0dab30`, the same day: GitHub was detecting the licence as "Other" because clarifying text followed
  the MIT body. `LICENSE` is now pure MIT and the third-party notes moved into `NOTICE`. No code change.

---

## How entries are cut from here on

Every release from now on gets its own entry.

- Work lands under **[Unreleased]** as it is merged.
- When a release is ready, the version is bumped in **both** places that carry it —
  `<Version>` in `VRChatArchiveMod.csproj` and `PluginInfo.Version` in `src/Plugin.cs` — and they must
  match. The 3.5.0 note above is what happens when they do not.
- The `[Unreleased]` block is renamed to the new version with the release date, and a fresh empty
  `[Unreleased]` is opened above it.
- The release is then cut by pushing an annotated git tag of the form **`vX.Y.Z`** (for example
  `v3.9.21`), which is what the release workflow keys off. A version with no `vX.Y.Z` tag is not a
  release, and does not get an entry here.

Two things this file will keep doing:

- Grouping changes under Keep a Changelog's headings — Added, Changed, Deprecated, Removed, Fixed,
  Security — rather than by module.
- Saying "unknown" where something is unknown. The gap above is the reason this file exists in the shape
  it does.

Note for anyone reading this expecting downloadable builds: there are none here. The project cannot be
built from a fresh clone of this repository, by design — `libs/` (proprietary VRChat, Unity and BepInEx
reference assemblies) and `ressources/` (the 18 embedded media files the `.csproj` lists) are not
redistributable and are not included. See the README's Building section.

[Unreleased]: https://github.com/kawaiistudio/VRChatArchiveMod/compare/0153028...main
[3.9.20]: https://github.com/kawaiistudio/VRChatArchiveMod/commit/0153028b767c5a1845c0729031011bceeb077cea
[3.5.0]: https://github.com/kawaiistudio/VRChatArchiveMod/commit/5f0829345e3376f3de1fe8cd5f0fbd56036a7b09
