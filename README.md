# CupHeads

Steam P2P multiplayer mod project for Cuphead, built with BepInEx, Harmony, Steamworks P2P, and an Electron-based installer project.

This repository is intended to provide the source code, build scripts, documentation, and development notes for the CupHeads project.

## Repository contents

* `CupheadOnline/` - the BepInEx + Harmony mod source
* `CupheadInstaller/` - the Electron installer application source
* `build.ps1` - root build script for producing local build artifacts
* `tools/audit-hooks.ps1` - Harmony hook audit that verifies every patch target against the game code

## Version compatibility

Both players must run the same CupHeads version.

Since v1.4.0 the connection handshake carries the mod version and a wire-protocol number. If the builds do not match, the connection is refused with a clear message telling both players which versions they have, instead of silently desyncing. Older pre-1.4.0 builds are detected and refused the same way.

## Features

### Multiplayer

* Steam P2P transport for Cuphead through BepInEx + Harmony
* Host, join, invite friend, retry last action, and export bug reports
* Host-led lobby flow with integrated save slot, lead character, and start-game actions
* Host-authoritative scene syncing for level and menu transitions
* Host-authoritative client drift correction to keep the guest aligned with the host
* Automatic host scene-follow so guests are pulled into the selected save or map
* Networked overworld handoff that spawns both native players and routes guest controls through Player Two
* Universal input routing for keyboard and controller during active sessions
* Safer remote button-edge handling for jump, dash, confirm, cancel, and menu actions
* Hybrid co-op authority where the host owns world state while each player can own damage to their own body
* Deterministic cross-machine enemy addressing so boss and enemy state sync matches between both PCs
* Boss health synchronised through the game's own damage path, keeping phase changes and the knockout in step
* Level-entry ready gate: the host briefly holds the fight until the guest's scene finishes loading, with a fail-open timeout
* Automatic reconnect after an unexpected mid-run disconnect, with session state restored from the host's recovery bundle
* Desync sentinel that samples boss HP and player positions, then auto-corrects and resyncs after repeated mismatches
* Plane (shmup) levels use the same host-simulated proxy sync model as run-and-gun levels
* Ping-adaptive jitter buffering for smooth remote movement on slower connections
* Visual-only tracer streaks so the other player's shots stay readable without double-counting damage
* Remote menu input routing for overworld prompts, equip cards, and shop-style menus
* Defensive stale-packet guards for save selection, host snapshots, weapon events, revive grants, damage events, scene loads, and status updates
* Save compatibility checks for mismatched progress, DLC state, or setup
* Stable loadout packet encoding for Cuphead weapon, super, and charm IDs
* Defensive weapon-prefab fallback guards for stale or invalid remote weapon IDs
* Live connection HUD with role, status, session information, and sync warnings
* In-game session panel with `F8` toggle for diagnostics and session state
* Optional boss health bars during battle levels
* Battle Assist HUD with fight timer, deaths, retries, parries, and optional boss HP multiplier readout
* QoL hotkeys:

  * `F6` quick resync
  * `F7` boss health bars
  * `F8` session panel
  * `F9` copy diagnostics
  * `F10` Battle Assist HUD
  * `F11` Dev Lab
  * Hold `C` then `1`-`4` for the comm wheel (WAIT / GO / REVIVE ME / SWELL WORK)
* Local Dev Lab for same-PC simulation of the remote-input path
* Optional startup splash video with audio, skip support, and configurable film-static overlay
* Optional boss HP scaling per extra active player, disabled by default
* Recovery and resync tools for active sessions, including exported diagnostics bundles

### Menu and UX

* Shared vintage UI theme: every overlay uses Cuphead's own menu fonts (resolved from the game at runtime) on 1930s-style card panels with a rounded double ink rule and gold accents
* Comm wheel with four canned messages that pop as themed toasts on both screens
* Team results card on the knockout screen with both players' deaths, parries, and retries
* Lets a knocked-out player steer their revive ghost sideways while waiting for a parry-revive
* Lobby roster shows live ping and a save-synced indicator, and a toast announces when someone joins
* In-game mod health warning on menu scenes if any Harmony hook failed to apply
* Lobby-style multiplayer screen injected into Slot Select
* Cleaner roster, status, and action layout
* Steam readiness shown in-menu
* Runtime player color swatches with in-game tinting
* Dedicated credits screen
* Clear Steam readiness and connection status messaging
* Clipboard helpers for lobby IDs and diagnostics
* Retry and reconnect guidance when Steam sessions fail
* Toggleable gameplay HUD features through the BepInEx config
* Toggleable networking and dev-test settings through the BepInEx config
* Toggleable startup splash settings through the BepInEx config

### Experimental expanded sessions

CupHeads includes experimental support for larger Steam lobbies and tracked extra participants beyond the vanilla two-player setup.

This area is still experimental. Cuphead has many scene-specific scripts, so not every fight, cutscene, platforming section, or scripted event is guaranteed to behave perfectly with more than two active participants.

### Characters and DLC

* Cuphead and Mugman are supported in the main co-op flow
* Ms. Chalice is supported when The Delicious Last Course DLC is installed and her charm/loadout is available
* Save compatibility checks look at DLC world access and DLC progression before the host starts a run

## Current gameplay limitations

Cuphead is deeply built around `PlayerOne` and `PlayerTwo`.

CupHeads adds online co-op support and experimental extra-participant support, but it does not automatically make every scene in the game fully unlimited-player compatible. Custom bosses, cutscenes, scripted events, and special scenes may still need scene-specific work.

## Requirements

* Windows 10 or later
* Cuphead installed through Steam
* .NET SDK for building the mod
* Node.js and npm for building the installer project
* BepInEx 5
* A legitimate Steam copy of Cuphead

## Building from source

### Build the mod only

```powershell
dotnet build .\CupheadOnline\CupheadOnline.csproj -c Release
```

### Audit the Harmony hooks

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\audit-hooks.ps1
```

This verifies every Harmony patch target and reflected member name in the mod source against the decompiled game code, and fails if any hook no longer exists. `build.ps1` runs it automatically before building, so a game update or a typo breaks the build instead of shipping a silently half-working mod.

### Build the full local package

```powershell
.\build.ps1 -Release
```

This produces local build artifacts under the project output folders.

### Build the installer project manually

```powershell
cd .\CupheadInstaller
npm install
npm run dist
```

## Development setup

CupHeads patches the game's menu and gameplay flow through Harmony and uses Steamworks P2P for multiplayer.

The main mod project is located in:

```text
CupheadOnline/
```

The installer project is located in:

```text
CupheadInstaller/
```

The installer source is responsible for detecting the Cuphead installation, preparing the BepInEx setup, refreshing the mod plugin, copying optional assets, cleaning obsolete files from older builds, and verifying the final local setup.

## Custom startup splash

To replace the intro splash video during development, place an MP4 file in the installer source at:

```text
CupheadInstaller/assets/StartupSplash/CupHeadsIntro.mp4
```

The installer copies it into the mod asset folder during setup.

Recommended export settings:

* MP4/H.264 video
* AAC audio
* 30 FPS
* 16:9 aspect ratio
* 3-8 seconds long

Avoid HEVC/H.265 exports because Cuphead's Unity 2017 video player may not decode them correctly.

If you need to disable the startup splash during development, set:

```text
EnableStartupSplash = false
```

under `[StartupSplash]` in:

```text
BepInEx/config/com.cupheadonline.mod.cfg
```

## Multiplayer start flow

1. Host opens `MULTIPLAYER` and creates or joins a Steam lobby.
2. Once connected, the host picks `SAVE SLOT` and `LEAD` inside the multiplayer lobby.
3. The guest stays in the lobby and follows the host automatically.
4. The host presses `START GAME` from the multiplayer lobby to launch the run.

Character choice follows Cuphead's native co-op model. The host chooses the lead character, and the guest becomes the opposite character. The base game does not expose a separate fully independent guest character picker.

If the guest sees `REQUEST HOST SAVE`, press it once to ask the host for a fresh save sync. The host also rebroadcasts the selected save while the lobby is open.

When the host starts, CupHeads sends an explicit launch scene and keeps watching host snapshots. If the guest falls behind or misses the first launch packet, the mod attempts to auto-follow the host scene instead of requiring manual resync.

During a Steam session, guests are mapped onto Cuphead's native Player Two slot. CupHeads remaps the guest's normal keyboard/controller input to that slot automatically.

Keyboard and controller are both treated as live local inputs while a session is active. If a controller wakes up mid-run or Rewired assigns it to the other vanilla slot, CupHeads routes it back to the local Steam player and keeps remote Player Two actions driven by the guest's Steam input frames.

Internal scenes such as Porkrind's shop use the same routing path. The host remains the authority for scene transitions, while guest menu buttons are forwarded so Player Two can back out or interact.

Online equip-card exits are guarded so mismatched two-player equipment/menu states can close cleanly instead of waiting forever for both local cards to report ready on the same machine.

## Local dev testing

Press `F11` to open the CupHeads Dev Lab.

This mode does not create a Steam lobby. It can mark Player One as the local host player and Player Two as the network-controlled participant, then feed Player Two through CupHeads' `RemoteInputDriver`.

Recommended solo test flow:

1. Launch Cuphead and press `F11`.
2. Choose `START + OPEN SAVE SELECT`.
3. Pick a normal save file.
4. Use Player One controls for Cuphead.
5. Use Player Two/controller bindings for Mugman.
6. Press `F11` again and choose `STOP SIMULATOR` when finished.

The Dev Lab also has `START SIMULATOR`, `COPY DIAGNOSTICS`, and `CLOSE` actions.

This mode is meant for auditing movement, map interactions, equip/shop menus, death/revive behavior, and boss fights without waiting for a second tester. It is not a replacement for a real Steam P2P test because it does not simulate network jitter, packet loss, Steam lobby joining, or two separate machines.

## Two-window testing note

Two Cuphead windows on one Steam account are not a reliable proof of Steam P2P because both processes share one Steam identity.

Use two Windows users, two Steam accounts, or a second PC for real P2P testing.

If Dev Lab fails to spawn or control Player Two, the bug is likely in local spawn/input routing. If Dev Lab works but Steam multiplayer fails, the bug is more likely related to lobby handling, packets, or scene sync.

## Authority model

CupHeads uses a hybrid co-op model.

The host remains authoritative for:

* World state
* Save selection
* Scene transitions
* Level starts
* Boss and enemy state
* RNG seeds
* Progression
* Retries
* Menu flow

For player feel, `Networking.LatencyFriendlyDamage` is enabled by default so each peer owns damage to their own player body.

If you need to compare behavior against the older strict host-authoritative damage model, set:

```text
LatencyFriendlyDamage = false
```

under `[Networking]` in:

```text
BepInEx/config/com.cupheadonline.mod.cfg
```

## Tech stack

* BepInEx 5
* Harmony 2
* Steamworks P2P
* Electron
* Node.js
* PowerShell build scripts

## Credits

* Germanized and Sh0kr for the mod
* Made for Daniel
* Special thanks to Internallinked
* BepInEx for the mod framework
* Harmony for patching
* Electron for the installer shell

## Disclaimer

CupHeads is an independent fan project and is not affiliated with Studio MDHR, Cuphead, Microsoft, Valve, or Steam.

This repository is provided for source code, development, and documentation purposes.
