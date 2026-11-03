using System;
using HapticLink.Core;

namespace HapticLink.Sources
{
    /// <summary>Base level plus an optional pattern LFO. Also the safest way to test the link.</summary>
    public class ManualSource : SourceBase
    {
        public override string Name => "Manual";

        private readonly Settings _s;
        private readonly Random _rng = new();
        private double _phase;          // 0..1 within the pattern period
        private double _randomTarget;
        private double _randomCurrent;

        public ManualSource(Settings s)
        {
            _s = s;
            Status = "Ready";
        }

        public override void Start()
        {
            Running = true;
            Status = "Ready";
        }

        public override void Stop()
        {
            Running = false;
            Raw = 0;
            _phase = 0;
            Status = "Stopped";
        }

        public override void Tick(double dtMs)
        {
            if (!Running) { Raw = 0; return; }

            var level = Sig.Clamp01(_s.ManualLevel);
            if (level <= 0) { Raw = 0; return; }

            var period = Math.Max(0.1, _s.PatternPeriodSec);
            _phase += (dtMs / 1000.0) / period;
            if (_phase >= 1.0)
            {
                _phase -= Math.Floor(_phase);
                _randomTarget = _rng.NextDouble();
            }

            double mod = _s.Pattern switch
            {
                PatternMode.None => 1.0,
                PatternMode.Pulse => _phase < 0.5 ? 1.0 : 0.0,
                PatternMode.Wave => 0.5 - 0.5 * Math.Cos(_phase * 2 * Math.PI),
                PatternMode.Ramp => _phase,
                PatternMode.Random => StepRandom(dtMs),
                _ => 1.0
            };

            // Depth blends between a flat level (0) and full modulation (1).
            var depth = Sig.Clamp01(_s.PatternDepth);
            var shaped = 1.0 - depth + depth * mod;
            Raw = Sig.Clamp01(level * shaped);

            Status = _s.Pattern == PatternMode.None ? "Ready" : $"{_s.Pattern} @ {period:0.0}s";
        }

        private double StepRandom(double dtMs)
        {
            // glide toward the current random target so it doesn't jump
            var alpha = 1.0 - Math.Exp(-dtMs / 120.0);
            _randomCurrent += (_randomTarget - _randomCurrent) * alpha;
            return Sig.Clamp01(_randomCurrent);
        }
    }
}
