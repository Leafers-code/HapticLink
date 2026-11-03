#!/usr/bin/env python3
"""evdev/uinput controller mirror; JSON lines on stdout, EOF on stdin stops it.

Only FF_RUMBLE is advertised. Uploading an effect does not play it: EV_FF
starts/stops playback, respecting replay delay, duration, repeats and FF_GAIN.
"""
import argparse
import errno
import json
import os
import select
import signal
import sys
import time
from dataclasses import dataclass


@dataclass(frozen=True)
class Effect:
    strong: int
    weak: int
    length: float
    delay: float


class EffectMixer:
    def __init__(self):
        self.effects = {}
        self.playing = {}
        self.gain = 65535

    def upload(self, effect_id, effect):
        self.effects[effect_id] = effect

    def erase(self, effect_id):
        self.effects.pop(effect_id, None)
        self.playing.pop(effect_id, None)

    def play(self, effect_id, repeats, now):
        if repeats <= 0:
            self.playing.pop(effect_id, None)
        elif effect_id in self.effects:
            self.playing[effect_id] = (now, repeats)

    def levels(self, now):
        strong = weak = 0
        for effect_id, (start, repeats) in list(self.playing.items()):
            effect = self.effects.get(effect_id)
            if effect is None:
                self.playing.pop(effect_id, None)
                continue
            elapsed = max(0, now - start)
            cycle = effect.delay + effect.length
            if effect.length > 0:
                if elapsed >= cycle * repeats:
                    self.playing.pop(effect_id, None)
                    continue
                if elapsed % cycle < effect.delay:
                    continue
            elif elapsed < effect.delay:
                continue
            strong += effect.strong
            weak += effect.weak
        return min(65535, strong * self.gain // 65535), min(65535, weak * self.gain // 65535)


def emit(**fields):
    print(json.dumps(fields), flush=True)


def is_gamepad(device, e):
    caps = device.capabilities(absinfo=False)
    keys, axes = caps.get(e.EV_KEY, []), caps.get(e.EV_ABS, [])
    return (not device.name.startswith('HapticLink') and e.BTN_GAMEPAD in keys
            and e.ABS_X in axes and e.ABS_Y in axes)


def gamepads(evdev):
    result = []
    for path in sorted(evdev.list_devices()):
        device = None
        try:
            device = evdev.InputDevice(path)
            if is_gamepad(device, evdev.ecodes):
                result.append({'path': path, 'name': device.name})
        except OSError:
            pass
        finally:
            if device is not None:
                device.close()
    return result


def run(args, evdev):
    e = evdev.ecodes
    if args.list:
        print(json.dumps(gamepads(evdev)), flush=True)
        return
    devices = gamepads(evdev)
    path = args.device or (devices[0]['path'] if devices else '')
    if not path:
        raise RuntimeError('No accessible gamepad. Check device permissions and reconnect it.')
    if args.grab and args.no_passthrough:
        raise RuntimeError('Exclusive grab requires input forwarding.')
    mixer = EffectMixer()
    physical = virtual = None
    grabbed = False
    running = True

    def stop(*_):
        nonlocal running
        running = False

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        physical = evdev.InputDevice(path)
        if not is_gamepad(physical, e):
            raise RuntimeError('Select a physical gamepad event device.')
        original = physical.capabilities()
        caps = {kind: values for kind, values in original.items() if kind in (e.EV_KEY, e.EV_ABS, e.EV_MSC)}
        caps[e.EV_FF] = [e.FF_RUMBLE, e.FF_GAIN]
        virtual = evdev.UInput(caps, name='HapticLink Virtual Gamepad',
                               vendor=physical.info.vendor, product=physical.info.product,
                               version=physical.info.version, bustype=physical.info.bustype,
                               phys='hapticlink/virtual', max_effects=16)
        if args.grab:
            physical.grab()
            grabbed = True
        if not args.no_passthrough:
            # Seed the current state, including held buttons, before the first change.
            for code in physical.active_keys():
                virtual.write(e.EV_KEY, code, 1)
            for code, info in caps.get(e.EV_ABS, []):
                virtual.write(e.EV_ABS, code, info.value)
            virtual.syn()
        emit(status=f'Mirroring {physical.name} → HapticLink Virtual Gamepad (event driven)')
        next_send = 0.0
        while running:
            readers = [physical, virtual, sys.stdin]
            ready, _, _ = select.select(readers, [], [], 0.01)
            for device in ready:
                if device is sys.stdin:
                    if not os.read(sys.stdin.fileno(), 1024):
                        running = False
                    continue
                for event in device.read():
                    if device is physical:
                        if event.type == e.EV_SYN and event.code == e.SYN_DROPPED:
                            raise RuntimeError('Controller input overflow; restart the controller source.')
                        if not args.no_passthrough and (event.type in caps or event.type == e.EV_SYN) and event.type != e.EV_FF:
                            virtual.write_event(event)
                    elif event.type == e.EV_UINPUT and event.code == e.UI_FF_UPLOAD:
                        upload = virtual.begin_upload(event.value)
                        try:
                            if upload.effect.type != e.FF_RUMBLE or upload.effect.ff_trigger.button:
                                upload.retval = -errno.EINVAL
                            else:
                                effect = upload.effect
                                rumble = effect.u.ff_rumble_effect
                                mixer.upload(effect.id, Effect(rumble.strong_magnitude, rumble.weak_magnitude,
                                                              effect.ff_replay.length / 1000, effect.ff_replay.delay / 1000))
                                upload.retval = 0
                        finally:
                            virtual.end_upload(upload)
                    elif event.type == e.EV_UINPUT and event.code == e.UI_FF_ERASE:
                        erase = virtual.begin_erase(event.value)
                        try:
                            mixer.erase(erase.effect_id)
                            erase.retval = 0
                        finally:
                            virtual.end_erase(erase)
                    elif event.type == e.EV_FF:
                        if event.code == e.FF_GAIN:
                            mixer.gain = max(0, min(65535, event.value))
                        elif event.code < 16:
                            mixer.play(event.code, event.value, time.monotonic())
            now = time.monotonic()
            if now >= next_send:
                strong, weak = mixer.levels(now)
                emit(strong=strong, weak=weak)
                next_send = now + 0.05
    finally:
        if physical is not None:
            if grabbed:
                try:
                    physical.ungrab()
                except OSError:
                    pass
            physical.close()
        if virtual is not None:
            virtual.close()
        emit(strong=0, weak=0)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--list', action='store_true')
    parser.add_argument('--device', default='')
    parser.add_argument('--grab', action='store_true')
    parser.add_argument('--no-passthrough', action='store_true')
    args = parser.parse_args()
    try:
        import evdev
        run(args, evdev)
        return 0
    except ImportError:
        emit(error='Install python3-evdev with your Linux package manager, or set PythonCommand to a venv containing evdev.')
    except PermissionError:
        emit(error='Permission denied: allow this user access to /dev/uinput and the gamepad. See README-Linux.md.')
    except Exception as error:
        emit(error=str(error))
    return 1


if __name__ == '__main__':
    raise SystemExit(main())
