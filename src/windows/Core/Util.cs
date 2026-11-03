using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace HapticLink.Core
{
    /// <summary>Lightweight in-memory + on-disk log. UI subscribes to <see cref="Line"/>.</summary>
    public static class Log
    {
        public static event Action<string> Line;
        private static readonly object Gate = new();

        public static void Write(string msg)
        {
            var stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            try { Line?.Invoke(stamped); } catch { /* UI gone */ }
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var path = Path.Combine(Settings.Dir, "haptic.log");
                    // Seed a BOM so editors and PowerShell read the UTF-8 back correctly.
                    if (!File.Exists(path)) File.WriteAllText(path, "", new UTF8Encoding(true));
                    File.AppendAllText(path, stamped + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { /* logging must never throw */ }
        }
    }

    /// <summary>Asymmetric attack/release smoother operating on 0..1 signals.</summary>
    public class Smoother
    {
        private double _value;

        public double Process(double target, double attackMs, double releaseMs, double dtMs)
        {
            var tau = target > _value ? attackMs : releaseMs;
            if (tau <= 0.5 || dtMs <= 0)
            {
                _value = target;
                return _value;
            }
            // one-pole: alpha = 1 - e^(-dt/tau)
            var alpha = 1.0 - Math.Exp(-dtMs / tau);
            _value += (target - _value) * alpha;
            if (Math.Abs(_value) < 1e-4) _value = 0;
            return _value;
        }

        public void Reset() => _value = 0;
    }

    public static class Sig
    {
        public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>Deadzone, curve then gain. Input and output are 0..1.</summary>
        public static double Shape(double raw, SourceSettings s)
        {
            var v = Clamp01(raw);
            if (v <= s.Deadzone) return 0;
            // rescale so the signal still spans the full range above the deadzone
            v = (v - s.Deadzone) / (1.0 - s.Deadzone);
            if (Math.Abs(s.Curve - 1.0) > 1e-3) v = Math.Pow(v, s.Curve);
            return Clamp01(v * s.Gain);
        }
    }

    /// <summary>Fixed-capacity ring of recent values, for the UI meters/graphs.</summary>
    public class History
    {
        private readonly double[] _buf;
        private int _head;
        public History(int n) => _buf = new double[n];
        public int Count => _buf.Length;

        public void Push(double v)
        {
            _buf[_head] = v;
            _head = (_head + 1) % _buf.Length;
        }

        /// <summary>Oldest-to-newest snapshot.</summary>
        public double[] Snapshot()
        {
            var outp = new double[_buf.Length];
            for (int i = 0; i < _buf.Length; i++) outp[i] = _buf[(_head + i) % _buf.Length];
            return outp;
        }
    }
}
