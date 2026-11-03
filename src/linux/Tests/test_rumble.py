import importlib.util
from pathlib import Path
import sys
import unittest
from types import SimpleNamespace as NS
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('rumble_bridge', Path(__file__).parents[1] / 'linux/rumble_bridge.py')
bridge = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = bridge
spec.loader.exec_module(bridge)


class EffectTests(unittest.TestCase):
    def setUp(self):
        self.mixer = bridge.EffectMixer()
        self.mixer.upload(0, bridge.Effect(60000, 30000, 1, .2))

    def test_upload_is_not_playback(self):
        self.assertEqual(self.mixer.levels(20), (0, 0))

    def test_delay_and_expiry(self):
        self.mixer.play(0, 1, 10)
        self.assertEqual(self.mixer.levels(10.1), (0, 0))
        self.assertEqual(self.mixer.levels(10.3), (60000, 30000))
        self.assertEqual(self.mixer.levels(11.3), (0, 0))

    def test_repeats(self):
        self.mixer.play(0, 2, 10)
        self.assertEqual(self.mixer.levels(11.3), (0, 0))
        self.assertEqual(self.mixer.levels(11.5), (60000, 30000))
        self.assertEqual(self.mixer.levels(12.5), (0, 0))

    def test_stop_and_erase(self):
        self.mixer.play(0, 1, 10)
        self.mixer.play(0, 0, 10.3)
        self.assertEqual(self.mixer.levels(10.4), (0, 0))
        self.mixer.play(0, 1, 20)
        self.mixer.erase(0)
        self.assertEqual(self.mixer.levels(20.4), (0, 0))

    def test_gain_and_overlapping_effects(self):
        self.mixer.upload(1, bridge.Effect(40000, 20000, 1, 0))
        self.mixer.play(0, 1, 10)
        self.mixer.play(1, 1, 10)
        self.assertEqual(self.mixer.levels(10.5), (65535, 50000))
        self.mixer.gain = 32767
        self.assertEqual(self.mixer.levels(10.5), (49999, 24999))

    def test_unknown_effect_and_zero_gain(self):
        self.mixer.play(99, 1, 10)
        self.assertEqual(self.mixer.levels(10.5), (0, 0))
        self.mixer.play(0, 1, 10)
        self.mixer.gain = 0
        self.assertEqual(self.mixer.levels(10.5), (0, 0))

    def test_indefinite_effect_still_stops(self):
        self.mixer.upload(2, bridge.Effect(10, 20, 0, 0))
        self.mixer.play(2, 1, 10)
        self.assertEqual(self.mixer.levels(999), (10, 20))
        self.mixer.play(2, 0, 999)
        self.assertEqual(self.mixer.levels(1000), (0, 0))


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.e = NS(EV_SYN=0, EV_KEY=1, EV_ABS=3, EV_MSC=4, EV_FF=21, EV_UINPUT=257,
                    BTN_GAMEPAD=304, ABS_X=0, ABS_Y=1, FF_RUMBLE=80, FF_GAIN=96,
                    UI_FF_UPLOAD=1, UI_FF_ERASE=2, SYN_DROPPED=3)

    def test_enumeration_closes_devices_and_ignores_virtual_and_keyboard(self):
        devices = []
        def make(path):
            device = NS(path=path, name={'a': 'Gamepad', 'b': 'HapticLink Virtual Gamepad', 'c': 'Keyboard'}[path], closed=False)
            device.capabilities = lambda **_: {1: [304] if path != 'c' else [30], 3: [0, 1]}
            device.close = lambda: setattr(device, 'closed', True)
            devices.append(device)
            return device
        fake = NS(list_devices=lambda: ['a', 'b', 'c'], InputDevice=make, ecodes=self.e)
        self.assertEqual(bridge.gamepads(fake), [{'path': 'a', 'name': 'Gamepad'}])
        self.assertTrue(all(device.closed for device in devices))

    def test_upload_playback_parent_eof_and_grab_cleanup(self):
        physical = NS(name='Test Pad', info=NS(vendor=1, product=2, version=1, bustype=3), closed=False, grabbed=False)
        physical.capabilities = lambda **kwargs: {1: [304], 3: [0, 1]} if kwargs else {1: [304], 3: [(0, NS(value=0)), (1, NS(value=0))]}
        physical.active_keys = lambda: []
        physical.grab = lambda: setattr(physical, 'grabbed', True)
        physical.ungrab = lambda: setattr(physical, 'grabbed', False)
        physical.close = lambda: setattr(physical, 'closed', True)
        effect = NS(id=0, type=80, ff_trigger=NS(button=0), ff_replay=NS(length=1000, delay=0),
                    u=NS(ff_rumble_effect=NS(strong_magnitude=50000, weak_magnitude=10000)))
        upload = NS(effect=effect, retval=None)
        virtual = NS(closed=False, write=lambda *_: None, syn=lambda: None)
        virtual.begin_upload = lambda _: upload
        virtual.end_upload = lambda _: None
        virtual.read = lambda: [NS(type=257, code=1, value=1), NS(type=21, code=0, value=1)]
        virtual.close = lambda: setattr(virtual, 'closed', True)
        capabilities = []
        def create_virtual(caps, **kwargs):
            capabilities.append(caps)
            return virtual
        fake = NS(ecodes=self.e, InputDevice=lambda _: physical, UInput=create_virtual)
        messages = []
        ready = iter([([virtual], [], []), ([bridge.sys.stdin], [], [])])
        with patch.object(bridge, 'gamepads', return_value=[{'path': '/dev/input/event1', 'name': 'Test Pad'}]), \
             patch.object(bridge.select, 'select', side_effect=lambda *_: next(ready)), \
             patch.object(bridge.signal, 'signal'), patch.object(bridge.os, 'read', return_value=b''), \
             patch.object(bridge.time, 'monotonic', return_value=10), \
             patch.object(bridge, 'emit', side_effect=lambda **fields: messages.append(fields)):
            bridge.run(NS(list=False, device='', grab=True, no_passthrough=False), fake)
        self.assertEqual(upload.retval, 0)
        self.assertIn({'strong': 50000, 'weak': 10000}, messages)
        self.assertEqual(messages[-1], {'strong': 0, 'weak': 0})
        self.assertFalse(physical.grabbed)
        self.assertTrue(physical.closed and virtual.closed)
        self.assertEqual(capabilities[0][21], [80, 96])


if __name__ == '__main__':
    unittest.main()
