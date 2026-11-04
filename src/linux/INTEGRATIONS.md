# Game events, Intiface clients, overlay and EQ

HapticLink 1.2 adds an input bridge for integrations that use the Buttplug protocol, a direct HTTP game-event input, Counter-Strike 2 GSI, application audio selection, game-window focus, a transparent desktop overlay and a ten-band haptics EQ. The updated app also builds for Windows with WASAPI output capture and ViGEm/XInput rumble; per-application audio isolation is Linux-only. The original Windows WPF source remains separate and unchanged. See [Windows setup](README-Windows.md).

## Game plugin tabs

CS2 and Overwatch 2 have preconfigured, disabled tabs on first launch. Plugins adds/removes game tabs and imports custom integrations. CS2 installs its GSI config and edits effects directly. Overwatch connects through the separate OverStim screen-detection companion; its tab configures the connection and launches a selected install. See [PLUGINS.md](PLUGINS.md).

## Community discovery and host / port connections

**Plugins → Discover** reads the community catalog, provides setup guides and adds dedicated tabs. For mods with separate IP and port settings, assign the shared address under the game's **Host / port connection** section. This routes root WebSocket connections into that tab rather than bypassing its enable switch and gain. One tab can own that address at a time; dedicated profile addresses remain independent. See [PLUGINS.md](PLUGINS.md).

## Using apps from the Intiface ecosystem

1. Connect HapticLink to Lovense Remote as usual.
2. Open **Events**, enable the source, and note the server address: `ws://127.0.0.1:12346` by default.
3. In the game mod/integration, set its Buttplug/Intiface server address to that address.
4. Connect/scan in the integration and select **HapticLink virtual vibration**.
5. ARM OUTPUT in HapticLink. Incoming vibration appears in the Events meter and mixes with enabled audio, rumble, OSC and manual sources.

The path is **game/mod → HapticLink virtual device → HapticLink mixer → Lovense Remote → device**. HapticLink receives the integration's output; it does not connect to Intiface Central as an output backend or add support for every physical device supported by Intiface Central.

If an integration fixes its port at 12345, close any other server using that port, change HapticLink's port to 12345, and restart the bridge. Integrations running on another computer cannot reach this loopback-only server.

Supported: protocol v2 `VibrateCmd`, v3 `ScalarCmd` with a Vibrate actuator, and v4.0 `OutputCmd` with a Vibrate feature; discovery, scan notifications, pings and version-appropriate stop commands are implemented. V4's feature range is 0..20, while v2/v3 scalar speeds are 0..1. One virtual vibration feature is advertised. Position/linear travel, rotation, sensors, protocol v0/v1, TLS and remote-server hosting are not implemented.

The bridge advertises a ping interval, clears a disconnected/timed-out client's level, and supports at most eight clients. Stop commands clear the Events input and its release tail; they do not panic other enabled sources. Use PANIC to stop the whole mixer. Output remains subject to arming, focus pausing, master cap, auto-stop and command expiry.

This provides protocol compatibility for configurable vibration integrations. It is not a claim that every project in [the app list you supplied](https://github.com/Renpona/awesome-intiface-sfw-list/blob/sfw-list/README.md) has been run or tested. A Windows-only mod still needs Windows or a working compatibility setup. Browser applications may impose their own mixed-content/WebSocket restrictions.

## Direct game-event input

With Events enabled, send JSON to the same port:

```sh
curl -H 'Content-Type: application/json' \
  -d '{"event":"kill"}' \
  http://127.0.0.1:12346/event
```

`kill`, `damage`, `death`, `shot` and `round_win` have editable intensity, duration, enabled and cooldown controls. Add custom names on the Events tab. An integration can supply its own bounded pulse instead:

```json
{"event":"quest_complete","level":0.6,"durationMs":500}
```

Explicit levels must be 0..1 and durations 0..5000 ms. `/status` returns bridge status. This endpoint is a generic input; games need to send telemetry or have an adapter/mod that does so. Audio analysis does not infer named game events.

### Counter-Strike 2

Copy `integrations/gamestate_integration_hapticlink.cfg` into the game's `game/csgo/cfg/` folder, restart CS2 and enable Events in HapticLink. The default file sends to `http://127.0.0.1:12346/cs2`; edit the URI if you change the port.

The adapter compares successive snapshots for the local player's kills, health, deaths, active weapon ammo and a won round. The first snapshot establishes a baseline. Spectated players are ignored. It uses CS2's Game State Integration input and does not inject into the game. Snapshot-level ammo changes can merge very rapid shots; this is not a frame-perfect weapon-event API.

## Application and game audio

Audio capture has three modes:

- **Chosen output device:** capture the selected output monitor.
- **One application:** refresh playing applications and choose a playback stream. The PID is remembered for this running session, with the selected stream preferred while it exists.
- **Selected game window:** capture an audio stream whose reported process ID matches the selected game window.

The source uses `parec --monitor-stream` for application isolation and follows stream/sink changes. Missing target audio produces zero and a waiting status; it never falls back to recording all output in application/game mode. Applications with audio in a separate helper process, or multiple streams, may need explicit application selection. Select again after restarting a process. This feature requires PulseAudio or PipeWire's PulseAudio service.

## Game focus and overlay

On **Overlay**, refresh game windows and select the game. Optional focus pausing silences every output while the game is unfocused or focus cannot be verified, including test pulses. It does not use continuous-output budget while paused.

On X11/XWayland install the window-discovery tools:

```sh
# Debian / Ubuntu
sudo apt install wmctrl x11-utils libxext6
# Fedora
sudo dnf install wmctrl xprop libXext
# Arch
sudo pacman -S wmctrl xorg-xprop libxext
```

The overlay is a separate borderless, transparent, always-on-top window. It shows the selected game, arm/pause/output state, all five source meters and recent event text. Default mode passes mouse input through on Windows/X11. Interactive mode exposes Arm/Panic controls, an EQ button with live equalizer controls, and a draggable header. EQ settings stay synchronized with the main Audio tab. Its offset and opacity are saved. With focus-only display selected, it hides outside the game; interactive mode keeps it visible for adjustment.

It works with windowed and borderless games when the window manager permits it. It is not an injected Discord/Steam overlay and may be covered by exclusive fullscreen. Native Wayland games and compositor-specific restrictions can prevent discovery, positioning, click-through or always-on-top behavior. The supplied Avalonia 11 build uses XWayland. Unknown focus with focus pausing enabled silences output.

Bind desktop custom shortcuts to the app's absolute path with:

```text
HapticLink --toggle-overlay
HapticLink --overlay-interact
HapticLink --panic
```

## Haptics EQ

The **Audio** tab selects the frequencies used to generate haptics. Choose **Full range**, **Bass** (20–180 Hz), **Mids** (180–2500 Hz) or **Treble** (2500–20000 Hz), or set an exact custom range using **From / To (Hz)**. You can also drag the blue edges on the spectrum. The highlighted region is the selected capture band; incoming audio outside it is filtered out with a gradual roll-off at the cutoffs. Changes apply live without restarting capture and are saved.

For example, set **80–140 Hz** to focus on low bass pulses while rejecting most voices and higher-frequency effects. The selection works on the chosen output device or Linux application/game stream. It changes the audio used for haptics, without changing speaker/headphone playback.

**Advanced audio settings** retains the optional ten-band ±12 dB EQ and preamp after frequency selection. Turning tonal shaping off leaves the capture range active. Existing Bass/Treble settings are preserved when upgrading. The overlay's EQ button exposes the same range controls.

Filter coefficients follow the [RBJ audio EQ cookbook](https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html).

## Validation

The official Buttplug C# 5.0.1 client was connected to the bridge during automated tests and successfully discovered the virtual device, sent vibration and stopped it. Raw v2/v3/v4 clients, HTTP events, CS2 snapshot fixtures, output gating, DSP and UI rendering are also checked. See [VALIDATION.md](VALIDATION.md). Native Linux audio isolation, game focus, click-through and actual games/devices still require testing on a Linux desktop.

Protocol references: [connection](https://buttplug.io/docs/spec/identification/), [v3 protocol](https://buttplug.io/docs/spec-v3/spec/identification/), [v4 stops](https://buttplug.io/docs/spec/stop/), [PulseAudio stream capture](https://manpages.debian.org/unstable/pulseaudio-utils/parec.1.en.html).
