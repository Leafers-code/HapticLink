using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HapticLink.Core;
using HapticLink.Sources;

namespace HapticLink;

public sealed class EqualizerPanel : StackPanel
{
    private readonly Settings _settings;
    private readonly AudioSource _source;
    private readonly FrequencyRangeView _view = new() { Height = 104 };
    private readonly TextBlock _selection = new() { FontSize = 11, Foreground = Classic.Accent, FontFamily = Classic.Mono, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericUpDown _from, _to;
    private readonly Slider _fromSlider, _toSlider;
    private readonly TonalEqualizerPanel _tonal;
    private readonly CheckBox _enabled = new() { Content = "Enabled", Margin = new Thickness(10, 0, 0, 0) };
    private readonly List<(Button Button, double Low, double High)> _presets = new();
    private bool _syncing;

    public EqualizerPanel(Settings settings, AudioSource source)
    {
        _settings = settings; _source = source; Spacing = 5;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(new TextBlock { Text = "AUDIO FREQUENCY CAPTURE", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Classic.Muted });
        Grid.SetColumn(_selection, 1); header.Children.Add(_selection); Children.Add(header);
        _enabled.IsChecked = settings.AudioFrequencyCaptureEnabled;
        _enabled.IsCheckedChanged += (_, _) => { if (!_syncing) { settings.AudioFrequencyCaptureEnabled = _enabled.IsChecked == true; Refresh(); } };
        Grid.SetColumn(_enabled, 2); header.Children.Add(_enabled);
        Children.Add(new TextBlock { Text = "Choose the part of the audio that drives haptics. Drag the blue edges or enter a range.", FontSize = 11, Foreground = Classic.Muted, TextWrapping = TextWrapping.Wrap });
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void Preset(string title, AudioBand band, double low, double high)
        {
            var button = new Button { Content = title };
            button.Click += (_, _) => { Apply(low, high); _settings.AudioBand = band; Refresh(); };
            presets.Children.Add(button); _presets.Add((button, low, high));
        }
        Preset("Full range", AudioBand.Loudness, 20, 20000);
        Preset("Bass", AudioBand.Bass, 20, 180);
        Preset("Mids", AudioBand.Custom, 180, 2500);
        Preset("Treble", AudioBand.Treble, 2500, 20000);
        Children.Add(presets); Children.Add(_view);
        _view.RangeChanged += Apply;
        (_from, _fromSlider) = RangeRow("From (Hz)", true);
        (_to, _toSlider) = RangeRow("To (Hz)", false);
        Children.Add(new TextBlock { Text = "Blue = captured frequencies. Other frequencies are filtered out with a gradual roll-off. Speaker audio stays unchanged.", FontSize = 11, Foreground = Classic.Muted, TextWrapping = TextWrapping.Wrap });
        _tonal = new TonalEqualizerPanel(settings, source);
        Children.Add(new Expander { Header = "Advanced tonal shaping", Content = _tonal });
        Refresh();
    }

    private (NumericUpDown, Slider) RangeRow(string label, bool lower)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("76,*,92"), ColumnSpacing = 8, MinHeight = 27 };
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Classic.Muted, VerticalAlignment = VerticalAlignment.Center });
        var slider = new Slider { Minimum = Math.Log10(20), Maximum = Math.Log10(20000), VerticalAlignment = VerticalAlignment.Center };
        var number = new NumericUpDown { Minimum = 20, Maximum = 20000, Increment = 10, FormatString = "0", ShowButtonSpinner = false, Name = lower ? "CaptureFromHz" : "CaptureToHz" };
        void Change(double value)
        {
            if (_syncing) return;
            var range = AudioFrequencyRange.Selected(_settings);
            if (lower) Apply(Math.Clamp(value, 20, range.High - AudioFrequencyRange.Gap), range.High);
            else Apply(range.Low, Math.Clamp(value, range.Low + AudioFrequencyRange.Gap, 20000));
        }
        slider.ValueChanged += (_, _) => Change(Math.Round(Math.Pow(10, slider.Value)));
        number.ValueChanged += (_, _) => { if (number.Value is { } value) Change((double)value); };
        Grid.SetColumn(slider, 1); row.Children.Add(slider); Grid.SetColumn(number, 2); row.Children.Add(number); Children.Add(row);
        return (number, slider);
    }

    private void Apply(double low, double high)
    {
        (low, high) = AudioFrequencyRange.Normalize(low, high);
        _settings.AudioCaptureLowHz = low; _settings.AudioCaptureHighHz = high; _settings.AudioBand = AudioBand.Custom;
        _settings.AudioFrequencyCaptureEnabled = true;
        Refresh();
    }

    public void Refresh()
    {
        if (_from == null || _to == null) return;
        var range = AudioFrequencyRange.Selected(_settings);
        _syncing = true;
        try
        {
            _from.Value = (decimal)range.Low; _to.Value = (decimal)range.High;
            _fromSlider.Value = Math.Log10(range.Low); _toSlider.Value = Math.Log10(range.High);
            _enabled.IsChecked = _settings.AudioFrequencyCaptureEnabled;
            _selection.Text = _settings.AudioFrequencyCaptureEnabled ? $"{range.Low:0}–{range.High:0} Hz" : "BYPASSED · full range";
            foreach (var preset in _presets) preset.Button.Classes.Set("active", preset.Low == range.Low && preset.High == range.High);
            var captured = AudioFrequencyRange.For(_settings);
            _view.Update(captured.Low, captured.High, _source.Running ? _source.Spectrum : Array.Empty<double>());
            _tonal?.Refresh();
        }
        finally { _syncing = false; }
    }
}

internal sealed class FrequencyRangeView : Control
{
    private double _low = 20, _high = 20000;
    private double[] _spectrum = Array.Empty<double>();
    private bool? _dragLower;
    public event Action<double, double> RangeChanged;
    public FrequencyRangeView() { Cursor = new Cursor(StandardCursorType.SizeWestEast); }
    private Rect Plot => new(8, 6, Math.Max(1, Bounds.Width - 16), Math.Max(1, Bounds.Height - 27));
    private double X(double frequency) => Plot.X + Math.Log10(Math.Clamp(frequency, 20, 20000) / 20) / 3 * Plot.Width;
    public void Update(double low, double high, double[] spectrum) { _low = low; _high = high; _spectrum = spectrum; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        var plot = Plot;
        context.DrawRectangle(Classic.Track, new Pen(Classic.Line), plot, 3, 3);
        var selected = new Rect(X(_low), plot.Y, Math.Max(1, X(_high) - X(_low)), plot.Height);
        context.DrawRectangle(Classic.Brush("#244EA1FF"), null, selected);
        foreach (var frequency in new double[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
        {
            var x = X(frequency);
            context.DrawLine(new Pen(Classic.Line, .6), new Point(x, plot.Y), new Point(x, plot.Bottom));
            var text = new FormattedText(frequency >= 1000 ? (frequency / 1000).ToString("0") + "k" : frequency.ToString("0"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Classic.Mono), 10, Classic.Muted);
            context.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, Bounds.Width - text.Width)), plot.Bottom + 5));
        }
        for (int i = 0; i < Math.Min(_spectrum.Length, HapticEqualizer.Frequencies.Length); i++)
        {
            var f = HapticEqualizer.Frequencies[i]; var x = X(f); var height = Math.Clamp(_spectrum[i], 0, 1) * (plot.Height - 4);
            var width = Math.Min(24, plot.Width / 25);
            context.DrawRectangle(f >= _low && f <= _high ? Classic.Accent : Classic.Line, null, new Rect(x - width / 2, plot.Bottom - height - 1, width, Math.Max(1, height)), 2, 2);
        }
        foreach (var x in new[] { X(_low), X(_high) })
        {
            context.DrawLine(new Pen(Classic.Accent, 2), new Point(x, plot.Y), new Point(x, plot.Bottom));
            context.DrawRectangle(Classic.Accent, null, new Rect(x - 3, plot.Y + plot.Height / 2 - 9, 6, 18), 2, 2);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var x = e.GetPosition(this).X;
        _dragLower = Math.Abs(x - X(_low)) <= Math.Abs(x - X(_high)); e.Pointer.Capture(this); Move(x); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragLower.HasValue && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Move(e.GetPosition(this).X);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _dragLower = null; e.Pointer.Capture(null); }
    private void Move(double x)
    {
        var frequency = Math.Round(20 * Math.Pow(1000, Math.Clamp((x - Plot.X) / Plot.Width, 0, 1)));
        if (_dragLower == true) RangeChanged?.Invoke(Math.Min(frequency, _high - AudioFrequencyRange.Gap), _high);
        else RangeChanged?.Invoke(_low, Math.Max(frequency, _low + AudioFrequencyRange.Gap));
    }
}
