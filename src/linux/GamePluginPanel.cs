using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HapticLink.Core;

namespace HapticLink;

public sealed class GamePluginPanel : StackPanel
{
    private readonly Engine _engine;
    private readonly PluginManager _manager;
    private readonly string _id;
    private readonly PluginProfile _profile;
    private readonly CheckBox _enabled;
    private readonly TextBlock _status = Label(""), _level = Label("0%"), _message = Label(""), _application = Label("");
    private readonly SignalMeter _meter = new() { Height = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _address = new() { IsReadOnly = true };
    private readonly TextBlock _hostPortStatus = Label("");
    private bool _syncing;
    public GamePluginPanel(Engine engine, PluginProfile profile, Action<string> remove = null)
    {
        _engine = engine; _manager = engine.Plugins; _id = profile.Id; _profile = profile; Spacing = 4;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        heading.Children.Add(new TextBlock { Text = profile.Name.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Classic.Muted });
        _enabled = new CheckBox { Content = "Enable plugin", Name = "GameEnabled-" + _id };
        _enabled.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            _manager.SetEnabled(_id, _enabled.IsChecked == true);
            if (_enabled.IsChecked == true && !engine.S.Events.Enabled) engine.SetSourceEnabled(4, true);
            engine.S.Save(); Refresh();
        };
        Grid.SetColumn(_enabled, 1); heading.Children.Add(_enabled); Children.Add(heading);
        if (remove != null)
        {
            var removeButton = Button("Remove plugin", () => { remove(_id); return Task.CompletedTask; }); removeButton.Name = "RemovePlugin-" + _id;
            ToolTip.SetTip(removeButton, "Remove this HapticLink plugin and its tab. Undo is available in Plugins."); Grid.SetColumn(removeButton, 2); heading.Children.Add(removeButton);
        }
        if (profile.Kind != "cs2") Children.Add(Label(profile.Description));
        var meter = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*,52"), ColumnSpacing = 8 };
        _status.MaxLines = 1; _status.TextTrimming = TextTrimming.CharacterEllipsis; meter.Children.Add(_status);
        Grid.SetColumn(_meter, 1); meter.Children.Add(_meter); _level.FontFamily = Classic.Mono; _level.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(_level, 2); meter.Children.Add(_level); Children.Add(meter);
        var gainRow = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*,52"), ColumnSpacing = 8 };
        gainRow.Children.Add(Label("Intensity scale"));
        var gain = new Slider { Minimum = 0, Maximum = 2, Value = _manager.Gain(_id) };
        var amount = Label(gain.Value.ToString("P0")); amount.HorizontalAlignment = HorizontalAlignment.Right; amount.FontFamily = Classic.Mono;
        gain.ValueChanged += (_, _) => { _manager.SetGain(_id, gain.Value); amount.Text = gain.Value.ToString("P0"); };
        Grid.SetColumn(gain, 1); gainRow.Children.Add(gain); Grid.SetColumn(amount, 2); gainRow.Children.Add(amount); Children.Add(gainRow);
        if (profile.Kind == "cs2")
        {
            var install = Button("Install CS2 game config…", InstallCs2); ToolTip.SetTip(install, "Install once into game/csgo/cfg, restart CS2, then enable this plugin.");
            Children.Add(install); AddRules();
        }
        else if (profile.Companion == "overstim")
        {
            Children.Add(Label("OverStim reads Overwatch's screen and detects events. Its hero and detection controls stay in the companion app."));
            var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
            void Add(Button button) { button.Margin = new Thickness(0, 0, 6, 4); toolbar.Children.Add(button); }
            Add(Button("Get OverStim", async () => { var top = TopLevel.GetTopLevel(this); if (top != null) await top.Launcher.LaunchUriAsync(new Uri(GameIntegrationSetup.OverStimProject)); }));
            Add(Button("Configure OverStim", () => { GameIntegrationSetup.ConfigureOverStim(_manager.Endpoint(_profile, engine.Events.ActivePort)); _message.Text = "Connection configured. Open OverStim, select your hero and start detection."; return Task.CompletedTask; }));
            Add(Button("Choose OverStim app…", ChooseCompanion)); Add(Button("Launch OverStim", LaunchCompanion));
            Children.Add(toolbar); Children.Add(_application);
        }
        else
        {
            Children.Add(Label(profile.Setup)); Children.Add(_address);
            if (profile.Kind != "buttplug") AddRules();
        }
        if (profile.Kind == "cs2" || profile.Companion == "overstim")
        {
            var connection = new StackPanel { Spacing = 5 }; connection.Children.Add(_address);
            connection.Children.Add(Button("Copy connection address", CopyAddress));
            connection.Children.Add(Label(profile.Setup)); Children.Add(new Expander { Header = "Connection details", Content = connection });
        }
        else Children.Add(Button("Copy connection address", CopyAddress));
        if (profile.Kind == "buttplug")
        {
            if (profile.SourceUrl.Length > 0)
            {
                var guide = new StackPanel { Spacing = 4 };
                guide.Children.Add(Label(profile.SetupKind + " · External setup required"));
                guide.Children.Add(Button("Open project / install guide ↗", async () => { var top = TopLevel.GetTopLevel(this); if (top != null) await top.Launcher.LaunchUriAsync(new Uri(profile.SourceUrl)); }));
                if (profile.InstallRepositoryUrl.Length > 0)
                {
                    guide.Children.Add(new TextBox { Text = profile.InstallRepositoryUrl, IsReadOnly = true });
                    var copy = Button("Copy Dalamud repository", async () => { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard != null) await clipboard.SetTextAsync(profile.InstallRepositoryUrl); });
                    copy.Name = "CopyGameRepository-" + _id; guide.Children.Add(copy);
                }
                Children.Add(guide);
            }
            var hostPort = new StackPanel { Spacing = 4 };
            hostPort.Children.Add(Label("For clients with separate host and port fields. Only one game can use this address at a time; full connection addresses stay independent."));
            hostPort.Children.Add(_hostPortStatus);
            var use = Button("Use host / port mode", () => { _manager.SetHostPortPlugin(_id); _engine.S.Save(); _message.Text = "Host / port connections now belong to this tab. Reconnect the external mod."; Refresh(); return Task.CompletedTask; });
            use.Name = "UseHostPort-" + _id; hostPort.Children.Add(use);
            Children.Add(new Expander { Header = "Host / port connection", Content = hostPort });
        }
        _message.Foreground = Classic.Accent; Children.Add(_message); Refresh();
    }
    private void AddRules()
    {
        Children.Add(Label("EVENT EFFECTS — intensity and duration"));
        foreach (var rule in _profile.Rules)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,52,90"), ColumnSpacing = 8, MinHeight = 28 };
            var enabled = new CheckBox { Content = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(rule.Event.Replace('_', ' ')), IsChecked = rule.Enabled };
            enabled.IsCheckedChanged += (_, _) => rule.Enabled = enabled.IsChecked == true; row.Children.Add(enabled);
            var value = Label(rule.Level.ToString("P0")); value.FontFamily = Classic.Mono; value.HorizontalAlignment = HorizontalAlignment.Right;
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = rule.Level }; slider.ValueChanged += (_, _) => { rule.Level = slider.Value; value.Text = rule.Level.ToString("P0"); };
            Grid.SetColumn(slider, 1); row.Children.Add(slider); Grid.SetColumn(value, 2); row.Children.Add(value);
            var duration = new NumericUpDown { Minimum = 0, Maximum = 5000, Value = rule.DurationMs, Increment = 50, FormatString = "0' ms'", ShowButtonSpinner = false };
            duration.ValueChanged += (_, _) => { if (duration.Value is { } d) rule.DurationMs = (int)d; }; ToolTip.SetTip(duration, "Effect duration in milliseconds");
            Grid.SetColumn(duration, 3); row.Children.Add(duration); Children.Add(row);
        }
        Children.Add(Button("Apply event effects", () => { _manager.Install(PluginManager.Serialize(_profile)); _engine.S.Save(); _message.Text = "Event effects saved."; return Task.CompletedTask; }));
    }
    private async Task InstallCs2()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage == null) return;
        var candidates = GameIntegrationSetup.FindCs2CfgDirectories();
        var start = candidates.Length > 0 ? await storage.TryGetFolderFromPathAsync(candidates[0]) : null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose CS2's game/csgo/cfg folder", AllowMultiple = false, SuggestedStartLocation = start });
        if (folders.Count == 0) return;
        var directory = folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local game folder.");
        GameIntegrationSetup.InstallCs2Config(directory, _manager.Cs2Config(_profile)); _message.Text = "Game config installed. Restart CS2 to begin sending events.";
    }
    private async Task ChooseCompanion()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose the installed OverStim application", AllowMultiple = false });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath() ?? throw new IOException("Choose a local application.");
        if (!Path.GetFileNameWithoutExtension(path).Contains("OverStim", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose the OverStim application.");
        _engine.S.PluginApplications = new(_engine.S.PluginApplications) { [_id] = path }; _engine.S.Save(); Refresh();
    }
    private Task LaunchCompanion()
    {
        if (!_engine.S.PluginApplications.TryGetValue(_id, out var path) || !File.Exists(path)) throw new IOException("Choose the installed OverStim app first.");
        GameIntegrationSetup.ConfigureOverStim(_manager.Endpoint(_profile, _engine.Events.ActivePort));
        if (_manager.GetEnabled(_id) == null) _manager.SetEnabled(_id, true);
        if (!_engine.S.Events.Enabled) _engine.SetSourceEnabled(4, true);
        _engine.S.Save(); Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = true });
        _message.Text = "OverStim opened. Select your hero and start detection."; return Task.CompletedTask;
    }
    private async Task CopyAddress() { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard != null) await clipboard.SetTextAsync(_manager.Endpoint(_profile, _engine.Events.ActivePort)); }
    public void Refresh()
    {
        var installed = _manager.Installed.FirstOrDefault(p => p.Profile.Id == _id); if (installed == null) return;
        _syncing = true; _enabled.IsChecked = installed.Enabled; _syncing = false;
        var level = _engine.Events.ProfileLevel(_id); _meter.Value = level; _level.Text = level.ToString("P0");
        _status.Text = !installed.Enabled ? "Disabled" : !_engine.S.Events.Enabled ? "Event bridge disabled — enable it on Events" : !_engine.Events.Running ? _engine.Events.Status
            : _profile.Kind == "buttplug" ? _engine.Events.ProfileClientCount(_id) > 0 ? "Integration connected" : _profile.Companion == "overstim" ? "Waiting for OverStim" : "Waiting for integration" : "Listening for game events";
        _status.Foreground = !installed.Enabled ? Classic.Muted : _engine.Events.Running ? Classic.Good : Classic.Warn;
        _address.Text = _manager.Endpoint(_profile, _engine.Events.ActivePort);
        _hostPortStatus.Text = "Host: 127.0.0.1   Port: " + _engine.Events.ActivePort + "\n" + (_manager.HostPortPluginId == _id ? "Assigned to this tab" : "Use host / port mode to assign this address to this tab");
        _application.Text = _engine.S.PluginApplications.TryGetValue(_id, out var path) ? "Selected: " + Path.GetFileName(path) : "Choose the installed companion to launch it from this tab.";
        _message.IsVisible = !string.IsNullOrWhiteSpace(_message.Text);
    }
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception ex) { _message.Text = ex.Message; } finally { button.IsEnabled = true; _message.IsVisible = !string.IsNullOrWhiteSpace(_message.Text); } }; return button;
    }
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 11, Foreground = Classic.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
}
