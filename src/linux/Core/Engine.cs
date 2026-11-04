using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HapticLink.Sources;
using HapticLink.Toy;

namespace HapticLink.Core
{
    public class EngineState
    {
        public bool Connected;
        public bool Armed;
        public bool Panicked;
        public bool SafetyTripped;
        public double Output;
        public int OutputLevel20;
        public double[] Raw = new double[5];
        public double[] Shaped = new double[5];
        public string[] SourceStatus = new string[5];
        public bool[] SourceAvailable = new bool[5];
        public bool[] SourceRunning = new bool[5];
        public bool FocusPaused;
        public TimeSpan ContinuousRun;
        /// <summary>One entry per toy motor, each with its own Output. <see cref="Output"/> is the strongest of them.</summary>
        public List<Channel> Channels = new();
    }

    /// <summary>
    /// Owns the sources, mixes them into one intensity, applies the safety limits and
    /// drives the toy. Everything user-facing reads <see cref="Updated"/>.
    /// </summary>
    public class Engine : IDisposable
    {
        public const int IdxRumble = 0, IdxAudio = 1, IdxOsc = 2, IdxManual = 3, IdxEvents = 4;

        public Settings S { get; }
        public LovenseClient Client { get; } = new();

        public RumbleSource Rumble { get; }
        public AudioSource Audio { get; }
        public OscSource Osc { get; }
        public ManualSource Manual { get; }
        public IntegrationSource Events { get; }
        public GameFocus Game { get; }

        private readonly SourceBase[] _sources;
        private readonly Smoother[] _smoothers = { new(), new(), new(), new(), new() };
        // VRChat reaches each toy motor as a different signal, so that source is smoothed per channel.
        private readonly Dictionary<string, Smoother> _oscSmoothers = new();

        /// <summary>Which parts are driving which toy right now, in words, for the VRChat tab.</summary>
        public string RoutingSummary { get; private set; } = "";
        private readonly History _outputHistory = new(240);

        private readonly object _stateGate = new();
        private readonly SemaphoreSlim _outputGate = new(1, 1);
        private CancellationTokenSource _cts;
        private Task _loop;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTickMs;
        private TimeSpan _continuousRun;
        private DateTime? _zeroSinceUtc = DateTime.UtcNow;
        private long _eventsStopRevision;

        public bool Armed { get; private set; }
        public bool Panicked { get; private set; }
        public bool SafetyTripped { get; private set; }

        private DateTime _testUntilUtc = DateTime.MinValue;
        private double _testLevel;

        /// <summary>Short user-initiated pulse to verify the link. Deliberately bypasses arming.</summary>
        public void Test(double level01, double seconds)
        {
            lock (_stateGate)
            {
                _testLevel = Math.Min(Sig.Clamp01(level01), Sig.Clamp01(S.MasterCap));
                _testUntilUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 0, 5));
            }
        }

        public event Action<EngineState> Updated;

        public Engine(Settings s)
        {
            S = s;
            Game = new GameFocus(s);
            Rumble = new RumbleSource(s);
            Audio = new AudioSource(s, Game);
            Osc = new OscSource(s);
            Manual = new ManualSource(s);
            Events = new IntegrationSource(s);
            _sources = new SourceBase[] { Rumble, Audio, Osc, Manual, Events };
        }

        public SourceSettings SettingsFor(int idx) => idx switch
        {
            IdxRumble => S.Rumble,
            IdxAudio => S.Audio,
            IdxOsc => S.Osc,
            IdxEvents => S.Events,
            _ => S.Manual
        };

        public SourceBase SourceAt(int idx) => _sources[idx];
        public double[] OutputHistory() => _outputHistory.Snapshot();

        // ---------------- lifecycle ----------------

        public void Start()
        {
            if (_loop != null) return;
            Game.Start();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunLoop(_cts.Token));

            for (int i = 0; i < _sources.Length; i++)
                if (SettingsFor(i).Enabled) _sources[i].Start();
        }

        public void SetSourceEnabled(int idx, bool on)
        {
            lock (_stateGate)
            {
                SettingsFor(idx).Enabled = on;
                if (on) _sources[idx].Start();
                else { _sources[idx].Stop(); _smoothers[idx].Reset(); }
            }
        }

        /// <summary>Restart a running source so changed device/port settings take effect.</summary>
        public void RestartSource(int idx)
        {
            lock (_stateGate)
            {
                if (!SettingsFor(idx).Enabled) return;
                _sources[idx].Stop();
                _sources[idx].Start();
            }
        }

        public void Arm(bool on)
        {
            lock (_stateGate)
            {
                Armed = on;
                _testUntilUtc = DateTime.MinValue;
                SafetyTripped = false;
                _continuousRun = TimeSpan.Zero;
                if (!on) _ = StopOutputAsync();
                if (!on) Events.Clear();
                Log.Write(on ? "Output armed." : "Output disarmed.");
            }
        }

        public void Panic()
        {
            lock (_stateGate)
            {
                _testUntilUtc = DateTime.MinValue;
                Panicked = true;
                Armed = false;
                _continuousRun = TimeSpan.Zero;
                foreach (var sm in _smoothers) sm.Reset();
                _oscSmoothers.Clear();
                S.ManualLevel = 0;
                Events.Clear();
                _ = StopOutputAsync();
                Log.Write("PANIC — output stopped.");
            }
        }

        public void ClearPanic()
        {
            lock (_stateGate)
            {
                Panicked = false;
                SafetyTripped = false;
                Log.Write("Panic cleared.");
            }
        }

        private async Task StopOutputAsync()
        {
            await _outputGate.WaitAsync().ConfigureAwait(false);
            try { await Client.StopAsync().ConfigureAwait(false); }
            finally { _outputGate.Release(); }
        }

        // ---------------- main loop ----------------

        private async Task RunLoop(CancellationToken ct)
        {
            var hz = Math.Clamp(S.UpdateHz, 1, 20);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / hz));
            _lastTickMs = _clock.Elapsed.TotalMilliseconds;

            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    var wantHz = Math.Clamp(S.UpdateHz, 1, 20);
                    if (wantHz != hz)
                    {
                        hz = wantHz;
                        timer.Period = TimeSpan.FromMilliseconds(1000.0 / hz);
                    }

                    var now = _clock.Elapsed.TotalMilliseconds;
                    var dt = Math.Max(1.0, now - _lastTickMs);
                    _lastTickMs = now;

                    EngineState state;
                    await _outputGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        lock (_stateGate) state = Compute(dt);
                        if (Client.IsConnected)
                        {
                            await Client.SetOutputsAsync(state.Channels, S, ct).ConfigureAwait(false);
                            RefreshToysNowAndThen(now, ct);
                        }
                    }
                    finally { _outputGate.Release(); }

                    _outputHistory.Push(state.Output);
                    Updated?.Invoke(state);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write("Engine loop stopped: " + ex.Message);
            }
        }

        private const double ToyRefreshMs = 15000;
        private double _lastToyRefreshMs;
        private int _refreshingToys;

        /// <summary>
        /// Routing depends on which toys are switched on, and that changes without anyone telling us.
        /// Runs beside the loop, never in it — a slow phone must not stall the output.
        /// </summary>
        private void RefreshToysNowAndThen(double nowMs, CancellationToken ct)
        {
            if (nowMs - _lastToyRefreshMs < ToyRefreshMs) return;
            _lastToyRefreshMs = nowMs;
            if (Interlocked.Exchange(ref _refreshingToys, 1) == 1) return;
            Task.Run(async () =>
            {
                try { await Client.RefreshToysAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                finally { Interlocked.Exchange(ref _refreshingToys, 0); }
            });
        }

        private EngineState Compute(double dtMs)
        {
            var st = new EngineState
            {
                Connected = Client.IsConnected,
                Armed = Armed,
                Panicked = Panicked,
                FocusPaused = Game.State.Pauses(S)
            };

            for (int i = 0; i < _sources.Length; i++)
            {
                var src = _sources[i];
                var cfg = SettingsFor(i);
                src.Tick(dtMs);
                if (i == IdxEvents && Events.StopRevision != _eventsStopRevision)
                {
                    _eventsStopRevision = Events.StopRevision;
                    _smoothers[i].Reset();
                }

                st.Raw[i] = src.Raw;
                st.SourceStatus[i] = src.Status;
                st.SourceAvailable[i] = src.Available;
                st.SourceRunning[i] = src.Running;

                if (!cfg.Enabled || !src.Running)
                {
                    _smoothers[i].Reset();
                    st.Shaped[i] = 0;
                    continue;
                }

                var shaped = Sig.Shape(src.Raw, cfg);
                st.Shaped[i] = _smoothers[i].Process(shaped, cfg.AttackMs, cfg.ReleaseMs, dtMs);
            }

            // ---- per-toy routing: only the VRChat source differs from one channel to the next ----
            var oscOn = S.Osc.Enabled && Osc.Running;
            var channels = PartRouter.Build(Client.Toys, S, oscOn ? Osc.ZonesPresent : null);
            var routing = PartRouter.Describe(channels, oscOn ? Osc.ZonesPresent : null, S);
            if (routing != RoutingSummary)
            {
                RoutingSummary = routing;
                if (oscOn && Client.IsConnected) Log.Write("Parts → toys: " + routing.Replace("\n", "  |  "));
            }

            if (!oscOn) _oscSmoothers.Clear();
            var mixIn = (double[])st.Shaped.Clone();
            double oscShapedMax = 0, mixedMax = 0;
            foreach (var ch in channels)
            {
                mixIn[IdxOsc] = oscOn ? OscFor(ch, dtMs) : 0;
                oscShapedMax = Math.Max(oscShapedMax, mixIn[IdxOsc]);
                ch.Output = Sig.Clamp01(Mix(mixIn) * Sig.Clamp01(S.MasterCap));
                mixedMax = Math.Max(mixedMax, ch.Output);
            }
            st.Shaped[IdxOsc] = oscShapedMax;
            foreach (var stale in _oscSmoothers.Keys.Where(k => channels.All(c => c.Key != k)).ToList())
                _oscSmoothers.Remove(stale);

            var output = mixedMax;

            // ---- safety ----
            if (!Armed || Panicked || st.FocusPaused) output = 0;

            if (output > 0.001)
            {
                _zeroSinceUtc = null;
                _continuousRun += TimeSpan.FromMilliseconds(dtMs);
                if (S.MaxContinuousMinutes > 0 &&
                    _continuousRun.TotalMinutes >= S.MaxContinuousMinutes && !SafetyTripped)
                {
                    SafetyTripped = true;
                    Armed = false;
                    output = 0;
                    Log.Write($"Safety cutoff: {S.MaxContinuousMinutes} min of continuous output reached. " +
                              "Re-arm to continue.");
                }
            }
            else
            {
                // a full minute at zero resets the continuous-run budget
                _zeroSinceUtc ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - _zeroSinceUtc.Value).TotalSeconds >= 60) _continuousRun = TimeSpan.Zero;
            }

            if (SafetyTripped) output = 0;

            // Test pulses are explicit and short-lived, so they win over arm/safety but never over panic.
            var testing = !Panicked && !st.FocusPaused && DateTime.UtcNow < _testUntilUtc;
            var testLevel = Math.Min(_testLevel, Sig.Clamp01(S.MasterCap));
            if (testing) output = Math.Max(output, testLevel);

            // Whatever silenced the overall output silences every motor; a test pulse reaches them all.
            var open = Armed && !Panicked && !SafetyTripped && !st.FocusPaused;
            foreach (var ch in channels)
            {
                if (!open) ch.Output = 0;
                if (testing) ch.Output = Math.Max(ch.Output, testLevel);
            }

            st.SafetyTripped = SafetyTripped;
            st.Armed = Armed;
            st.Output = output;
            st.OutputLevel20 = (int)Math.Round(output * 20);
            st.ContinuousRun = _continuousRun;
            st.Channels = channels;
            return st;
        }

        /// <summary>The VRChat source as this one motor should feel it: its zones at their weights, then shaped and smoothed.</summary>
        private double OscFor(Channel ch, double dtMs)
        {
            var levels = Osc.ZoneLevels;
            var depths = Osc.ZoneDepths;
            var raw = Osc.TickedLevel;
            for (int z = 0; z < levels.Length; z++)
                raw = Math.Max(raw, levels[z] * PartRouter.Weight(ch, z, depths[z], S));

            if (!_oscSmoothers.TryGetValue(ch.Key, out var sm)) _oscSmoothers[ch.Key] = sm = new Smoother();
            return sm.Process(Sig.Shape(raw, S.Osc), S.Osc.AttackMs, S.Osc.ReleaseMs, dtMs);
        }

        private double Mix(double[] shaped)
        {
            var active = new List<(double val, int idx)>();
            for (int i = 0; i < shaped.Length; i++)
                if (SettingsFor(i).Enabled) active.Add((shaped[i], i));

            if (active.Count == 0) return 0;

            switch (S.Mix)
            {
                case MixMode.Average:
                    return active.Average(a => a.val);
                case MixMode.Sum:
                    return Sig.Clamp01(active.Sum(a => a.val));
                case MixMode.Priority:
                    // highest-priority source that is actually producing something
                    foreach (var a in active.OrderByDescending(a => SettingsFor(a.idx).Priority))
                        if (a.val > 0.001) return a.val;
                    return 0;
                default:
                    return active.Max(a => a.val);
            }
        }

        // ---------------- shutdown ----------------

        public async Task ShutdownAsync()
        {
            Armed = false;
            try { _cts?.Cancel(); } catch { }
            if (_loop != null)
            {
                try { await _loop.ConfigureAwait(false); } catch { }
                _loop = null;
            }
            foreach (var s in _sources)
            {
                try { s.Stop(); } catch { }
            }
            try { await Client.StopAsync().ConfigureAwait(false); } catch { }
        }

        public void Dispose()
        {
            try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
            foreach (var s in _sources) { try { s.Dispose(); } catch { } }
            Client.Dispose();
            Game.Dispose();
            _cts?.Dispose();
        }
    }
}
