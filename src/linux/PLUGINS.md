# Game plugins

Plugins are game integrations. Each installed plugin appears as a main tab alongside Audio, Controller and OSC. CS2 and Overwatch 2 are included and preconfigured on first launch, with output disabled until you enable a plugin and arm the mixer.

## Counter-Strike 2

1. Open **CS2** and choose **Install CS2 game config**. Select the game's `game/csgo/cfg` folder; Steam libraries are detected to suggest a location. An existing HapticLink config is backed up before replacement.
2. Restart CS2 and enable the plugin. Kill, damage, death, shot and round-win effects already have levels/durations.
3. Adjust the effect controls and choose **Apply event effects**. The intensity scale applies live to this game's input.

The integration uses Game State Integration and does not inject into the game. The first snapshot is a baseline, spectated players are ignored, and rapid shots may be combined by snapshot timing.

## Overwatch 2

The Overwatch tab receives game-driven vibration from the separate [OverStim](https://codeberg.org/pharmercy/OverStim) companion. Its screen detector and hero-specific responses run in that app.

1. Use **Get OverStim** to reach the official project and install a supported build.
2. Close OverStim, then choose **Configure OverStim**. HapticLink writes its dedicated local WebSocket address, enables Intiface output and disables sensor reads. Other settings are preserved; previous values are backed up in HapticLink's `companion-backups` settings folder.
3. Choose the installed OverStim application. **Launch OverStim** configures the connection, enables this plugin/bridge and opens the app. Select the hero and start the detector there.
4. Connect HapticLink to Lovense Remote and arm output when ready. This tab shows incoming level and connection status, with its own live intensity scale.

Windows setup uses OverStim's Qt settings under `HKCU\Software\OverStim\OverStim\config`. Linux setup updates `$XDG_CONFIG_HOME/OverStim/OverStim.conf` or `~/.config/OverStim/OverStim.conf`; a compatible OverStim build/capture backend is still required. A running OverStim must be closed before applying its connection settings.

The setup keys were checked against [OverStim config](https://codeberg.org/pharmercy/OverStim/src/branch/main/overstim/utils.py), [settings](https://codeberg.org/pharmercy/OverStim/src/branch/main/overstim/settings.py) and [Qt storage conventions](https://doc.qt.io/qt-6/qsettings.html). HapticLink does not bundle OverStim. Live Overwatch detection has not been tested here.

## Add, remove and share game tabs

**Plugins** lists installed game tabs and other available integrations. Open a game directly, remove it, or undo the most recent removal. Removed bundled games are not recreated on every launch. Adding a game installs its ready-made settings without enabling its input or arming output.

An imported custom integration also gets its own tab. Use **Import plugin** for a new plugin or an update with the same ID; existing enabled choices are preserved. Export a plugin under **Share / update** to share its event mappings. Imports configure HapticLink's input; external game mods/apps still require their own installation.

Plugin files are stored under `plugins/installed` in the existing settings directory. Removal retains a restorable copy in `plugins/removed`. Plugin inputs and cooldowns are isolated; disabling/removing a game clears its active input. All inputs still pass through the master cap, focus pause, expiry and auto-stop.

## Custom integration format

Schema 2 adds optional tab and companion metadata; existing schema 1 exports remain readable. The host supports `cs2`, `buttplug` and `http-events`. For example:

```json
{
  "schemaVersion": 2,
  "id": "my-game",
  "name": "My game",
  "tabName": "My game",
  "version": "1.0.0",
  "description": "Events sent by my game adapter.",
  "kind": "http-events",
  "setup": "POST named events to the address in Connection details.",
  "companion": "",
  "rules": [
    { "event": "quest_complete", "enabled": true, "level": 0.6, "durationMs": 500, "cooldownMs": 250 }
  ]
}
```

IDs use 3–64 lowercase letters/digits/hyphens. Reserved filename IDs are rejected. Profiles allow 64 unique rules, levels 0–1, durations 0–5000 ms and cooldowns 0–10000 ms. Up to 64 plugins can be installed, with a 128 KiB limit per file. The only recognized companion is `overstim`; profiles cannot supply arbitrary launch commands. Companion application paths are selected locally and are not included in plugin exports.

HTTP plugin endpoints accept named mappings without caller-provided intensity/duration overrides. The legacy `/event`, `/cs2` and root Buttplug endpoints remain available on Events.

## Audio and frequency EQ

The frequency selector is now built directly into **Audio**. Choose the capture device, select Bass/Mids/Treble or drag/type a custom frequency range, and apply device changes from the same panel. The optional tonal EQ, noise floor, ceiling and source shaping are in **Advanced audio settings**. At smaller window heights the source meters become a compact row so the audio controls stay accessible.
