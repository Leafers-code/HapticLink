namespace HapticLink.Core;

/// <summary>Frequency-range capture and optional tonal shaping of haptic analysis.</summary>
public sealed class HapticEqualizer
{
    public static readonly double[] Frequencies = { 31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private readonly Biquad[,] _filters = new Biquad[2, 10];
    private readonly Biquad[,] _meters = new Biquad[2, 10];
    private readonly Biquad[,] _range = new Biquad[2, 4];
    private (double Low, double High) _lastRange = (double.NaN, double.NaN);
    private bool _rangeActive;
    private double[] _gains = Enumerable.Repeat(double.NaN, 10).ToArray();
    private readonly double[] _energy = new double[10];
    private double _preamp = 1;
    public double[] Levels { get; private set; } = new double[10];
    public HapticEqualizer()
    {
        for (int c = 0; c < 2; c++) for (int b = 0; b < 4; b++) _range[c, b] = new();
        for (int c = 0; c < 2; c++) for (int b = 0; b < 10; b++)
        {
            _filters[c, b] = new(); _meters[c, b] = new();
            _meters[c, b].BandPass(Frequencies[b]);
        }
    }
    public void Configure(Settings settings)
    {
        var range = AudioFrequencyRange.For(settings);
        if (range != _lastRange)
        {
            _lastRange = range;
            _rangeActive = range.Low > AudioFrequencyRange.Minimum || range.High < AudioFrequencyRange.Maximum;
            // Two Butterworth sections form a fourth-order cutoff at each edge.
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < 2; i++)
                {
                    var q = i == 0 ? .541196100146197 : 1.306562964876377;
                    _range[c, i].Cutoff(range.Low, q, highPass: true);
                    _range[c, i + 2].Cutoff(range.High, q, highPass: false);
                }
        }
        _preamp = settings.AudioEqEnabled ? Math.Pow(10, Math.Clamp(settings.AudioEqPreampDb, -24, 12) / 20) : 1;
        for (int b = 0; b < 10; b++)
        {
            var gain = settings.AudioEqEnabled && settings.AudioEqGainsDb?.Length == 10 ? settings.AudioEqGainsDb[b] : 0;
            if (!double.IsFinite(gain)) gain = 0;
            gain = Math.Clamp(gain, -12, 12);
            if (gain == _gains[b]) continue;
            _gains[b] = gain;
            for (int c = 0; c < 2; c++) _filters[c, b].Peak(Frequencies[b], gain);
        }
        Array.Clear(_energy);
    }
    public double Process(double sample, int channel)
    {
        // Show incoming spectrum, including the frequencies excluded by the selection.
        for (int b = 0; b < 10; b++) { var value = _meters[channel, b].Run(sample); _energy[b] += value * value; }
        if (_rangeActive) for (int b = 0; b < 4; b++) sample = _range[channel, b].Run(sample);
        sample *= _preamp;
        for (int b = 0; b < 10; b++) sample = _filters[channel, b].Run(sample);
        return double.IsFinite(sample) ? sample : 0;
    }
    public void FinishBuffer(int samples)
    {
        var levels = new double[10];
        for (int b = 0; b < 10; b++)
        {
            var rms = Math.Sqrt(_energy[b] / Math.Max(1, samples));
            levels[b] = .75 * Levels[b] + .25 * Sig.Clamp01((20 * Math.Log10(rms + 1e-9) + 70) / 70);
        }
        Levels = levels;
    }
    public void Reset()
    {
        for (int c = 0; c < 2; c++) for (int b = 0; b < 4; b++) _range[c, b].Reset();
        for (int c = 0; c < 2; c++) for (int b = 0; b < 10; b++) { _filters[c, b].Reset(); _meters[c, b].Reset(); }
        Levels = new double[10];
    }
    private sealed class Biquad
    {
        private double b0 = 1, b1, b2, a1, a2, z1, z2;
        public double Run(double x) { var y = b0 * x + z1; z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y; return y; }
        public void Reset() => z1 = z2 = 0;
        public void Peak(double frequency, double gain)
        {
            var a = Math.Pow(10, gain / 40); var w = 2 * Math.PI * frequency / 48000; var alpha = Math.Sin(w) / 2;
            Set(1 + alpha * a, -2 * Math.Cos(w), 1 - alpha * a, 1 + alpha / a, -2 * Math.Cos(w), 1 - alpha / a);
        }
        public void BandPass(double frequency)
        {
            var w = 2 * Math.PI * frequency / 48000; var alpha = Math.Sin(w) / 2;
            Set(alpha, 0, -alpha, 1 + alpha, -2 * Math.Cos(w), 1 - alpha);
        }
        // RBJ low/high-pass coefficients: https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html
        public void Cutoff(double frequency, double q, bool highPass)
        {
            var w = 2 * Math.PI * frequency / 48000; var cosine = Math.Cos(w); var alpha = Math.Sin(w) / (2 * q);
            var b = highPass ? (1 + cosine) / 2 : (1 - cosine) / 2;
            Set(b, highPass ? -2 * b : 2 * b, b, 1 + alpha, -2 * cosine, 1 - alpha);
        }
        private void Set(double nb0, double nb1, double nb2, double a0, double na1, double na2)
        { b0 = nb0 / a0; b1 = nb1 / a0; b2 = nb2 / a0; a1 = na1 / a0; a2 = na2 / a0; Reset(); }
    }
}
