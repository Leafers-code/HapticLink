namespace HapticLink.Core;

public static class AudioFrequencyRange
{
    public const double Minimum = 20, Maximum = 20000, Gap = 10;
    public static (double Low, double High) Normalize(double low, double high)
    {
        low = double.IsFinite(low) ? Math.Clamp(low, Minimum, Maximum - Gap) : Minimum;
        high = double.IsFinite(high) ? Math.Clamp(high, low + Gap, Maximum) : Maximum;
        return (low, high);
    }
    // Preserve Bass/Treble choices from older configuration files.
    public static (double Low, double High) For(Settings settings) => settings.AudioFrequencyCaptureEnabled ? Selected(settings) : (Minimum, Maximum);
    public static (double Low, double High) Selected(Settings settings) => settings.AudioBand switch
    {
        AudioBand.Bass => (20, 180),
        AudioBand.Treble => (2500, 20000),
        AudioBand.Custom => Normalize(settings.AudioCaptureLowHz, settings.AudioCaptureHighHz),
        _ => (Minimum, Maximum)
    };
}
