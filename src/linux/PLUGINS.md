# Plugin manager

Open **Plugins** to manage HapticLink features and install integration profiles.

## Built-in features

The **Features** view has enable switches, status and Configure buttons for controller rumble, game audio, VRChat OSC, manual patterns, the event bridge, frequency capture, tonal shaping, overlay and game-focus pausing. Connection/output, mixer limits and desktop shortcuts remain core features.

Switches stay synchronized with the original tabs. Bypassing frequency capture keeps the selected cutoffs so they return when enabled again. Source errors appear in the status tooltip; Configure opens the relevant controls.

## Install a profile

1. Choose **Available**, then Install next to CS2 events, Intiface/Buttplug integration or Custom game events. New profiles are installed disabled.
2. Open **Installed → Setup and event mappings**. Follow the instructions and copy the profile address into your game integration or adapter.
3. Enable the profile. This starts the shared Events bridge if needed; it does not connect to a device or arm output. A bridge that was subsequently disabled must be enabled again under Features or Events.
4. Connect to Lovense Remote as usual and arm output when ready. Profiles feed the existing mixer, limits, focus policy and output expiry.

For **CS2**, use Export CS2 game config and save the file in the game's `game/csgo/cfg` folder. Restart CS2 afterward. The exported config uses the profile-specific URI.

For **Buttplug**, use the profile's WebSocket address, connect/scan from the integration and select HapticLink virtual vibration. Vibration protocols v2, v3 and v4.0 are supported as described in [INTEGRATIONS.md](INTEGRATIONS.md).

For **HTTP game events**, send `{"event":"quest_complete"}` to the profile's HTTP address. The name must be in that profile's mappings. Intensity, duration and cooldown come from the saved mapping; per-request level/duration overrides are rejected on profile endpoints. Existing `/event` behavior is unchanged.

Profiles provide HapticLink configuration and mappings. Install any required third-party game mod or integration app separately. The Available list also links to the Intiface app list.

## Edit, update, export and remove

- Expand a profile's event mappings to edit enabled state, intensity, duration or cooldown. Add/remove event names and choose **Save mappings** to apply them.
- **Export profile** saves portable JSON. **Import profile** installs a new ID or updates the existing ID while preserving its enabled choice.
- The bundled catalog offers Update when its version is newer than the installed version. It does not download or execute third-party game binaries.
- **Remove** stops the profile's active pulses/connections. **Undo removal** restores the most recently removed profile and its prior enabled choice. A backup JSON is retained under `plugins/removed` in the settings directory and can be imported later.
- **Reload** reads files edited outside the app. Invalid files are reported while valid profiles stay available. Reloading/updating an active profile clears its held input; a WebSocket integration may need to reconnect.

Profiles use separate endpoints and cooldowns. Disabling/removing one profile clears its input and release tail without clearing other profiles. A Buttplug stop command on a profile stops that profile; the legacy root device's stop command still stops the entire Events input. PANIC stops the complete mixer.

## Custom profile format

Import a UTF-8 JSON file such as:

```json
{
  "schemaVersion": 1,
  "id": "my-game",
  "name": "My game events",
  "version": "1.0.0",
  "description": "Quest and damage feedback from my adapter.",
  "kind": "http-events",
  "setup": "POST named events to the address shown after installation.",
  "rules": [
    { "event": "quest_complete", "enabled": true, "level": 0.6, "durationMs": 500, "cooldownMs": 250 },
    { "event": "damage", "enabled": true, "level": 0.4, "durationMs": 250, "cooldownMs": 100 }
  ]
}
```

Kinds: `http-events`, `cs2`, `buttplug`. IDs use 3–64 lowercase letters/digits/hyphens; reserved filename IDs are rejected. Versions are numeric, for example `1.0.0`. A profile supports up to 64 unique event names, levels 0–1, durations 0–5000 ms and cooldowns 0–10000 ms. Up to 64 profiles can be installed; each file is limited to 128 KiB. Unknown schema fields/kinds are rejected.

Installed files live under `plugins/installed` in HapticLink's settings directory. Enabled choices remain in the existing config. On Windows this is `%LOCALAPPDATA%\HapticLink`; Linux uses `$XDG_CONFIG_HOME/HapticLink` or `~/.config/HapticLink`.
