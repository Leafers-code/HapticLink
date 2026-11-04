# HapticLink 1.2 — Windows preview

The updated Avalonia app can also be published for Windows. It includes the Intiface-compatible event receiver, CS2 GSI, game-window focus pausing, transparent overlay with interactive EQ, patterns and OSC. It uses NAudio WASAPI for output-device capture and the recovered ViGEm/XInput implementation for controller rumble.

## Run

Extract the Windows `.7z` download with 7-Zip and run `HapticLink.exe`. The self-contained build includes .NET. The local copy requested in this session is in `Documents\HapticLink-1.2`.

Connect to Lovense Remote's Game Mode as before and arm output only when ready. Settings and logs remain in `%LOCALAPPDATA%\HapticLink`; the new app starts disarmed and does not auto-connect. The original recovered Windows WPF source in `src/windows` is preserved separately.

Use **Chosen output device** for Windows audio. WASAPI input is normalized to 48 kHz stereo before the common haptics EQ. Per-application stream isolation is currently Linux-only and fails with an explicit status on Windows; Windows game-focus pausing is available. Windows rumble requires ViGEmBus and may need HidHide so games target the virtual controller. The Linux exclusive-grab option does not apply on Windows.

The overlay works as a topmost desktop window for borderless/windowed games. Enable interactive mode to move it and expand EQ controls; turn interactive mode off for click-through. Exclusive-fullscreen games can cover desktop overlays.

See [INTEGRATIONS.md](INTEGRATIONS.md) for integration addresses and CS2 setup. Build from the repository root:

```powershell
dotnet publish src/linux/HapticLink.csproj -c Release -r win-x64 --self-contained true -o publish/HapticLink-win-x64
```

The shared app currently remains under `src/linux` for continuity. Windows capture/rumble dependencies are included only for a Windows runtime identifier.
