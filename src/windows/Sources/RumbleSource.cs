using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HapticLink.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace HapticLink.Sources
{
    /// <summary>
    /// Captures force-feedback the game sends to a controller, without injecting into the game.
    /// We stand up a ViGEm virtual Xbox pad, forward the physical pad's input through it, and
    /// read back the rumble the game sends to the virtual pad.
    /// </summary>
    public class RumbleSource : SourceBase
    {
        public override string Name => "Rumble";

        private readonly Settings _s;
        private ViGEmClient _client;
        private IXbox360Controller _pad;
        private Thread _pump;
        private volatile bool _stop;

        private volatile int _large;   // 0..255
        private volatile int _small;   // 0..255
        private long _feedbackCount;

        /// <summary>XInput slot our own virtual pad landed on — never mirror this one.</summary>
        private volatile int _virtualIndex = -1;
        /// <summary>XInput slot of the physical pad we're currently mirroring, -1 if none.</summary>
        private volatile int _activeSlot = -1;
        private uint _lastPacket;

        private long _polls;
        private volatile int _pollHzActual;
        private DateTime _startedUtc;
        private bool _hintShown;

        public RumbleSource(Settings s) => _s = s;

        public override void Start()
        {
            if (Running) return;
            _stop = false;
            _feedbackCount = 0;
            _polls = 0;
            _lastPacket = 0;
            _virtualIndex = -1;
            _activeSlot = -1;
            _startedUtc = DateTime.UtcNow;
            _hintShown = false;

            // Snapshot before creating the virtual pad; whatever slot appears afterwards is ours.
            var before = XInput.ConnectedSlots();

            try
            {
                _client = new ViGEmClient();
                _pad = _client.CreateXbox360Controller();
                _pad.AutoSubmitReport = false;
                _pad.FeedbackReceived += OnFeedback;
                _pad.Connect();
            }
            catch (VigemBusNotFoundException)
            {
                Available = false;
                Status = "ViGEmBus driver not installed";
                Log.Write("Rumble: ViGEmBus driver missing. Install it from " +
                          "https://github.com/nefarius/ViGEmBus/releases then restart HapticLink.");
                Cleanup();
                return;
            }
            catch (Exception ex)
            {
                Available = false;
                Status = "Virtual pad failed: " + ex.Message;
                Log.Write("Rumble: " + ex.Message);
                Cleanup();
                return;
            }

            Available = true;
            Running = true;
            Status = "Virtual pad up, detecting controllers...";

            // Slot identification and polling both live on the pump thread so the UI never blocks.
            _pump = new Thread(() => Pump(before))
            {
                IsBackground = true,
                Name = "HapticLink.RumblePump",
                Priority = ThreadPriority.AboveNormal
            };
            _pump.Start();
        }

        private void OnFeedback(object sender, Xbox360FeedbackReceivedEventArgs e)
        {
            _large = e.LargeMotor;
            _small = e.SmallMotor;
            Interlocked.Increment(ref _feedbackCount);
            Recompute();
        }

        private void Recompute()
        {
            var l = _large / 255.0 * _s.RumbleLargeWeight;
            var sm = _small / 255.0 * _s.RumbleSmallWeight;
            Raw = Sig.Clamp01(Math.Max(l, sm));
        }

        private void Pump(HashSet<int> before)
        {
            // Without this, Thread.Sleep rounds up to the ~15.6ms scheduler tick and the pump
            // runs at ~64Hz however small the requested period is.
            XInput.BeginHighResolutionTimers();
            try
            {
                ResolveVirtualIndex(before);

                var sw = Stopwatch.StartNew();
                long nextRescanMs = 0;
                long nextStatusMs = 1000;
                long pollsAtLastStatus = 0;

                while (!_stop)
                {
                    var nowMs = sw.ElapsedMilliseconds;

                    // Rescanning probes empty slots, which is slow, so keep it off the hot path.
                    if (_activeSlot < 0 || nowMs >= nextRescanMs)
                    {
                        RescanActiveSlot();
                        nextRescanMs = nowMs + 1000;
                    }

                    if (_s.RumblePassthrough && _activeSlot >= 0 && _pad != null)
                    {
                        if (XInput.TryGetState((uint)_activeSlot, out var st))
                        {
                            // Only push a report when the pad state actually changed.
                            if (st.dwPacketNumber != _lastPacket)
                            {
                                _lastPacket = st.dwPacketNumber;
                                Mirror(st.Gamepad);
                            }
                        }
                        else
                        {
                            Log.Write($"Rumble: controller on slot {_activeSlot} disconnected.");
                            _activeSlot = -1;
                            _lastPacket = 0;
                        }
                    }

                    Recompute();
                    Interlocked.Increment(ref _polls);

                    if (nowMs >= nextStatusMs)
                    {
                        var polls = Interlocked.Read(ref _polls);
                        _pollHzActual = (int)(polls - pollsAtLastStatus);
                        pollsAtLastStatus = polls;
                        nextStatusMs = nowMs + 1000;
                        UpdateStatus();
                    }

                    SleepForPeriod();
                }
            }
            catch (Exception ex)
            {
                Log.Write("Rumble pump stopped: " + ex.Message);
            }
            finally
            {
                XInput.EndHighResolutionTimers();
            }
        }

        // A stick can hit either extreme, but not both axes at these exact opposed values at once.
        private const short SentinelX = 31337;
        private const short SentinelY = -31337;

        /// <summary>
        /// Work out which XInput slot our own virtual pad took, so rescans never mirror it into
        /// itself. Diffing the slot list is racy — Steam Input re-enumerates its emulated pad
        /// whenever a new gamepad appears, so a slot can show up that isn't ours. Instead, stamp
        /// a sentinel stick position onto our pad and find the slot that echoes it back.
        /// </summary>
        private void ResolveVirtualIndex(HashSet<int> before)
        {
            try
            {
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, SentinelX);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbY, SentinelY);
                _pad.SubmitReport();
            }
            catch (Exception ex)
            {
                Log.Write("Rumble: could not stamp identity report: " + ex.Message);
            }

            for (int attempt = 0; attempt < 40 && !_stop; attempt++)
            {
                Thread.Sleep(50);
                for (uint i = 0; i < 4; i++)
                {
                    if (!XInput.TryGetState(i, out var st)) continue;
                    if (st.Gamepad.sThumbLX != SentinelX || st.Gamepad.sThumbLY != SentinelY) continue;

                    _virtualIndex = (int)i;
                    Log.Write($"Rumble: virtual pad confirmed on XInput slot {_virtualIndex}.");
                    ClearSentinel();
                    return;
                }
            }

            // Fallbacks, least trustworthy last.
            try
            {
                _virtualIndex = _pad.UserIndex;
                Log.Write($"Rumble: virtual pad reported slot {_virtualIndex} (unverified).");
            }
            catch
            {
                var added = XInput.ConnectedSlots();
                added.ExceptWith(before);
                if (added.Count == 1)
                {
                    _virtualIndex = added.First();
                    Log.Write($"Rumble: assuming virtual pad is slot {_virtualIndex} (unverified).");
                }
                else
                {
                    Log.Write("Rumble: could not identify the virtual pad's slot. Passthrough is " +
                              "disabled to avoid mirroring the pad into itself.");
                }
            }
            ClearSentinel();
        }

        private void ClearSentinel()
        {
            try
            {
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                _pad.SubmitReport();
            }
            catch { }
        }

        private void RescanActiveSlot()
        {
            // Without a confirmed identity we cannot tell our pad from a real one; mirroring it
            // into itself would latch input, so stay idle instead.
            if (_virtualIndex < 0) return;

            for (uint i = 0; i < 4; i++)
            {
                if ((int)i == _virtualIndex) continue;
                if (!XInput.TryGetState(i, out _)) continue;

                if (_activeSlot != (int)i)
                {
                    _activeSlot = (int)i;
                    _lastPacket = 0;
                    Log.Write($"Rumble: mirroring controller on XInput slot {i}.");
                }
                return;
            }

            if (_activeSlot >= 0)
            {
                _activeSlot = -1;
                _lastPacket = 0;
            }
        }

        private void Mirror(XInput.XINPUT_GAMEPAD g)
        {
            // ViGEm's button bitmask is identical to XInput's, so this is a 1:1 copy.
            _pad.SetButtonsFull(g.wButtons);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, g.sThumbLX);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, g.sThumbLY);
            _pad.SetAxisValue(Xbox360Axis.RightThumbX, g.sThumbRX);
            _pad.SetAxisValue(Xbox360Axis.RightThumbY, g.sThumbRY);
            _pad.SetSliderValue(Xbox360Slider.LeftTrigger, g.bLeftTrigger);
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, g.bRightTrigger);
            _pad.SubmitReport();
        }

        private void SleepForPeriod()
        {
            var hz = Math.Clamp(_s.RumblePollHz, 60, 1000);
            var ms = (int)Math.Round(1000.0 / hz);
            Thread.Sleep(Math.Max(1, ms));
        }

        private void UpdateStatus()
        {
            if (_activeSlot < 0)
            {
                Status = "No controller detected — switch it on. PlayStation pads need " +
                         "DS4Windows or Steam Input to appear as XInput.";
                return;
            }

            var both = _virtualIndex >= 0;
            Status = $"Mirroring slot {_activeSlot} → virtual slot {_virtualIndex} · {_pollHzActual} Hz";

            if (both && Interlocked.Read(ref _feedbackCount) == 0 &&
                (DateTime.UtcNow - _startedUtc).TotalSeconds > 30)
            {
                Status = $"Mirroring slot {_activeSlot} · {_pollHzActual} Hz — no rumble yet; " +
                         "hide the real pad with HidHide so the game only sees the virtual one";
                if (!_hintShown)
                {
                    _hintShown = true;
                    Log.Write("Rumble: your game can see BOTH pads, so it is rumbling the real one " +
                              "and may be reading doubled input. Install HidHide " +
                              "(https://github.com/nefarius/HidHide/releases) and hide the physical " +
                              "controller — HapticLink still reads it, games only see the virtual pad.");
                }
            }
        }

        public override void Stop()
        {
            _stop = true;
            try { _pump?.Join(400); } catch { }
            _pump = null;
            Cleanup();
            Running = false;
            Raw = 0;
            _large = _small = 0;
            _activeSlot = -1;
            _virtualIndex = -1;
            if (Available) Status = "Stopped";
        }

        private void Cleanup()
        {
            try
            {
                if (_pad != null)
                {
                    _pad.FeedbackReceived -= OnFeedback;
                    _pad.Disconnect();
                }
            }
            catch { }
            try { _client?.Dispose(); } catch { }
            _pad = null;
            _client = null;
        }
    }

    /// <summary>XInput binding. Uses the undocumented ordinal 100 export when present so the
    /// Guide button passes through too, which XInputGetState deliberately masks out.</summary>
    internal static class XInput
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        private const int ERROR_SUCCESS = 0;
        private static bool _unavailable;
        private static int _mode = 0; // 0 = untried, 1 = Ex(#100), 2 = plain, 3 = legacy dll

        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx14(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState14(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint ms);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint ms);

        public static void BeginHighResolutionTimers()
        {
            try { TimeBeginPeriod(1); } catch { }
        }

        public static void EndHighResolutionTimers()
        {
            try { TimeEndPeriod(1); } catch { }
        }

        public static bool TryGetState(uint index, out XINPUT_STATE state)
        {
            state = default;
            if (_unavailable) return false;

            if (_mode == 0)
            {
                foreach (var candidate in new[] { 1, 2, 3 })
                {
                    try
                    {
                        var r = Call(candidate, index, out state);
                        _mode = candidate;
                        return r == ERROR_SUCCESS;
                    }
                    catch (DllNotFoundException) { }
                    catch (EntryPointNotFoundException) { }
                    catch { return false; }
                }
                _unavailable = true;
                Log.Write("XInput unavailable on this system; controller passthrough disabled.");
                return false;
            }

            try { return Call(_mode, index, out state) == ERROR_SUCCESS; }
            catch { return false; }
        }

        private static uint Call(int mode, uint index, out XINPUT_STATE state) => mode switch
        {
            1 => XInputGetStateEx14(index, out state),
            2 => XInputGetState14(index, out state),
            _ => XInputGetState910(index, out state)
        };

        public static HashSet<int> ConnectedSlots()
        {
            var set = new HashSet<int>();
            for (uint i = 0; i < 4; i++)
                if (TryGetState(i, out _)) set.Add((int)i);
            return set;
        }
    }
}
