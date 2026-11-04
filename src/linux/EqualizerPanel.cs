using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using HapticLink.Core;
using HapticLink.Sources;

namespace HapticLink;

public sealed class EqualizerPanel : StackPanel
{
    private readonly ProgressBar[] _meters = new ProgressBar[10];
    private readonly Slider[] _sliders = new Slider[10];
    private readonly AudioSource _source;
    private readonly Settings _settings;
    private readonly CheckBox _enabled;
    private readonly Slider _preamp;
    public EqualizerPanel(Settings settings, AudioSource source)
    {
        _source = source; _settings = settings; Spacing = 8;
        settings.AudioEqGainsDb = settings.AudioEqGainsDb?.Length == 10 ? settings.AudioEqGainsDb : new double[10];
        Children.Add(new TextBlock { Text = "HAPTICS EQ", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 14, 0, 0) });
        Children.Add(new TextBlock { Text = "Shapes the audio used to generate haptics. Speaker/headphone sound stays unchanged.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var enabled = new CheckBox { Content = "Enable equalizer", IsChecked = settings.AudioEqEnabled };
        _enabled = enabled;
        enabled.IsCheckedChanged += (_, _) => settings.AudioEqEnabled = enabled.IsChecked == true; Children.Add(enabled);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void AddPreset(string label, double[] gains)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => { for (int i = 0; i < 10; i++) _sliders[i].Value = gains[i]; };
            presets.Children.Add(button);
        }
        AddPreset("Flat", new double[10]);
        AddPreset("Impacts", new double[] { 3, 6, 4, 2, -3, -6, -6, -4, -3, -3 });
        AddPreset("Detail", new double[] { -6, -4, -3, -2, 0, 2, 4, 5, 3, 0 });
        Children.Add(presets);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*,*,*,*") };
        for (int i = 0; i < 10; i++)
        {
            var index = i;
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
            var frequency = HapticEqualizer.Frequencies[i];
            column.Children.Add(new TextBlock { Text = frequency >= 1000 ? (frequency / 1000).ToString("0.#") + "k" : frequency.ToString("0"), HorizontalAlignment = HorizontalAlignment.Center });
            var value = new TextBlock { Text = settings.AudioEqGainsDb[i].ToString("+0;-0;0"), HorizontalAlignment = HorizontalAlignment.Center };
            _sliders[i] = new Slider { Orientation = Orientation.Vertical, Minimum = -12, Maximum = 12, Value = settings.AudioEqGainsDb[i], Height = 145, TickFrequency = 1 };
            _sliders[i].ValueChanged += (_, _) => { settings.AudioEqGainsDb[index] = _sliders[index].Value; value.Text = _sliders[index].Value.ToString("+0;-0;0"); };
            column.Children.Add(_sliders[i]); column.Children.Add(value);
            _meters[i] = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinWidth = 0, MaxWidth = 35, Width = 35 };
            column.Children.Add(_meters[i]); Grid.SetColumn(column, i); grid.Children.Add(column);
        }
        Children.Add(grid);
        Children.Add(new TextBlock { Text = "Preamp (dB)" });
        var preamp = new Slider { Minimum = -24, Maximum = 12, Value = settings.AudioEqPreampDb };
        _preamp = preamp;
        var amount = new TextBlock { Text = preamp.Value.ToString("F1") + " dB" };
        preamp.ValueChanged += (_, _) => { settings.AudioEqPreampDb = preamp.Value; amount.Text = preamp.Value.ToString("F1") + " dB"; };
        Children.Add(preamp); Children.Add(amount);
    }
    public void Refresh()
    {
        var values = _source.Spectrum;
        for (int i = 0; i < _meters.Length; i++)
        {
            _meters[i].Value = _source.Running ? values[i] : 0;
            if (_settings.AudioEqGainsDb?.Length == 10 && _sliders[i].Value != _settings.AudioEqGainsDb[i]) _sliders[i].Value = _settings.AudioEqGainsDb[i];
        }
        if (_preamp.Value != _settings.AudioEqPreampDb) _preamp.Value = _settings.AudioEqPreampDb;
        if (_enabled.IsChecked != _settings.AudioEqEnabled) _enabled.IsChecked = _settings.AudioEqEnabled;
    }
}
