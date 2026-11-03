# Validation — November 3, 2026

Built with .NET SDK 8.0.427 on Windows x64. Linux binary published for `linux-x64`, self-contained, with .NET runtime 8.0.33 and Avalonia 11.3.22. Original source recovered from the October 9, 2026 C-drive image through a read-only VHDX/NTFS reader; no image contents were modified.

## Passed

- Release compilation with zero warnings and zero errors.
- 28 C# checks: connection to a local fake Remote HTTP server; double-encoded device JSON; disarmed startup; master cap for each motor; finite command expiry; disarm/test/panic behavior; stop commands; real loopback UDP OSC parsing; audio channel energy and band filters; config persistence and per-avatar profiles; all six tabs rendered with Avalonia Headless; OSC panel reconstruction; device-list refresh; GUI shutdown.
- Nine Python tests: effects upload versus playback; delay, duration and repeats; stop and erase; simultaneous effects and gain; unknown effects; indefinite effects; controller enumeration/cleanup; mocked evdev/uinput upload/playback and parent-EOF handling, including release of an exclusive grab.
- UI renders inspected for readable controls and layout. Rendered with the headless Skia backend on Windows, not a native Linux window manager.
- Package inspection: Linux ELF x64 app host, bundled .NET runtime, Linux native rendering libraries, helper scripts and setup documentation. Executable permission bits are set in the tar archive.

## Requires a Linux machine

There is no installed Linux/WSL environment on this host. The binary has not been launched on Linux. Native PipeWire/PulseAudio capture, evdev/uinput kernel behavior and permissions, desktop shortcuts, Steam/Proton controller selection, VRChat prefix discovery and physical-device output still require end-to-end testing on the target distro. The controller bridge is implemented and covered by portable scheduler/protocol mocks; those tests cannot substitute for a real controller.

No commands were sent to real hardware during validation. The recovered Windows installation and its configuration were not changed.

Reproduce checks from the source folder:

```sh
dotnet run --project Tests/HapticLink.Tests.csproj -c Release
python3 -m unittest discover -s Tests -p 'test_*.py' -v
dotnet publish HapticLink.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```
