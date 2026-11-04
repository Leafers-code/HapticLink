# Validation â€” HapticLink 1.2.1 preview

Built November 3, 2026 with .NET SDK 8.0.427 and Avalonia 11.3.22 on Windows x64. The updated app builds for `linux-x64` and `win-x64`; the original Windows WPF source is unchanged.

## Passed

- Release compilation: zero warnings and errors.
- 81 C# checks, including a real connection from the official Buttplug C# 5.0.1 client: discovery, vibration and StopCmd. Raw v2/v3/v4 clients verify handshakes, capabilities, scanning, command validation, disconnect and missing-ping expiry.
- Real loopback HTTP event requests; invalid-level rejection; pulse expiry; CS2 baseline, local-player kill and spectating fixtures; event source integration with the armed mixer; immediate clearing of release smoothing on stop.
- Existing LAN Remote client with fake hardware, double-encoded JSON, arming, caps, finite command expiry, panic/disarm behavior, UDP OSC and configuration/profile round trips.
- DSP: stereo energy, silence, bass-only capture, custom capture rejection above and below the selected band, live cutoff changes, full-range restore, malformed cutoff bounds, configuration round-trip, approximately 12 dB tonal EQ boost, attenuation and bypass. The Bass preset and exact cutoff controls also update capture settings through the UI.
- Application audio metadata parsing; X11 window geometry/title parsing; focus policy and engine gating; paused output does not consume the continuous-output budget.
- All eight main-window tabs plus the transparent overlay rendered with Avalonia Headless/Skia. Screenshots inspected, including EQ controls and overlay. GUI shutdown and device/profile controls checked.
- The minimum 1040Ã—700 window keeps arm/panic, every tab header and the persistent log inside the viewport. Both minimum and default sizes were rendered and inspected.
- Nine Python controller-bridge tests retained from the earlier version.
- Windows PCM normalization: continuous 44.1â†’48 kHz resampling, mono-to-stereo conversion and opposite-phase float stereo. The Windows app was launched from Documents and its WASAPI output meter observed responding. No real-device connection or arming was performed during that verification.

## Native behavior still to verify

No Linux/WSL runtime is installed on the build host. The Linux executable has not been launched here. Native PipeWire/PulseAudio application isolation, game focus/window discovery, X11 input-shape click-through, compositor placement, uinput/controller behavior, real CS2 sessions and physical-device output require Linux desktop testing. Desktop overlays do not cover every exclusive-fullscreen or native Wayland game.

The official client test establishes protocol interoperability, not validation of every third-party app or game mod. Only vibration is advertised by the bridge. Haptics EQ does not process audible speaker/headphone playback.

No commands were sent to real hardware during these checks. Test config/logs and screenshots use a scratch directory and are not committed as personal runtime data.

```sh
dotnet run --project src/linux/Tests/HapticLink.Tests.csproj -c Release
python3 -m unittest discover -s src/linux/Tests -p 'test_*.py' -v
dotnet publish src/linux/HapticLink.csproj -c Release -r linux-x64 --self-contained true -o publish/HapticLink-linux-x64
```
