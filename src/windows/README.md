# HapticLink

A Windows desktop app that reads what's happening in your games and drives a Lovense toy over
your local network. No cloud, no account, no game modding — everything stays on the LAN.

- **Source:** `C:\Users\user\Projects\HapticLink`
- **Installed:** `%LOCALAPPDATA%\HapticLink\app\HapticLink.exe` (desktop shortcut: *HapticLink*)
- **Config + log:** `%LOCALAPPDATA%\HapticLink\config.json`, `haptic.log`

---

## Connecting the toy

Uses the Lovense Remote app's **Game Mode** local API — no Lovense desktop app needed.

1. Phone: **Lovense Remote → Discover → Game Mode**, switch it on. It shows a local IP.
2. HapticLink: type that IP in **Connection**, or press **Scan LAN** to find it automatically.
3. Press **Connect**. Your toys appear in the dropdown with battery level.

The app remembers the host and reconnects on launch. It tries plain HTTP on `:20010` first,
then falls back to `https://<dashed-ip>.lovense.club:30010`.

Phone and PC must be on the same network. If the phone sleeps or Game Mode turns itself off,
the connection drops — reconnect and carry on.

**Function** picks which motor the intensity drives — `Vibrate` for the Hush, `All` if you want
every function on a multi-function toy to follow the signal at once.

---

## The four sources

Each source produces a 0–100% signal. They're mixed together (see **Mix mode**), scaled by
**Master cap**, then sent to the toy. Nothing is sent until you press **ARM OUTPUT**.

### Controller rumble
Mirrors the force-feedback a game sends to your gamepad. It works without touching the game:
HapticLink creates a **virtual Xbox pad** (ViGEmBus — already installed on this machine), forwards
your real controller's input through it, and reads back the rumble the game sends to the virtual pad.

The catch: games send rumble to the pad they see *first*. If your physical controller is still
visible, the game rumbles that one and HapticLink sees nothing. Fix it with
[HidHide](https://github.com/nefarius/HidHide/releases) — hide the physical pad from everything
except HapticLink. The status line warns you if 45 seconds pass with no rumble received.

*Heavy motor* / *Light motor* weights let you follow big impacts, fine texture, or both.
*Pad poll rate* (default 500 Hz) is how often the physical pad is read and mirrored — this is
**not** the same as the toy *Update rate* on the left, which paces the Lovense commands.

Note this only helps in games that actually implement rumble. Rust, for instance, barely uses it —
audio is the better source there.

#### Steam Input / DualSense

This machine's controller is a **DualSense** translated by **Steam Input** into an emulated Xbox
360 pad (`root\steamxbox`, `VID_045E&PID_028E`). Two things follow from that:

- Steam only publishes its emulated pad **while a Steam game is focused**. Outside a game there
  is no XInput pad at all. HapticLink therefore rescans every second and picks it up when it
  appears — it can't be detected once at startup.
- Input passes through **two** emulation layers: DualSense → Steam Input → HapticLink → game.
  Steam's own deadzones and response curves are applied before HapticLink sees anything, and
  gyro, touchpad and adaptive triggers don't survive the trip.

Slot order matters: whichever pad occupies XInput slot 0 is the one most games bind to. Start
HapticLink before launching the game and it takes slot 0, with Steam's pad landing on slot 1 —
which is what you want, since the game then rumbles HapticLink's pad.

### Game audio
Loopback-captures your output device, so it works with **every** game with zero per-title setup.

- **Loudness** — overall intensity.
- **Bass** — explosions, engines, impacts, footsteps. Usually the most "game-like" mapping.
- **Treble** — gunfire, UI, high-frequency detail.

*Quiet floor* is the level treated as zero; *Loud ceiling* is the level treated as 100%. If the
signal sits pinned at 100%, lower the ceiling — or raise the floor if it barely moves.

This hears **everything** on that device — voice chat, music, Discord. Pick a dedicated output
device if that matters.

### VRChat OSC
Enable OSC in VRChat once (**Action menu → Options → OSC → Enabled**). After that there is nothing
to set up: wear an avatar with SPS haptics and it works.

- **Auto-detect touch sensors** (on by default) groups the avatar's `OGB/Pen/<name>/...` and
  `OGB/Orf/<name>/...` parameters into *parts* — one plug or socket each — recognised by name, so
  there is nothing to tick and switching avatars needs nothing from you. **Count my own hands too**
  adds the wearer's own `...Self` sources, which is how you test alone. Each part is read the way
  OscGoesBrrr reads it (SPS was designed against that reader, `Sources/OscParts.cs`):
  - `Touch...` and a plug's `FrotOthers` only count while their `...Close` flag is true. The float
    alone already rises from a metre away, so without the gate a hand *near* you buzzes.
  - Most sockets (Blowjob, Handjob, Thighjob, ...) expose only `PenOthersNewRoot` / `NewTip` — how
    near the visiting plug's root and tip are, on 1 m receivers. The plug's length is measured from
    those while it approaches, and depth is how much of that length has gone past the socket once
    the tip reading saturates (> 0.99). Until then the part stays silent, so someone merely standing
    close does nothing. Sockets that also carry the legacy `PenOthers` fall back to it.
- **The status line tells you whether the avatar can work at all**: *"Dragon A T2: 9 SPS parts
  ready — Anal/Anal pen 45% · build-up 20%"*, or *"main 1 has no SPS parts, so it can't drive
  the toy"*. HapticLink learns the avatar from VRChat's change message, or by asking VRChat's
  OSCQuery server at startup, then reads the name and sensor list from the file VRChat writes at
  `LocalLow\VRChat\VRChat\OSC\usr_*\Avatars\<id>.json`.
- **Feel** decides how a sensor becomes strength. A sensor's value is a *position* (how deep, how
  close), so used directly it buzzes flat out for as long as contact is held. **Follow motion**
  blends that toward the sensor's *speed*: each stroke becomes a swell, and stillness settles to
  the **Held-still level** (a share of the position) instead of silence or full blast. 0% is the
  old position-only behaviour. **Motion sensitivity** sets how fast counts as full strength
  (`OscMotionFullSpeed`, sensor travel per second — 3 is a full-depth stroke at 1.5 Hz).
  **Touch strength** caps `Touch...` sensors relative to `Pen...` ones, so a hand is a tease and
  not the same as full contact (`Frot...` sits between, `OscFrotWeight`). These are shared by every
  avatar and apply to auto-detected parts only; a ticked parameter that belongs to a part is ignored
  in the manual list, because its raw value would win the max and undo all of it.
- **Build up over time** starts VRChat output at *Starts at* and climbs to full over roughly *Time
  to full* of continuing contact (faster when it is intense), easing back at two-thirds of that
  pace once you're left alone. It survives avatar changes. `OscBuildUp` / `OscBuildStart` /
  `OscBuildMinutes`; the status line shows the current build-up.
- **Parts → toys** picks which parts drive which toy (`Core/Parts.cs`). Every part is sorted into
  a body zone by name (Penis, Vagina, Anal, Butt, Mouth, Chest, Thighs, Hands, Feet, Other — every
  plug is Penis), every connected toy into a kind by its Lovense name (Hush/Ridge anal, Edge
  prostate, Max/Calor/Gush/Solace/Diamo penis, Lush/Nora/... vaginal, Gemini chest). A toy is driven
  in full by the zones where it is worn and at **Other parts** (`OscSympathy`, default 60 %) by the
  rest; if the avatar has nothing in a toy's own zones, its main parts (Penis/Vagina/Anal) are
  promoted so the toy isn't left with scraps. The green line under the checkbox, and a
  `Parts → toys:` log line whenever it changes, say what was picked. Unticking it, or 100 % Other
  parts, is the old everything-equal behaviour. Hand-ticked parameters belong to no zone and reach
  every toy in full. The toy list is re-read every 15 s, so switching a toy on is noticed.
  - A toy that reports `Vibrate1` + `Vibrate2` (the Edge) gets a channel per motor: the deep motor
    follows Anal/Butt, the entrance motor follows Penis, and with **move ... with depth** on,
    penetration into Anal/Butt travels from the entrance motor to the deep one as it goes in.
    Which of the two is the inner arm isn't documented — **Swap the Edge motors** flips it.
  - With one toy connected and none singled out, commands still go out as a broadcast (no `toy`
    field), exactly as before; the toy is only named when there are two to tell apart.
- **Remember these settings for this avatar** gives the avatar you're wearing its own copy of
  everything on the tab below it (auto-detect options, hand-ticked parameters, the five shaping
  sliders). It comes back whenever you wear that avatar; every other avatar keeps the shared
  settings. Untick it while wearing the avatar to forget it. Stored in config.json as `OscShared`
  and `OscAvatars`; the top-level `Osc` / `OscParams` / `OscAuto*` values are only the live copy.
- **Other parameters (advanced)** is the old manual list, for avatars without SPS: tick something
  that moves when you're touched. Menu toggles and sliders (`Peen`, `PenisErect`) sit at a fixed
  value and just produce a constant buzz. Ticked entries add to the auto-detected ones; the
  highest value wins. Default listen port 9001.

SPS2 avatars keep every `OGB/*` value frozen at 0 until an app sets `OGB_ENABLED`, and VRChat
forgets it on each avatar change. HapticLink sends it to VRChat's input port every 2 s while the
OSC source is on (`OscOgbEnable` / `OscSendPort` in config.json, defaults `true` / `9000`).

Parameter names with spaces arrive with underscores (`Dragon Penis` → `Dragon_Penis`); the list
shows the real on-the-wire address, so tick from the list rather than typing names from Unity.

### Manual + patterns
A plain level slider plus **Pulse / Wave / Ramp / Random** patterns with adjustable period and
depth. Good for testing the link, and works standalone with no game running.

---

## Shaping

Every source has the same five controls, on its own tab:

| Control | What it does |
|---|---|
| **Gain** | Multiplies the signal. >1 makes weak signals reach full strength. |
| **Curve** | Response shape. >1 softens the low end; <1 makes it hotter early. |
| **Deadzone** | Ignores anything below this, so background noise doesn't trigger. |
| **Attack** | How fast it ramps up. Low = punchy, high = smooth. |
| **Release** | How fast it falls. High values ride over gaps between events. |

**Mix mode** combines sources: `Max` (loudest wins — the sane default), `Average`, `Sum`
(clamped), or `Priority` (highest-priority active source wins; Manual outranks Rumble outranks
OSC outranks Audio).

---

## Safety

- **ARM OUTPUT** — nothing reaches the toy until armed. Disarming stops it immediately.
- **PANIC — STOP** — hard stop, disarms, zeroes the manual level. Also `Ctrl+Alt+X` globally,
  so it works from inside a fullscreen game.
- **Master cap** — hard ceiling on output regardless of what the sources do.
- **Auto-stop** — force-stops after N minutes of continuous non-zero output (default 20, slide to
  0 to disable). A full minute at zero resets the budget. Re-arm to continue.
- **Dead-man timeout** — every command carries a 4-second expiry, refreshed while the app runs.
  If HapticLink crashes, is killed, or loses the network, the toy stops on its own within 4
  seconds rather than running indefinitely.

**Hotkeys** (global, work while a game has focus):

| Key | Action |
|---|---|
| `Ctrl+Alt+X` | Panic — stop everything |
| `Ctrl+Alt+A` | Toggle arm |
| `Ctrl+Alt+↑` / `↓` | Manual level ±10% |

---

## Troubleshooting

**"Connected, but no toys reported"** — toy is off or not paired to the phone. Wake it, then
**Refresh toys**.

**Connects but nothing happens** — check you pressed ARM, that Master cap isn't at 0, and that the
source tab shows a moving meter. **Test 25%** bypasses arming to prove the link works.

**Rumble stays at 0%** — the game is talking to your physical pad. Install HidHide and hide it.

**Input feels dead or laggy in game** — check the Rumble status line. It should read
`Mirroring slot N → virtual slot M · ~500 Hz`. If it says *No controller detected*, nothing is
being passed through and the game is reading a frozen pad; with Steam Input that means no Steam
game is focused yet. Slots are re-scanned every second, so it should correct itself.

**Audio meter pinned at 100%** — lower *Loud ceiling*. Pinned at 0% — lower *Quiet floor*, or
check you picked the output device you actually play through.

**OSC list stays empty** — OSC isn't enabled in VRChat, or another app already owns port 9001.

**Toy stutters** — drop **Update rate**. 10 Hz is the sweet spot; the phone's Bluetooth bridge
chokes well before 20.

---

## Testing without a toy
`HAPTICLINK_HOME=<folder>` makes the app keep its config and log there instead of
`%LOCALAPPDATA%\HapticLink`. Point that config's `Host` at a mock Game Mode server on
`127.0.0.1:20010` (answer `GetToys`, log every `Function`), send OSC to UDP 9001, and the whole
chain — parts, routing, per-motor levels, dead-man fields — can be checked and even armed with
nothing physical attached. The mock must speak HTTP/1.1 keep-alive or HttpClient reports
"response ended prematurely".

## Rebuilding

```powershell
cd C:\Users\user\Projects\HapticLink
dotnet publish -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\HapticLink\app"
```

Stop the running app first — the exe is locked while it's open.

.NET 8 WPF. Dependencies: `NAudio` (WASAPI loopback), `Nefarius.ViGEm.Client` (virtual pad).
OSC parsing and the Lovense client are hand-rolled, no extra packages.

### Layout

| File | Role |
|---|---|
| `Toy/LovenseClient.cs` | Game Mode API, endpoint probing, LAN discovery, toy parsing |
| `Core/Engine.cs` | Mixing, smoothing, safety limits, the output loop |
| `Core/Settings.cs` | Config model + JSON persistence |
| `Sources/RumbleSource.cs` | ViGEm virtual pad + XInput passthrough |
| `Sources/AudioSource.cs` | WASAPI loopback capture and band filtering |
| `Sources/OscSource.cs` | UDP listener + OSC 1.0 parser |
| `Sources/ManualSource.cs` | Level and pattern generator |

One implementation note worth keeping: the iOS Remote app returns the toy map **double-encoded**
as a JSON string under `data.toys`, which no published spec documents. `ParseToys` walks the
response and decodes embedded JSON strings rather than special-casing shapes, so it survives
whatever the next app update returns.
