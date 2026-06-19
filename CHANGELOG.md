# Changelog

## v1.2.24 - 2026-06-19

- Fixed corrupted online loadout sync by replacing raw byte casts of Cuphead's large weapon/super/charm enum IDs with stable packet wire codes.
- Added a weapon-prefab fallback guard so invalid or stale remote weapon IDs no longer crash Cuphead's weapon manager during shooting, death cleanup, or remote weapon switches.
- Hardened online equip-card closing so either peer can back out of two-player equipment/internal menu states instead of waiting forever on mismatched local ready cards.
- Updated the installer build configuration to avoid the unsigned portable build failing on Windows symlink extraction for electron-builder's code-sign helper.

## v1.2.23 - 2026-04-25

- Replaced the hidden F11 local-dev toggle with an in-game CupHeads Dev Lab overlay.
- Added Dev Lab buttons for starting the same-PC multiplayer simulator, starting and opening save select, stopping the simulator, copying diagnostics, and closing the panel.
- Added clearer in-game notes that true two-window Steam P2P testing still needs two Steam identities or a second PC.
- Updated local dev messaging so testers can distinguish same-PC spawn/input bugs from real Steam lobby/scene-sync bugs.

## v1.2.22 - 2026-04-25

- Added latency-friendly co-op damage: each peer now owns damage to their own player body while the host still owns scenes, saves, boss state, RNG, and progression.
- Added a fallback config switch, `Networking.LatencyFriendlyDamage`, so the older host-authoritative damage model can be restored if needed for testing.
- Softened client position correction while latency-friendly damage is enabled, reducing small rubber-band pulls during tight boss movement.
- Added F11 local dev simulation mode so one PC can test the CupHeads remote-input path with Player One local and Player Two driven through the mod's network-controlled slot.
- Added `Debug.EnableLocalDevSessionHotkey` and diagnostics output for local dev testing.
- Reworked startup splash normalization to force frame-zero/keyframe-friendly H.264 output and add a short first-frame hold so Unity 2017 is less likely to appear to skip the opening second.

## v1.2.21 - 2026-04-24

- Added installer verification for the embedded `CupheadOnline.dll` version, so stale installs such as `1.2.18` fail verification instead of appearing healthy.
- The installer now blocks installation while `Cuphead.exe` is running, preventing Windows from keeping the old loaded DLL in place.
- Improved final installer success text so it reports the exact CupHeads version that should appear in the BepInEx log.

## v1.2.20 - 2026-04-24

- Hardened startup splash playback against Unity 2017 `VideoPlayer` callback loops by detaching the prepare callback as soon as it fires.
- Added splash playback watchdogs so a stuck, stopped, or missing `loopPointReached` event closes the overlay instead of leaving Cuphead on a black screen.
- Kept the existing frame-zero/focus wait behavior from v1.2.19 while making the splash fail open if Unity's media backend acts up.

## v1.2.19 - 2026-04-24

- Fixed a Unity 2017 `VideoPlayer` prepare loop caused by calling `Stop()` inside `prepareCompleted`.
- Added a one-shot playback guard so the startup splash can only start once per boot.
- The splash now waits for the Cuphead window to be focused before preparing and playing, so the intro does not burn its opening seconds while the game is in the background.
- Keeps the black boot gate visible while waiting for focus, then starts the intro from frame zero.

## v1.2.18 - 2026-04-24

- Fixed the startup splash appearing to begin partway through by hiding the video surface until playback starts.
- Rewinds the prepared `VideoPlayer` back to `time = 0` / `frame = 0`, waits one frame, then starts playback from the beginning.
- Keeps the black boot gate visible while the video is preparing so the first visible splash frame is no longer lost behind Unity startup timing.

## v1.2.17 - 2026-04-24

- Changed startup splash playback into a true boot gate: Cuphead is paused and background game audio is muted while the intro video plays.
- Added StartScreen and StartScreenAudio input gates so pressing a key during the splash can no longer trigger the title screen underneath.
- Turned the procedural film-static overlay off by default and moved it to the new `StartupSplash.FilmStaticOverlay` config key.
- Preserved splash audio while game audio is muted by using `AudioSource.ignoreListenerPause`.
- Restored the previous game time scale and audio pause state after the splash ends, skips, errors, or gets destroyed.

## v1.2.16 - 2026-04-24

- Re-encoded the bundled startup splash from HEVC/H.265 1440p60 to Unity-friendly H.264 1080p30 with AAC audio.
- Moved startup splash playback out of plugin `Awake()` so Unity video decoding no longer starts while BepInEx is still patching the game.
- Hardened startup splash errors so unsupported videos stop and destroy immediately instead of fading through a failed media state.
- Documented that replacement splash videos should avoid HEVC/H.265 for Cuphead's Unity 2017 runtime.

## v1.2.15 - 2026-04-24

- Added an optional CupHeads startup splash video player that looks for `BepInEx/plugins/CupheadOnline/Assets/CupHeadsIntro.mp4`.
- Added audio playback for the startup splash through Unity's `VideoPlayer` + `AudioSource` path.
- Added skip support for `Escape`, `Z`, `Enter`, `Space`, controller confirm, controller back, and controller start.
- Added a live film-static overlay with configurable intensity so clean intro videos can still get a Cuphead-style projector/static pass.
- Updated the installer to bundle and repair-copy `StartupSplash/CupHeadsIntro.mp4` into the plugin `Assets` folder.
- Added fail-open guards so missing, unsupported, or slow-to-prepare videos skip safely instead of blocking the game boot.

## v1.2.14 - 2026-04-24

- Added a Battle Assist HUD for battle levels with a live timer, local deaths, retries, parries, and optional boss HP multiplier readout.
- Added QoL hotkeys: `F6` requests a multiplayer resync, `F7` toggles boss health bars, `F9` copies diagnostics to the clipboard, and `F10` toggles Battle Assist.
- Added BepInEx config toggles for the Battle Assist HUD and QoL hotkeys, plus diagnostics output for both settings.
- Expanded the existing death/retry/parry hooks so the Battle Assist HUD also works during solo play without requiring an active Steam session.

## v1.2.13 - 2026-04-24

- Added optional in-battle boss health bars that read Cuphead's live boss health property objects where available, with a DamageReceiver fallback for boss-like enemies.
- Added animated front/lag health fills, short defeated hold timing, and multi-boss stacking for fights with several major targets.
- Added a `ShowBossHealthBars` BepInEx UI config toggle and included the setting in diagnostics.
- Reset boss-bar tracking on scene transitions so stale defeated or fallback max-health values do not bleed between fights.

## v1.2.12 - 2026-04-24

- Fixed remote button-edge handling so `GetButtonDown` and `GetButtonUp` behave like real per-frame inputs instead of repeating until another network packet arrives.
- Added gentle host-authoritative correction for the client's own gameplay slot, reducing drift where host and guest appear to split into separate solo realities.
- Synced `DamageInfo.stoneTime` through host damage events so stun/status-style hits are authorized the same way as normal damage.
- Added stale/invalid guards for host snapshots, save revisions, scene loads, weapon events, damage events, and revive grants to prevent older packets from rewinding newer gameplay.
- Hardened status, loadout, revive, and enemy sync paths against missing player objects during scene transitions.

## v1.2.11 - 2026-04-23

- Registered the deterministic `Rand.GetValue` Harmony patches in the live patch pass so seeded scene RNG now actually drives boss patterns, enemy spawns, and other random gameplay the same way on both peers.
- Added a startup Harmony coverage audit that warns if a new patch class exists in the codebase but was never wired into `Plugin.Awake()`, reducing the chance of silent future desync regressions.
- Rejected stale remote input, player-state, enemy-state, extra-participant, and status packets so older combat snapshots cannot rewind newer gameplay during boss fights or after jitter.
- Fixed the remote input stall-resume path so held buttons do not come back as phantom fresh presses after a brief packet drought.
- Expanded proxy motor state application for remote gameplay slots so dash, duck, hit, grounded, and super-state flags stay closer to the host during active combat.
- Switched enemy HP reflection from a single global cache to a per-enemy-type cache, which prevents one boss or enemy class from poisoning HP sync for every other class loaded later in the run.

## v1.2.10 - 2026-04-19

- Moved remote loadout application back into the remote player init path so boss-fight weapon managers build from the synced loadout instead of being hot-swapped mid-run.
- Stopped replaying lobby loadout packets into active gameplay every few frames, which removes the repeated loadout spam and the weapon-prefab `KeyNotFoundException` death crash path.
- Let the host run the guest through Cuphead's real map and level motor/controller path using remote inputs, while keeping the client-side host slot as a proxy, for more reliable movement, jump, collision, and interaction behavior.
- Removed built-in remote death toggling from raw snapshot packets so damage/status authority stops fighting the real death and revive systems.
- Added PlayerInput button-down and button-up interception for network-controlled slots so jump, confirm, cancel, and other edge-triggered actions stay responsive for the hosted guest.
- Reset transient participant, revive, color, avatar, and damage-authorization state on scene transitions so stale session data does not bleed across maps, menus, or level loads.
- Re-broadcast the selected save slot as part of host recovery bundles, so reconnect and repair flows refresh both the save profile and the actual tracked slot selection.
- Tightened client damage authorization to the intended player receiver, reducing the chance of cross-player damage tokens leaking into the wrong slot.
- Pushed authoritative player status immediately on death and revive so health/death state reaches peers without waiting for slower follow-up updates.

## v1.2.9 - 2026-04-19

- Reworked live input replication so both host and guest continuously mirror gameplay and menu inputs through a single packet stream instead of conflicting map, level, and UI send paths.
- Fixed internal two-player menu drift by accepting remote input frames on both peers, which keeps equip cards, prompts, and shop-style menus in sync and lets both sides back out cleanly.
- Applied remote loadouts directly to the active save/loadout state while connected, so map-side character cards and menu selections start from the same data on both machines.

## v1.2.8 - 2026-04-19

- Added universal local input routing so the active player can switch between keyboard and controller at any time during maps, levels, equip cards, and menus.
- Routed host-side remote menu input through Steam input frames, allowing guest button presses to drive Player Two interactions in overworld bubbles and shop-style internal menus.
- Added fallback glyph text for Player Two prompts so interaction bubbles no longer render with an empty key when Rewired has no direct binding for that slot.

## v1.2.7 - 2026-04-19

- Narrowed guest input remapping so only spawned PlayerInput components swap to the guest's local controls, avoiding global menu/input side effects.
- Added host-authoritative scene-load gating so connected guests cannot accidentally launch their own local map or level transition while waiting for host sync.
- Added stale allowance and disconnect guards around scene, movement, weapon, damage, death/revive, and loadout sync sends for cleaner teardown and recovery behavior.

## v1.2.6 - 2026-04-19

- Fixed Steam multiplayer launches landing both players in separate solo overworld sessions by forcing Cuphead's native two-player flag before map and level player spawning.
- Added client input-slot remapping so Steam guests control the Player Two/Mugman slot with their normal keyboard or controller instead of being stuck on an unassigned local Player Two input.
- Added overworld map player state sync so host and guest map avatars can see each other moving before entering a boss or level.

## v1.2.5 - 2026-04-19

- Added an explicit host launch scene packet when `START GAME` is pressed so guests are pulled into the selected save/map immediately.
- Added automatic client scene-follow from host snapshots, so scene mismatch recovery now tries to move the guest to the host scene instead of requiring manual resync.
- Improved the installer DLL-refresh failure message so blocked writes explain to close Cuphead/Steam and run the installer as Administrator instead of showing a raw `copyfile` error.

## v1.2.4 - 2026-04-19

- Removed the guest ready-up gate from multiplayer starts. The host can now press `START GAME` once a save slot is selected, and the guest follows automatically.
- Simplified the guest lobby flow so guests wait for the host instead of needing to ready up.
- Kept `REQUEST HOST SAVE` as a manual fallback when the guest has not received the host's selected save yet.

## v1.2.3 - 2026-04-19

- Fixed a multiplayer lobby soft-block where the guest could stay stuck on `WAIT FOR SAVE` and never get a `READY UP` action after the host picked a save.
- Added host-side save-selection rebroadcasts while waiting for guest ready, so missed or early save-sync packets are repaired automatically.
- Changed the guest lobby action from a dead `WAIT FOR SAVE` row into `REQUEST HOST SAVE`, giving guests a manual resync button if the host selection has not arrived yet.

## v1.2.2 - 2026-04-19

- Reworked the integrated multiplayer start flow so the host now chooses `SAVE SLOT`, `LEAD`, and `START GAME` directly from the lobby instead of bouncing through Cuphead's solo save menu.
- Fixed the host save-selection sync so choosing the same save again no longer clears the guest's ready state right before launch.
- Improved the guest-ready gate so connected sessions can actually start once the host selection is locked in and the guest has readied up.
- Updated the README to document the new host-led lobby flow and the current character-selection behavior.

## v1.2.1 - 2026-04-19

- Fixed the Electron installer's BepInEx repair flow so it resolves the current Windows asset names instead of falling back to an outdated 404 URL.
- Bundled the BepInEx x64 and x86 repair archives directly into the installer so normal installs no longer depend on a live GitHub download.
- Added stronger installer timeout and progress handling around the BepInEx fallback download path so stalled downloads fail clearly instead of looking frozen.
- Updated the installer copy and README to reflect the new bundled BepInEx repair behavior.

## v1.2.0 - 2026-04-17

- Added optional boss HP scaling by active player count for battle levels, with a configurable per-extra-player modifier in the BepInEx config and the feature disabled by default.
- Added character-aware session diagnostics and HUD details so sessions can report Cuphead, Mugman, and Ms. Chalice more clearly, including DLC-aware save summaries.
- Added boss-scaling status to exported diagnostics and session panels so balancing issues are easier to verify and report.
- Updated the README to document DLC character support, optional boss scaling, and the current hard limit that live gameplay still follows Cuphead's native two-player runtime.

## v1.1.1 - 2026-04-17

- Upgraded the Electron installer so every install acts like a repair pass: it refreshes the bundled `CupheadOnline.dll`, repairs BepInEx only when needed, and verifies the final setup before finishing.
- Added automatic cleanup for legacy `LiteNetLib.dll` leftovers from the older UDP transport so stale files do not stick around in the plugin folder or packaged `dist` output.
- Polished the installer UI with clearer repair/update messaging, install-plan summaries, final verification feedback, and better wording around what the installer actually refreshes.
- Updated the README feature list and install notes to match the current save-sync, session, and self-repair installer workflow.

## v1.1.0 - 2026-04-17

- Added host/guest save compatibility checks so the mod now compares the chosen slot, map progress, DLC usage, coins, and completion before a run starts.
- Added periodic host session snapshots for reconnect recovery and lightweight desync warnings, plus stronger host-side enemy state broadcasting during live play.
- Added a new in-game session panel that can be viewed while paused or toggled with `F8`, including session stage, save/sync status, and local deaths, retries, and parries.
- Upgraded the connection HUD and multiplayer menu with live stage summaries, compatibility warnings, and richer session diagnostics.
- Added run-stat tracking patches for retries, parries, and player deaths to make playtesting and debugging easier.

## v1.0.3 - 2026-04-17

- Added a host-led `OPEN SAVE SLOT` flow so the multiplayer connection can stay alive while the host returns to Cuphead's normal save selection screen.
- Added save-slot and non-level scene sync so the guest follows the host into the chosen map or intro flow more reliably.
- Added a `COPY LOBBY ID` action in the multiplayer menu and automatically copy the lobby ID to the clipboard when a host lobby is created.
- Added a live title-screen footer that shows the mod version and whether the session is connected, waiting in lobby, or ready for the host to press `Start`.
- Upgraded the in-game connection HUD to show host/client role, peer context, lobby shorthand, and session elapsed time alongside ping quality.

## v1.0.2 - 2026-04-17

- Fixed the multiplayer submenu back behavior so `BACK`, `Escape`, and controller `B` cleanly return to the main menu instead of appearing to refresh the submenu.
- Added a permanent multiplayer hint that explicitly says to press `Escape` or controller `B` to go back.

## v1.0.1 - 2026-04-17

- Fixed Steam startup guards so Steam P2P polling only runs after Steamworks is initialized and stops spamming errors when the game is not launched through Steam.
- Fixed the Slot Select reflection crash by resolving the live `SlotSelectScreen` instance before reading non-static fields.
- Reworked the multiplayer and credits menus so they render reliably, respect back/cancel input, and no longer soft-lock or hide the screen.
- Fixed the credits screen formatting by switching it to explicit line placement instead of fragile multiline layout behavior in Unity 2017 UI.
- Fixed the multiplayer submenu layout so the actions stack correctly instead of overlapping on top of each other.
- Added clearer Steam status, richer connection HUD feedback, retry/invite/diagnostics actions, and safer host/join state transitions.
- Removed the obsolete legacy installer artifact and standardized the repo on the Electron web installer workflow.
- Added installer utility actions for opening the Cuphead folder, launching Steam, and verifying the install.
