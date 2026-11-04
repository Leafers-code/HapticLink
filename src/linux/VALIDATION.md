# Validation — HapticLink 1.5.1 preview

Built November 3, 2026 with .NET SDK 8.0.427 and Avalonia 11.3.22 on Windows x64. The updated app builds for `linux-x64` and `win-x64`; the original Windows WPF source is unchanged.

## Passed

- November 4, 2026: the standalone Windows executable was published with compressed, self-contained .NET bundling. It was launched from a folder containing only the EXE with isolated test settings; the 1.5.1 window loaded disarmed and the bundled community catalog was present in its extracted data. No code behavior changed for this additional packaging format.
- Release compilation: zero warnings and errors.
- 156 C# checks, including a real connection from the official Buttplug C# 5.0.1 client: discovery, vibration and StopCmd. Raw v2/v3/v4 clients verify handshakes, capabilities, scanning, command validation, disconnect and missing-ping expiry.
- Real loopback HTTP event requests; invalid-level rejection; pulse expiry; CS2 baseline, local-player kill and spectating fixtures; event source integration with the armed mixer; immediate clearing of release smoothing on stop.
- Plugin manager: schema validation, install/export/update/reload, persistent enabled choices, invalid-file isolation, profile-specific HTTP/Buttplug/CS2 routes, cooldowns and mapping enforcement, per-profile stop/removal, CS2 baseline resets, config export, preconfigured game initialization, per-game live intensity scaling, dedicated tab removal/restore, schema-one import, CS2 config installation/backups and OverStim configuration text preservation.
- Community discovery: 69-project offline snapshot, live HTTPS refresh (69 projects / 44 game integrations), game/app section parsing, exclusion of deprecated/library/unsafe/duplicate entries, stable URL identity, valid generated profiles, FFXIV repository/setup metadata, project URL validation, persistent cache, corrupt-cache fallback and failed/oversized/redirected refresh preservation.
- Real WebSocket tests for host/port-only clients: assignment persistence, per-tab client/meter/gain, target switching without interrupting dedicated-path clients, clearing old input, disabled/removed-target rejection and explicit legacy-root restoration. Concurrent disconnect disposal is handled during target changes.
- Browser search/add/remove through the UI, disabled dedicated-tab creation, FFXIV repository control and host/port assignment without enabling/arming output. Discovery and setup screens were rendered and inspected.
- Existing LAN Remote client with fake hardware, double-encoded JSON, arming, caps, finite command expiry, panic/disarm behavior, UDP OSC and configuration/profile round trips.
- DSP: stereo energy, silence, bass-only capture, custom capture rejection above and below the selected band, live cutoff changes, full-range restore, malformed cutoff bounds, configuration round-trip, approximately 12 dB tonal EQ boost, attenuation and bypass. The Bass preset and exact cutoff controls also update capture settings through the UI.
- Application audio metadata parsing; X11 window geometry/title parsing; focus policy and engine gating; paused output does not consume the continuous-output budget.
- All ten default main-window tabs plus the transparent overlay rendered with Avalonia Headless/Skia. Screenshots inspected, including EQ controls and overlay. GUI shutdown and device/profile controls checked.
- The minimum 1040×700 window keeps arm/panic, every tab header, the persistent log, integrated Audio cutoff controls and all five CS2 event controls inside the viewport. Both minimum and default sizes were rendered and inspected.
- Removal directly from a game tab and from Discover, including return navigation, Undo, disabled creation and visibility at the minimum window size.
- Scroll regression: the full expanded Controller section fits inside the actual tab viewport; tall Audio settings open with their header visible and the last tonal controls remain reachable. Dropdown height is bounded, with screen-edge flip/resize behavior configured. Persistent scrollbars expose overflowing content.
- New window icon loaded from the embedded multi-resolution ICO; PNG/SVG assets are included for launchers and source editing.
- Nine Python controller-bridge tests retained from the earlier version.
- Windows PCM normalization: continuous 44.1→48 kHz resampling, mono-to-stereo conversion and opposite-phase float stereo. The Windows app was launched from Documents and its WASAPI output meter observed responding. No real-device connection or arming was performed during that verification.

## Native behavior still to verify

No Linux/WSL runtime is installed on the build host. The Linux executable has not been launched here. Native PipeWire/PulseAudio application isolation, game focus/window discovery, X11 input-shape click-through, compositor placement, uinput/controller behavior, real CS2 sessions and physical-device output require Linux desktop testing. Desktop overlays do not cover every exclusive-fullscreen or native Wayland game.

OverStim connection settings and QSettings storage were checked against upstream source and Qt documentation. The Windows registry wrapper, companion launch and live Overwatch screen detection have not been exercised with an installed OverStim/game session here. No OverStim code or binary is bundled.

Individual community game/mod sessions, including FFXIV, have not been live-tested. The community source lists projects and requirements; it is not an executable plugin registry. Mod installation remains in the external project or in-game plugin manager.

The official client test establishes protocol interoperability, not validation of every third-party app or game mod. Only vibration is advertised by the bridge. Haptics EQ does not process audible speaker/headphone playback.

No commands were sent to real hardware during these checks. Test config/logs and screenshots use a scratch directory and are not committed as personal runtime data.

```sh
dotnet run --project src/linux/Tests/HapticLink.Tests.csproj -c Release
python3 -m unittest discover -s src/linux/Tests -p 'test_*.py' -v
dotnet publish src/linux/HapticLink.csproj -c Release -r linux-x64 --self-contained true -o publish/HapticLink-linux-x64
```
