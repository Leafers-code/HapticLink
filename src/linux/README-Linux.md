# HapticLink for Linux

**Version 1.5 preview:** [Game plugin tabs and setup](PLUGINS.md), plus Original-style compact dark UI with persistent source meters and log; [Intiface/game events, application audio, overlay and frequency-selective haptics EQ](INTEGRATIONS.md). The built-in Intiface-compatible receiver feeds the existing Lovense output; it is not an Intiface Central device backend. These features are in the Linux port, not the recovered Windows WPF source.

This port was made from the original HapticLink C# source recovered from the October 9, 2026 C-drive backup. The original Windows source is preserved separately. The Linux app uses Avalonia, PulseAudio/PipeWire monitor capture, and an optional Python evdev/uinput controller bridge. The existing Lovense LAN client, patterns, OSC/SPS processing, per-avatar settings and per-motor routing are retained.

## Run the packaged app

The `linux-x64` archive includes .NET; you do not need to install the .NET SDK or use Wine. It targets 64-bit Intel/AMD Linux with glibc and a graphical desktop. On a Wayland desktop, install/enable XWayland.

1. Extract the Linux x64 archive from the repository's downloads folder into a permanent folder.
2. Install desktop and audio dependencies. For Debian/Ubuntu:

   ```sh
   sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libgl1 pulseaudio-utils
   ```

   For Fedora:

   ```sh
   sudo dnf install libX11 libICE libSM fontconfig mesa-libGL pulseaudio-utils
   ```

   For Arch:

   ```sh
   sudo pacman -S libx11 libice libsm fontconfig mesa libpulse
   ```

   Audio requires a running PulseAudio server or PipeWire with `pipewire-pulse`. The app uses `parec` and `pactl` and selects output monitors, not your microphone.

3. From the extracted folder, run:

   ```sh
   ./HapticLink
   ```

4. On your phone, enable Lovense Remote → Game Mode. Enter its IP and press Connect. Choose a device or leave All connected devices selected. Press Test 25% to check the link, then choose a source and ARM OUTPUT.

Optional application-menu entry:

```sh
sh linux/install-desktop.sh
```

The package does not auto-connect or arm on launch. Settings save to `$XDG_CONFIG_HOME/HapticLink/config.json`, normally `~/.config/HapticLink/config.json`, with `haptic.log` alongside it. Set `HAPTICLINK_HOME` to override that directory.

## Audio

On the Audio tab, press Refresh output monitors, choose the output you play through, and enable the source. Press Apply / restart audio after changing outputs. Default output is resolved when capture starts; restart after switching the system default. Loudness, Bass and Treble modes and the existing gain/curve/attack/release controls are available. All sound played on that output contributes.

## Controller rumble (optional)

The bridge creates **HapticLink Virtual Gamepad**, forwards input from your physical controller, and receives rumble effects sent to that virtual pad. It supports conventional `FF_RUMBLE`, effect duration, delay, repeats, stop/erase and gain. It does not implement adaptive triggers, HD audio haptics, or forward rumble to the physical motors.

Install evdev for the Python interpreter selected in Settings:

```sh
# Debian / Ubuntu
sudo apt install python3-evdev
# Fedora
sudo dnf install python3-evdev
# Arch
sudo pacman -S python-evdev
```

Your user needs access to `/dev/uinput` and the gamepad's `/dev/input/event*` node. If your distro does not already provide access, the included optional udev rules grant access to uinput and joystick event devices, scoped to a dedicated group and active desktop sessions:

```sh
sudo modprobe uinput
sudo groupadd -f hapticlink
sudo usermod -aG hapticlink "$USER"
sudo install -m 644 linux/70-hapticlink.rules /etc/udev/rules.d/70-hapticlink.rules
sudo udevadm control --reload-rules
sudo udevadm trigger --subsystem-match=misc
sudo udevadm trigger --subsystem-match=input
```

Log out and back in after changing group membership; reconnect the gamepad. To load uinput on future boots, optionally put `uinput` on its own line in `/etc/modules-load.d/hapticlink.conf`. Run the app as your normal desktop user.

On the Controller tab, refresh gamepads, select the physical one, and enable Forward controller input. Optionally enable Grab physical gamepad exclusively to prevent duplicate evdev input; the helper releases the grab when stopped or terminated. Press Apply / restart controller after changes. Reconnect/restart the source if the gamepad disconnects.

Start the bridge before launching the game. In Steam's controller order, select **HapticLink Virtual Gamepad** first. Some games or Steam Input use direct HID access and can still see the physical controller despite an evdev grab; configure that title to use the virtual pad. Rumble capture works only when the game sends conventional rumble to the virtual pad. This path still needs verification with your Linux controller and games.

To remove the optional access rules later, remove `/etc/udev/rules.d/70-hapticlink.rules`, remove your user from the hapticlink group, reload udev and log out/in.

## VRChat on Proton

OSC normally stays on UDP 9001, with the SPS enable heartbeat sent to 9000. Enable OSC in VRChat. Standard Steam, Flatpak Steam and paths from `libraryfolders.vdf` are searched for the VRChat Proton prefix (app ID 438100). Custom prefixes can be set using the OSC folder field; point it at `AppData/LocalLow/VRChat/VRChat/OSC` inside your prefix.

OSCQuery discovery reads sockets belonging to the VRChat process on Linux. If process isolation hides them, enter the OSCQuery HTTP port explicitly. Avatar-change OSC messages also identify the current avatar, and live sensor detection works without reading avatar files. Remember per-avatar settings on the OSC tab as before.

## Stop shortcut

The app always opens disarmed. PANIC stops output and clears the manual level. Disarming also cancels any active test pulse; test pulses respect Master cap and are limited to five seconds. Existing command expiry and auto-stop remain in place.

Bind a desktop custom shortcut (for example Ctrl+Alt+X) to the absolute path:

```text
/path/to/HapticLink --panic
```

`--toggle-arm` can be bound separately. These commands use a local, current-user named pipe and work through the desktop shortcut system on X11 and Wayland. Ctrl+Alt+X / Ctrl+Alt+A also work directly while HapticLink has focus. Custom `HAPTICLINK_HOME` must match between the main app and shortcut command.

## Build from source

Install .NET SDK 8 or newer, then:

```sh
dotnet publish HapticLink.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
chmod +x publish/linux-x64/HapticLink
dotnet run --project Tests/HapticLink.Tests.csproj -c Release
python3 -m unittest discover -s Tests -p 'test_*.py'
```

For 64-bit ARM Linux, use `-r linux-arm64` and test on that device; the included binary is x64. No Windows-specific package references remain in the Linux project. The original README.md describes the Windows version; use this document for Linux setup.

## Validation limits

The port can be compiled and its portable core, OSC/network protocol, audio processing, effect scheduler and interface can be checked on Windows. That does not verify Linux sound-server capture, uinput permissions, controller behavior, Steam/Proton routing, or a physical device end to end. See the included validation report for the checks actually performed. Try the package on your distro and keep `haptic.log` when reporting a problem.

Implementation references: [Avalonia Linux deployment](https://docs.avaloniaui.net/docs/platform-specific-guides/linux), [python-evdev tutorial and force feedback](https://python-evdev.readthedocs.io/en/latest/tutorial.html), [Linux force feedback](https://www.kernel.org/doc/html/latest/input/ff.html), [PulseAudio parec manual](https://manpages.debian.org/unstable/pulseaudio-utils/parec.1.en.html).
