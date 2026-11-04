using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HapticLink.Core;
using HapticLink.Sources;
using HapticLink.Toy;

namespace HapticLink;

public sealed class MainWindow : Window
{
    private readonly Settings _s = Settings.Load();
    private readonly Engine _engine;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock _connection = Text("Enter your phone's Game Mode IP address."), _output = Text("0%"), _state = Text("DISARMED");
    private readonly TextBlock[] _status = new TextBlock[5];
    private readonly CheckBox[] _sourceToggles = new CheckBox[5];
    private CheckBox _overlayToggle, _focusToggle;
    private TabControl _tabs;
    private PluginManagerPanel _plugins;
    private readonly Dictionary<string, (TabItem Tab, GamePluginPanel Panel, string Stamp)> _gameTabs = new();
    private readonly SignalMeter[] _meters = new SignalMeter[5];
    private readonly SignalMeter[] _compactMeters = new SignalMeter[5];
    private readonly TextBlock[] _compactLevels = new TextBlock[5];
    private readonly TextBlock[] _levels = new TextBlock[5];
    private readonly Border[] _dots = new Border[5];
    private readonly TextBlock _connectionPillText = Text("DISCONNECTED"), _outputLevel = Text("0 / 20");
    private Border _connectionPill;
    private readonly HistoryChart _history = new() { Height = 52 };
    private EqualizerPanel _equalizer;
    private OverlayWindow _overlay;
    private readonly TextBlock _eventEndpoint = Text(""), _eventHistory = Text(""), _gameStatus = Text("No game selected"), _overlayStatus = Text("");
    private readonly SignalMeter _outputMeter = new() { Height = 18 };
    private readonly Button _arm = new() { Content = "ARM OUTPUT", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _log = new() { Name = "ActivityLog", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 92, FontSize = 11, Foreground = Classic.Muted, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBox _host;
    private readonly ComboBox _toys = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private StackPanel _params;
    private TextBlock _avatar, _routing;
    private readonly TabItem _oscTab = new() { Header = "VRChat OSC" };
    private EngineState _latest;
    private bool _closing, _closed;
    private int _ticks;
    private string _lastAvatar, _lastToys = "", _lastParams = "";

    public MainWindow()
    {
        Title = "HapticLink · 1.5.1 preview"; Width = 1200; Height = 800; MinWidth = 1040; MinHeight = 700;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://HapticLink/assets/hapticlink.ico")));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Classic.Background;
        _engine = new Engine(_s);
        _engine.Plugins.EnsureGameDefaults();
        _host = new TextBox { Text = _s.Host, Watermark = "192.168.1.42", MinWidth = 0 };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(10) };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var brand = Row(new TextBlock { Text = "HAPTICLINK", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Classic.Accent }, Text("game → toy bridge"));
        brand.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(brand);
        _connectionPillText.FontSize = 11; _connectionPillText.FontWeight = FontWeight.SemiBold;
        _connectionPill = new Border { Child = _connectionPillText, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 3), BorderBrush = Classic.Danger, Background = Classic.Brush("#2A1D1D") };
        _connectionPillText.Foreground = Classic.Danger;
        _state.FontSize = 11; _state.FontWeight = FontWeight.SemiBold;
        var status = Row(_connectionPill, _state); status.HorizontalAlignment = HorizontalAlignment.Center; status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 1); header.Children.Add(status);
        _arm.Name = "ArmOutput"; _arm.MinWidth = 130; _arm.FontWeight = FontWeight.SemiBold; _arm.Padding = new Thickness(18, 6);
        _arm.Click += (_, _) => ToggleArm();
        var panic = Button("PANIC — STOP", () => { _engine.Panic(); return Task.CompletedTask; });
        panic.Name = "PanicStop"; panic.Classes.Add("danger"); panic.Padding = new Thickness(18, 6); panic.FontWeight = FontWeight.SemiBold;
        var actions = Row(_arm, panic); Grid.SetColumn(actions, 2); header.Children.Add(actions);
        var headerCard = Card(header); headerCard.Padding = new Thickness(14, 10); headerCard.Margin = new Thickness(0, 0, 0, 10); root.Children.Add(headerCard);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*") }; Grid.SetRow(body, 1); root.Children.Add(body);
        var left = Panel(); left.Spacing = 10;
        var connection = Panel(); connection.Children.Add(Heading("CONNECTION"));
        connection.Children.Add(Text("Phone / PC running Lovense Game Mode"));
        connection.Children.Add(Columns(_host, Button("Scan LAN", Scan)));
        connection.Children.Add(Columns(Button("Connect", Connect), Button("Disconnect", async () =>
        {
            _engine.Arm(false); await _engine.Client.StopAsync(); _engine.Client.Disconnect();
            _connection.Text = "Disconnected."; RefreshToys();
        }), true));
        connection.Children.Add(Text("Toy")); connection.Children.Add(_toys);
        _toys.SelectionChanged += (_, _) => { if (_toys.SelectedItem is Choice c) _s.ToyId = c.Id; };
        AddEnum(connection, "Function", new[] { "Vibrate", "Rotate", "Pump", "Thrusting", "Suction", "Oscillate", "All" }, _s.Action, x => _s.Action = x, 66);
        connection.Children.Add(Columns(Button("Test 25% · 2 s", () => { _engine.Test(.25, 2); return Task.CompletedTask; }), Button("Refresh toys", async () => { await _engine.Client.RefreshToysAsync(); RefreshToys(); }), true));
        _connection.FontSize = 11; _connection.Margin = new Thickness(0, 3, 0, 0); connection.Children.Add(_connection);
        left.Children.Add(Card(connection));

        var output = Panel(); output.Children.Add(Heading("OUTPUT"));
        _output.FontSize = 26; _output.FontWeight = FontWeight.Bold; _output.FontFamily = Classic.Mono; _output.Foreground = Classic.Text;
        _outputLevel.FontFamily = Classic.Mono; _outputLevel.HorizontalAlignment = HorizontalAlignment.Right;
        output.Children.Add(Columns(_output, _outputLevel)); output.Children.Add(_outputMeter); output.Children.Add(_history);
        AddSlider(output, "Master cap", 0, 1, _s.MasterCap, x => _s.MasterCap = x, "P0", 85);
        AddSlider(output, "Update rate", 1, 20, _s.UpdateHz, x => _s.UpdateHz = (int)Math.Round(x), "0' Hz'", 85, 1);
        AddSlider(output, "Auto-stop", 0, 240, _s.MaxContinuousMinutes, x => _s.MaxContinuousMinutes = (int)Math.Round(x), "0' min'", 85, 1);
        AddEnum(output, "Mix mode", _s.Mix, x => _s.Mix = x, 85);
        output.Children.Add(Text("Ctrl+Alt+X  panic  ·  Ctrl+Alt+A  arm\nGlobal shortcuts: see Settings"));
        left.Children.Add(Card(output));
        body.Children.Add(new ScrollViewer { Content = left, Margin = new Thickness(0, 0, 10, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }; Grid.SetColumn(right, 1); body.Children.Add(right);
        var sources = Panel(); sources.Children.Add(Heading("SOURCE LEVELS"));
        var sourceNames = new[] { "Controller", "Game audio", "VRChat OSC", "Manual", "Events" };
        for (int i = 0; i < 5; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("82,*,48,12"), Height = 22 };
            row.Children.Add(Text(sourceNames[i]));
            _meters[i] = new SignalMeter { Height = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_meters[i], 1); row.Children.Add(_meters[i]);
            _levels[i] = Text("0%"); _levels[i].FontFamily = Classic.Mono; _levels[i].HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_levels[i], 2); row.Children.Add(_levels[i]);
            _dots[i] = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Classic.Line, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_dots[i], 3); row.Children.Add(_dots[i]); sources.Children.Add(row);
        }
        var sourceCard = Card(sources); sourceCard.Margin = new Thickness(0, 0, 0, 10); right.Children.Add(sourceCard);
        var compactSources = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*") };
        for (int i = 0; i < 5; i++)
        {
            var cell = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, i == 4 ? 0 : 10, 0) };
            var name = Text(sourceNames[i]); name.FontSize = 10; cell.Children.Add(name);
            _compactLevels[i] = Text("0%"); _compactLevels[i].FontFamily = Classic.Mono; _compactLevels[i].FontSize = 10;
            Grid.SetColumn(_compactLevels[i], 1); cell.Children.Add(_compactLevels[i]);
            _compactMeters[i] = new SignalMeter { Height = 8, Margin = new Thickness(0, 5, 0, 0) }; Grid.SetRow(_compactMeters[i], 1); Grid.SetColumnSpan(_compactMeters[i], 2); cell.Children.Add(_compactMeters[i]);
            Grid.SetColumn(cell, i); compactSources.Children.Add(cell);
        }
        var compactCard = Card(compactSources); compactCard.Padding = new Thickness(12, 9); compactCard.Margin = new Thickness(0, 0, 0, 10); compactCard.IsVisible = false; right.Children.Add(compactCard);
        SizeChanged += (_, _) => { var compact = Bounds.Height > 0 && Bounds.Height < 760; sourceCard.IsVisible = !compact; compactCard.IsVisible = compact; };
        var tabs = new TabControl { FontSize = 12, Padding = new Thickness(0) };
        _tabs = tabs;
        tabs.Items.Add(new TabItem { Header = "Controller", Content = Scroll(RumblePanel()) });
        tabs.Items.Add(new TabItem { Header = "Audio", Content = Scroll(AudioPanel()) });
        _oscTab.Content = Scroll(OscPanel()); tabs.Items.Add(_oscTab);
        tabs.Items.Add(new TabItem { Header = "Manual", Content = Scroll(ManualPanel()) });
        tabs.Items.Add(new TabItem { Header = "Events", Content = Scroll(EventsPanel()) });
        tabs.Items.Add(new TabItem { Header = "Overlay", Content = Scroll(OverlayPanel()) });
        _plugins = new PluginManagerPanel(_engine, Navigate);
        tabs.Items.Add(new TabItem { Header = "Plugins", Content = Scroll(_plugins) });
        tabs.Items.Add(new TabItem { Header = "Settings", Content = Scroll(SettingsPanel()) });
        var tabCard = Card(tabs); tabCard.Padding = new Thickness(6); Grid.SetRow(tabCard, 1); right.Children.Add(tabCard);
        var logPanel = Panel(); logPanel.Spacing = 3; logPanel.Children.Add(Heading("LOG")); logPanel.Children.Add(_log);
        var logCard = Card(logPanel); logCard.Padding = new Thickness(10, 8); logCard.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(logCard, 2); root.Children.Add(logCard);
        Content = root;
        SyncGameTabs(); _engine.Plugins.Changed += PluginChanged;
        _engine.Updated += state => _latest = state;
        Log.Line += OnLog;
        _timer.Tick += (_, _) => Refresh();
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.X) { _engine.Panic(); e.Handled = true; }
            if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.A) { ToggleArm(); e.Handled = true; }
        };
        Opened += (_, _) => { _engine.Start(); _timer.Start(); _ = ListenCommands(); };
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true; _engine.Plugins.Changed -= PluginChanged; _timer.Stop(); _lifetime.Cancel(); _overlay?.Close(); _engine.Panic(); _s.Save();
            await _engine.ShutdownAsync();
            Log.Line -= OnLog; _engine.Dispose(); _closed = true; Close();
        };
        RefreshToys();
    }

    private void ToggleArm()
    {
        if (_engine.Panicked) _engine.ClearPanic();
        _engine.Arm(!_engine.Armed);
    }

    private async Task Connect()
    {
        _s.Host = _host.Text?.Trim() ?? "";
        _engine.Arm(false);
        await _engine.Client.StopAsync();
        _connection.Text = "Connecting…";
        var success = await _engine.Client.ConnectAsync(_s, _lifetime.Token);
        _connection.Text = success ? "Connected · " + _engine.Client.BaseUrl : "No response. Check Game Mode and your LAN.";
        RefreshToys(); _s.Save();
    }
    private async Task Scan()
    {
        _connection.Text = "Scanning LAN…";
        var found = await LovenseClient.DiscoverAsync(_s.HttpPort, _s.HttpsPort, ct: _lifetime.Token);
        _connection.Text = found.Count == 0 ? "No devices found." : "Found: " + string.Join(", ", found);
        if (found.Count > 0) _host.Text = found[0];
    }

    private StackPanel SourcePanel(int index, string title, string description)
    {
        var enabled = Check("Enable source", _engine.SettingsFor(index).Enabled, x => { if (_engine.SettingsFor(index).Enabled != x) _engine.SetSourceEnabled(index, x); });
        _sourceToggles[index] = enabled;
        var p = Panel(); p.Children.Add(Columns(Heading(title), enabled));
        p.Children.Add(Text(description));
        _status[index] = Text("Idle"); _status[index].FontFamily = Classic.Mono; p.Children.Add(_status[index]);
        return p;
    }
    private void Shape(StackPanel p, SourceSettings s)
    {
        p.Children.Add(Heading("SHAPING"));
        AddSlider(p, "Gain", 0, 4, s.Gain, x => s.Gain = x);
        AddSlider(p, "Curve", .2, 3, s.Curve, x => s.Curve = x);
        AddSlider(p, "Deadzone", 0, .95, s.Deadzone, x => s.Deadzone = x, "P0");
        AddSlider(p, "Attack (ms)", 0, 1000, s.AttackMs, x => s.AttackMs = x, "F0");
        AddSlider(p, "Release (ms)", 0, 2000, s.ReleaseMs, x => s.ReleaseMs = x, "F0");
    }
    private StackPanel ManualPanel()
    {
        var p = SourcePanel(3, "MANUAL & PATTERNS", "Set a level or let a repeating pattern drive it.");
        AddSlider(p, "Level", 0, 1, _s.ManualLevel, x => _s.ManualLevel = x, "P0");
        AddEnum(p, "Pattern", _s.Pattern, x => _s.Pattern = x);
        AddSlider(p, "Period (seconds)", .1, 20, _s.PatternPeriodSec, x => _s.PatternPeriodSec = x);
        AddSlider(p, "Depth", 0, 1, _s.PatternDepth, x => _s.PatternDepth = x, "P0");
        Shape(p, _s.Manual); return p;
    }
    private StackPanel AudioPanel()
    {
        var p = Panel();
        var enabled = Check("Enable audio", _s.Audio.Enabled, x => { if (_s.Audio.Enabled != x) _engine.SetSourceEnabled(1, x); });
        _sourceToggles[1] = enabled; _status[1] = Text("Idle"); _status[1].MaxLines = 1; _status[1].TextTrimming = TextTrimming.CharacterEllipsis;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        heading.Children.Add(enabled); Grid.SetColumn(_status[1], 1); heading.Children.Add(_status[1]); p.Children.Add(heading);
        var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        choices.Items.Add(new Choice("", "Default output monitor"));
        if (_s.AudioDeviceId.Length > 0) choices.Items.Add(new Choice(_s.AudioDeviceId, _s.AudioDeviceId));
        choices.SelectedIndex = choices.Items.Count - 1;
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is Choice c) _s.AudioDeviceId = c.Id; };
        var devices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        devices.Children.Add(choices);
        var refresh = Button("Refresh", async () => SetChoices(choices, await Task.Run(AudioSource.ListDevices), _s.AudioDeviceId, "Default output monitor"));
        Grid.SetColumn(refresh, 1); devices.Children.Add(refresh);
        var apply = Button("Apply device", () => { _engine.RestartSource(1); return Task.CompletedTask; }); Grid.SetColumn(apply, 2); devices.Children.Add(apply); p.Children.Add(devices);
        _equalizer = new EqualizerPanel(_s, _engine.Audio, compact: true); p.Children.Add(_equalizer);
        var advanced = Panel();
        var modeLabels = new[] { "Chosen output device", "One application", "Selected game window" };
        if (!OperatingSystem.IsWindows())
        {
            AddEnum(advanced, "Capture", modeLabels, modeLabels[(int)_s.AudioCaptureMode], x => _s.AudioCaptureMode = (AudioCaptureMode)Array.IndexOf(modeLabels, x));
            var apps = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            apps.SelectionChanged += (_, _) => { if (apps.SelectedItem is AudioApplication app) { _s.AudioProcessId = app.ProcessId; _s.AudioStreamIndex = app.Stream; } };
            advanced.Children.Add(apps);
            advanced.Children.Add(Button("Refresh playing applications", async () => { var list = await Task.Run(AudioRouting.Applications); var selected = _s.AudioProcessId; apps.ItemsSource = list; apps.SelectedItem = list.FirstOrDefault(a => a.ProcessId == selected); }));
            advanced.Children.Add(Text("Application capture follows the selected playback stream. Game capture follows the window selected on Overlay. Missing target audio waits without recording other apps."));
        }
        else if (_s.AudioCaptureMode != AudioCaptureMode.OutputMonitor)
        {
            advanced.Children.Add(Text("The saved application capture mode requires Linux."));
            advanced.Children.Add(Button("Use output-device capture", () => { _s.AudioCaptureMode = AudioCaptureMode.OutputMonitor; _engine.RestartSource(1); return Task.CompletedTask; }));
        }
        AddSlider(advanced, "Quiet floor (dB)", -90, -1, _s.AudioFloorDb, x => _s.AudioFloorDb = x, "F0");
        AddSlider(advanced, "Loud ceiling (dB)", -60, 0, _s.AudioCeilDb, x => _s.AudioCeilDb = x, "F0");
        Shape(advanced, _s.Audio); advanced.Children.Add(_equalizer.TonalControls);
        p.Children.Add(new Expander { Header = "Advanced audio settings", Content = advanced });
        return p;
    }
    private StackPanel EventsPanel()
    {
        var p = SourcePanel(4, "INTIFACE & GAME EVENTS", "Point an Intiface/Buttplug integration at this local server and select HapticLink virtual vibration. Events enter the mixer; output still needs arming.");
        p.Children.Add(_eventEndpoint);
        AddNumber(p, "Local server port", 1024, 65535, _s.IntegrationPort, x => _s.IntegrationPort = x);
        AddNumber(p, "Client ping timeout (ms)", 1000, 30000, _s.IntegrationTimeoutMs, x => _s.IntegrationTimeoutMs = x);
        p.Children.Add(Button("Apply / restart bridge", () => { _engine.RestartSource(4); return Task.CompletedTask; }));
        p.Children.Add(Text("Receives vibration commands from Buttplug protocol v2, v3 and v4.0 clients. Configure the integration's server URL; do not point it at Intiface Central. Position, rotation and sensor features are not advertised. Windows-only integrations still need their supported OS."));
        p.Children.Add(Heading("GAME EVENT RULES"));
        var rules = Panel(); p.Children.Add(rules);
        void AddRule(GameEventRule rule)
        {
            var box = Panel(); box.Children.Add(Check(rule.Event, rule.Enabled, x => rule.Enabled = x));
            AddSlider(box, "Intensity", 0, 1, rule.Level, x => rule.Level = x, "P0");
            AddNumber(box, "Duration (ms)", 0, 5000, rule.DurationMs, x => rule.DurationMs = x);
            AddNumber(box, "Cooldown (ms)", 0, 10000, rule.CooldownMs, x => rule.CooldownMs = x);
            box.Children.Add(Button("Preview " + rule.Event, () => { _engine.Events.Trigger(rule.Event); return Task.CompletedTask; }));
            rules.Children.Add(new Expander { Header = rule.Event, Content = box });
        }
        _s.EventRules ??= new(); foreach (var rule in _s.EventRules) AddRule(rule);
        var custom = new TextBox { Watermark = "Custom event name, e.g. quest_complete" }; p.Children.Add(custom);
        p.Children.Add(Button("Add event rule", () =>
        {
            var name = custom.Text?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 80 || _s.EventRules.Count >= 64 || _s.EventRules.Any(r => r.Event == name)) return Task.CompletedTask;
            var rule = new GameEventRule { Event = name }; _s.EventRules = _s.EventRules.Append(rule).ToList(); AddRule(rule); custom.Text = "";
            return Task.CompletedTask;
        }));
        p.Children.Add(Text("HTTP POST /event accepts {\"event\":\"kill\"}, or an explicit level (0..1) and durationMs (0..5000). CS2 can send Game State Integration data to /cs2. A ready-to-copy CS2 configuration is included in integrations/."));
        p.Children.Add(Heading("RECENT EVENTS")); p.Children.Add(_eventHistory);
        Shape(p, _s.Events); return p;
    }
    private StackPanel OverlayPanel()
    {
        var p = Panel(); p.Children.Add(Heading("GAME WINDOW & OVERLAY"));
        var windows = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        windows.SelectionChanged += (_, _) =>
        {
            if (windows.SelectedItem is GameWindow window) { _s.GameWindowId = window.Id; _s.GameProcessId = window.ProcessId; }
        };
        p.Children.Add(windows);
        p.Children.Add(Button("Refresh game windows", async () =>
        {
            var list = await Task.Run(GameFocus.ListWindows); windows.ItemsSource = list;
            windows.SelectedItem = list.FirstOrDefault(w => w.Id == GameFocus.NormalizeId(_s.GameWindowId));
        }));
        p.Children.Add(Button("Clear game selection", () => { _s.GameWindowId = ""; _s.GameProcessId = 0; windows.SelectedItem = null; return Task.CompletedTask; }));
        p.Children.Add(_gameStatus);
        _focusToggle = Check("Pause all output when the selected game loses focus", _s.PauseWhenGameUnfocused, x => _s.PauseWhenGameUnfocused = x); p.Children.Add(_focusToggle);
        _overlayToggle = Check("Show overlay", _s.OverlayEnabled, x => _s.OverlayEnabled = x); p.Children.Add(_overlayToggle);
        p.Children.Add(Check("Interactive overlay — drag header and use controls", _s.OverlayInteractive, x => _s.OverlayInteractive = x));
        p.Children.Add(Check("Show only while the selected game is focused", _s.OverlayOnlyWhenGameFocused, x => _s.OverlayOnlyWhenGameFocused = x));
        AddSlider(p, "Overlay opacity", .2, 1, _s.OverlayOpacity, x => _s.OverlayOpacity = x, "P0");
        AddNumber(p, "Horizontal offset (pixels)", -2000, 8000, _s.OverlayOffsetX, x => _s.OverlayOffsetX = x);
        AddNumber(p, "Vertical offset (pixels)", -2000, 8000, _s.OverlayOffsetY, x => _s.OverlayOffsetY = x);
        p.Children.Add(_overlayStatus);
        p.Children.Add(Text("The overlay is a separate transparent, topmost window for windowed/borderless games. Default mode passes clicks through to the game. Interactive mode lets you drag it and use Arm/Panic. Exclusive fullscreen may cover it. X11/XWayland window discovery requires wmctrl and xprop; native Wayland windows may not be discoverable."));
        p.Children.Add(Text("Global desktop shortcuts: HapticLink --toggle-overlay, HapticLink --overlay-interact, and HapticLink --panic. If focus cannot be verified with focus pausing enabled, output pauses."));
        return p;
    }
    private StackPanel RumblePanel()
    {
        var p = SourcePanel(0, "CONTROLLER RUMBLE", OperatingSystem.IsWindows() ? "Forward your controller through ViGEm and turn game rumble into haptics." : "Mirror a physical gamepad through a virtual gamepad and read the game's rumble.");
        var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        choices.Items.Add(new Choice(_s.RumbleDevice, _s.RumbleDevice.Length == 0 ? "First accessible gamepad" : _s.RumbleDevice));
        choices.SelectedIndex = 0;
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is Choice c) _s.RumbleDevice = c.Id; };
        p.Children.Add(Columns(choices, Button("Refresh", async () =>
        {
            var devices = await Task.Run(() => RumbleSource.ListDevices(_s));
            SetChoices(choices, devices, _s.RumbleDevice, "First accessible gamepad");
        })));
        p.Children.Add(Button("Apply / restart controller", () => { _engine.RestartSource(0); return Task.CompletedTask; }));
        AddSlider(p, "Heavy motor weight", 0, 2, _s.RumbleLargeWeight, x => _s.RumbleLargeWeight = x);
        AddSlider(p, "Light motor weight", 0, 2, _s.RumbleSmallWeight, x => _s.RumbleSmallWeight = x);
        Shape(p, _s.Rumble);
        var setup = Panel();
        setup.Children.Add(Check("Forward controller input", _s.RumblePassthrough, x => _s.RumblePassthrough = x));
        if (!OperatingSystem.IsWindows()) setup.Children.Add(Check("Grab physical gamepad exclusively", _s.RumbleGrab, x => _s.RumbleGrab = x));
        setup.Children.Add(Text(OperatingSystem.IsWindows()
            ? "Install ViGEmBus if needed. Use HidHide to hide the physical controller from games so they receive the virtual controller once."
            : "Requires python3-evdev, /dev/uinput access and access to the selected gamepad. Select HapticLink in Steam's controller order. See README-Linux.md for setup."));
        p.Children.Add(new Expander { Header = "Controller setup", Content = setup }); return p;
    }
    private StackPanel OscPanel()
    {
        _params = Panel(); _avatar = Text("No avatar detected"); _routing = Text("");
        var p = SourcePanel(2, "VRCHAT OSC", "Enable OSC in VRChat. Existing SPS detection and per-device routing are retained.");
        p.Children.Add(_avatar); p.Children.Add(_routing);
        p.Children.Add(Check("Remember settings for this avatar", _s.OscLiveIsOwnProfile, x =>
        {
            if (x) _s.RememberOscAvatar(_engine.Osc.AvatarName ?? ""); else _s.ForgetOscAvatar();
        }));
        p.Children.Add(Check("Auto-detect sensors", _s.OscAutoSensors, x => _s.OscAutoSensors = x));
        p.Children.Add(Check("Include self sensors", _s.OscAutoSelf, x => _s.OscAutoSelf = x));
        p.Children.Add(Check("Send OGB_ENABLED heartbeat", _s.OscOgbEnable, x => _s.OscOgbEnable = x));
        AddNumber(p, "Listen port", 1024, 65535, _s.OscPort, x => _s.OscPort = x);
        AddNumber(p, "VRChat input port", 1, 65535, _s.OscSendPort, x => _s.OscSendPort = x);
        AddNumber(p, "OSCQuery port (0 = auto)", 0, 65535, _s.OscQueryPort, x => _s.OscQueryPort = x);
        AddField(p, "OSC folder (blank = detect Steam / Proton)", _s.VrchatOscDirectory, x => _s.VrchatOscDirectory = x);
        p.Children.Add(Button("Apply / restart OSC", () => { _engine.RestartSource(2); return Task.CompletedTask; }));
        AddSlider(p, "Motion", 0, 1, _s.OscMotion, x => _s.OscMotion = x, "P0");
        AddSlider(p, "Full motion speed", .1, 10, _s.OscMotionFullSpeed, x => _s.OscMotionFullSpeed = x);
        AddSlider(p, "Idle strength", 0, 1, _s.OscIdle, x => _s.OscIdle = x, "P0");
        AddSlider(p, "Touch weight", 0, 1, _s.OscTouchWeight, x => _s.OscTouchWeight = x, "P0");
        AddSlider(p, "Frot weight", 0, 1, _s.OscFrotWeight, x => _s.OscFrotWeight = x, "P0");
        p.Children.Add(Check("Route sensors by device zones", _s.OscAutoParts, x => _s.OscAutoParts = x));
        AddSlider(p, "Other zones weight", 0, 1, _s.OscSympathy, x => _s.OscSympathy = x, "P0");
        p.Children.Add(Check("Two-motor travel", _s.OscTravel, x => _s.OscTravel = x));
        p.Children.Add(Check("Swap Edge motors", _s.OscEdgeSwap, x => _s.OscEdgeSwap = x));
        p.Children.Add(Check("Gradual build-up", _s.OscBuildUp, x => _s.OscBuildUp = x));
        AddSlider(p, "Build-up start", 0, 1, _s.OscBuildStart, x => _s.OscBuildStart = x, "P0");
        AddSlider(p, "Build-up minutes", .1, 30, _s.OscBuildMinutes, x => _s.OscBuildMinutes = x);
        Shape(p, _s.Osc);
        p.Children.Add(Heading("OTHER PARAMETERS")); p.Children.Add(_params); return p;
    }
    private StackPanel SettingsPanel()
    {
        var p = Panel(); p.Children.Add(Heading("SETTINGS"));
        AddNumber(p, "Command expiry (seconds)", 1, 10, _s.DeadManSeconds, x => _s.DeadManSeconds = x);
        AddNumber(p, "Phone HTTP port", 1, 65535, _s.HttpPort, x => _s.HttpPort = x);
        AddNumber(p, "Phone HTTPS port", 1, 65535, _s.HttpsPort, x => _s.HttpsPort = x);
        p.Children.Add(Check("Prefer HTTPS", _s.PreferHttps, x => _s.PreferHttps = x));
        AddField(p, "Python executable", _s.PythonCommand, x => _s.PythonCommand = x);
        p.Children.Add(Button("Save settings", () => { _s.Save(); return Task.CompletedTask; }));
        p.Children.Add(Text("Config and log: " + Settings.Dir));
        p.Children.Add(Text("Shortcuts in this window: Ctrl+Alt+X panic · Ctrl+Alt+A arm. For desktop-wide keys, bind HapticLink --panic, --toggle-arm, --toggle-overlay or --overlay-interact in your desktop settings."));
        p.Children.Add(Text("Copy a Windows config.json into this folder while HapticLink is closed. Linux audio and gamepad selections must be chosen again. The app always starts disarmed. Global shortcuts are configured in your desktop; --panic also works on Wayland."));
        return p;
    }

    private void Refresh()
    {
        if (_latest is not { } st) return;
        _output.Text = st.Output.ToString("P0"); _outputMeter.Value = st.Output; _outputLevel.Text = st.OutputLevel20 + " / 20"; _history.Update(_engine.OutputHistory());
        _state.Text = st.Panicked ? "PANIC STOPPED" : st.FocusPaused ? "PAUSED · GAME UNFOCUSED" : st.SafetyTripped ? "AUTO-STOP REACHED" : st.Armed ? "ARMED" : "DISARMED";
        _arm.Content = st.Armed ? "DISARM OUTPUT" : "ARM OUTPUT";
        _state.Foreground = st.Panicked ? Classic.Danger : st.FocusPaused || st.SafetyTripped ? Classic.Warn : st.Armed ? Classic.Good : Classic.Muted;
        _arm.Classes.Set("armed", st.Armed);
        _connectionPillText.Text = st.Connected ? "CONNECTED" : "DISCONNECTED";
        _connectionPillText.Foreground = _connectionPill.BorderBrush = st.Connected ? Classic.Good : Classic.Danger;
        _connectionPill.Background = Classic.Brush(st.Connected ? "#192B23" : "#2A1D1D");
        for (int i = 0; i < 5; i++)
        {
            if (_sourceToggles[i].IsChecked != _engine.SettingsFor(i).Enabled) _sourceToggles[i].IsChecked = _engine.SettingsFor(i).Enabled;
            _meters[i].Value = st.Shaped[i]; _levels[i].Text = st.Shaped[i].ToString("P0"); _status[i].Text = st.SourceStatus[i];
            _compactMeters[i].Value = st.Shaped[i]; _compactLevels[i].Text = st.Shaped[i].ToString("P0");
            _dots[i].Background = st.SourceRunning[i] ? Classic.Good : _engine.SettingsFor(i).Enabled ? Classic.Warn : Classic.Line;
            ToolTip.SetTip(_dots[i], st.SourceStatus[i]);
        }
        _equalizer.Refresh();
        _overlayToggle.IsChecked = _s.OverlayEnabled; _focusToggle.IsChecked = _s.PauseWhenGameUnfocused;
        if (_ticks % 5 == 0) { _plugins.Refresh(); foreach (var gameTab in _gameTabs.Values) gameTab.Panel.Refresh(); }
        _eventEndpoint.Text = _engine.Events.Endpoint;
        _gameStatus.Text = _engine.Game.State.Status + (_engine.Game.State.Window is { } game ? " · " + game.Title : "");
        var focus = _engine.Game.State;
        var showOverlay = _s.OverlayEnabled && (_s.OverlayInteractive || !_s.OverlayOnlyWhenGameFocused || string.IsNullOrEmpty(_s.GameWindowId) || focus.Available && focus.Focused);
        if (showOverlay)
        {
            _overlay ??= new OverlayWindow(_s, _engine);
            _overlay.Refresh(st, focus);
            if (!_overlay.IsVisible && _overlay.InputAvailable) _overlay.Show();
            _overlayStatus.Text = _overlay.InputStatus;
        }
        else _overlay?.Hide();
        var id = _engine.Osc.AvatarId;
        if (id != _lastAvatar)
        {
            _lastAvatar = id; _s.SwitchOsc(id); _lastParams = "";
            _oscTab.Content = Scroll(OscPanel());
        }
        _avatar.Text = _engine.Osc.AvatarName ?? id ?? "No avatar detected";
        _routing.Text = _engine.RoutingSummary;
        if (++_ticks % 10 != 0) return;
        _s.Save(); RefreshToys(); RefreshParameters(); _eventHistory.Text = string.Join(Environment.NewLine, _engine.Events.EventHistory.Reverse());
    }
    private void RefreshToys()
    {
        var list = _engine.Client.Toys;
        var signature = string.Join("|", list.Select(x => x.Id + x.Display));
        if (_toys.Items.Count > 0 && signature == _lastToys) return;
        _lastToys = signature;
        SetChoices(_toys, list.Select(x => (x.Id, x.Display)).ToList(), _s.ToyId, "All connected devices");
    }
    private void RefreshParameters()
    {
        var names = _engine.Osc.Seen.Keys.Concat(_s.OscParams).Distinct().Order().Take(500).ToArray();
        var signature = string.Join("|", names);
        if (signature == _lastParams) return;
        _lastParams = signature; _params.Children.Clear();
        foreach (var name in names)
            _params.Children.Add(Check(name, _s.OscParams.Contains(name), x =>
            {
                var set = _s.OscParams.ToHashSet(); if (x) set.Add(name); else set.Remove(name); _s.OscParams = set.ToArray();
            }));
    }
    private void OnLog(string line) => Dispatcher.UIThread.Post(() =>
    {
        var text = _log.Text ?? ""; if (text.Length > 24000) text = text[^16000..];
        _log.Text = text + line + Environment.NewLine;
    });
    private async Task ListenCommands()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync(_lifetime.Token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (command == "panic") _engine.Panic();
                    else if (command == "arm") ToggleArm();
                    else if (command == "overlay") _s.OverlayEnabled = !_s.OverlayEnabled;
                    else if (command == "overlay-interact") _s.OverlayInteractive = !_s.OverlayInteractive;
                    else { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Show(); Activate(); }
                });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Write("Shortcut listener: " + ex.Message); await Task.Delay(1000); }
        }
    }

    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private void PluginChanged(string id) => Dispatcher.UIThread.Post(() => { if (!_closing) { SyncGameTabs(); _plugins.Refresh(); } });
    private void SyncGameTabs()
    {
        var installed = _engine.Plugins.Installed;
        var selectedId = _gameTabs.FirstOrDefault(p => p.Value.Tab == _tabs.SelectedItem).Key;
        foreach (var id in _gameTabs.Keys.Where(id => !installed.Any(p => p.Profile.Id == id)).ToArray()) { _tabs.Items.Remove(_gameTabs[id].Tab); _gameTabs.Remove(id); }
        foreach (var plugin in installed)
        {
            var stamp = PluginManager.Serialize(plugin.Profile);
            if (_gameTabs.TryGetValue(plugin.Profile.Id, out var current) && current.Stamp == stamp) continue;
            if (current.Tab != null) _tabs.Items.Remove(current.Tab);
            var panel = new GamePluginPanel(_engine, plugin.Profile, _plugins.RemovePlugin);
            var tab = new TabItem { Header = PluginManager.TabTitle(plugin.Profile), Content = Scroll(panel), Tag = plugin.Profile.Id };
            ToolTip.SetTip(tab, plugin.Profile.Name);
            var position = _tabs.Items.Cast<TabItem>().ToList().FindIndex(t => t.Header?.ToString() == "Plugins");
            _tabs.Items.Insert(position, tab); _gameTabs[plugin.Profile.Id] = (tab, panel, stamp);
        }
        if (selectedId != null && _gameTabs.TryGetValue(selectedId, out var selected)) _tabs.SelectedItem = selected.Tab;
        if (_tabs.SelectedItem == null) _tabs.SelectedItem = _tabs.Items.Cast<TabItem>().First(t => t.Header?.ToString() == "Plugins");
    }
    private void Navigate(string page)
    {
        if (page.StartsWith("plugin:", StringComparison.Ordinal)) { SyncGameTabs(); if (_gameTabs.TryGetValue(page[7..], out var game)) _tabs.SelectedItem = game.Tab; return; }
        if (page == "Connection") { _host.BringIntoView(); _host.Focus(); return; }
        if (page == "Output") { _output.BringIntoView(); return; }
        _tabs.SelectedItem = _tabs.Items.Cast<TabItem>().FirstOrDefault(t => t.Header?.ToString() == page);
    }
    private static void SetChoices(ComboBox combo, List<(string Id, string Name)> items, string selected, string defaultName)
    {
        var choices = new List<Choice> { new("", defaultName) };
        choices.AddRange(items.Select(x => new Choice(x.Id, x.Name)));
        // Replace as one collection to avoid persisting the transient empty selection.
        if (combo.ItemsSource == null) combo.Items.Clear();
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices[0];
    }
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Classic.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Foreground = Classic.Muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 5) };
    private static StackPanel Panel() => new() { Spacing = 4 };
    private static Border Card(Control child) => new() { Child = child, Background = Classic.Panel, BorderBrush = Classic.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12) };
    private static ScrollViewer Scroll(Control content) => new()
    {
        // Padding belongs to the measured content so the last controls can scroll fully into view.
        Content = new Border { Child = content, Padding = new Thickness(10) },
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };
    private static Grid Columns(Control first, Control second, bool equal = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(equal ? "*,*" : "*,Auto"), ColumnSpacing = 6 };
        if (first is Button) first.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (second is Button) second.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(first); Grid.SetColumn(second, 1); row.Children.Add(second); return row;
    }
    private static StackPanel Row(params Control[] controls)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in controls) p.Children.Add(c); return p;
    }
    private static Button Button(string title, Func<Task> action)
    {
        var b = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { Log.Write(ex.Message); }
            finally { b.IsEnabled = true; }
        }; return b;
    }
    private static CheckBox Check(string title, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }, IsChecked = value };
        c.IsCheckedChanged += (_, _) => changed(c.IsChecked == true); return c;
    }
    private static void AddSlider(StackPanel panel, string label, double min, double max, double value, Action<double> changed, string format = "F2", double labelWidth = 140, double step = 0)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*,64"), MinHeight = 22 };
        row.Children.Add(Text(label));
        var amount = Text(value.ToString(format)); amount.FontFamily = Classic.Mono; amount.Foreground = Classic.Text; amount.HorizontalAlignment = HorizontalAlignment.Right;
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        if (step > 0) { slider.TickFrequency = step; slider.IsSnapToTickEnabled = true; }
        slider.ValueChanged += (_, _) => { amount.Text = slider.Value.ToString(format); changed(slider.Value); };
        Grid.SetColumn(slider, 1); row.Children.Add(slider); Grid.SetColumn(amount, 2); row.Children.Add(amount); panel.Children.Add(row);
    }
    private static void FieldRow(StackPanel p, string label, Control control, double labelWidth = 140)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*"), ColumnSpacing = 8, MinHeight = 29 };
        row.Children.Add(Text(label)); control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(control, 1); row.Children.Add(control); p.Children.Add(row);
    }
    private static void AddNumber(StackPanel p, string label, int min, int max, int value, Action<int> changed)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Increment = 1, FormatString = "F0" };
        n.ValueChanged += (_, _) => { if (n.Value is { } v) changed((int)v); }; FieldRow(p, label, n);
    }
    private static void AddField(StackPanel p, string label, string value, Action<string> changed)
    {
        var box = new TextBox { Text = value };
        box.TextChanged += (_, _) => changed(box.Text ?? ""); FieldRow(p, label, box);
    }
    private static void AddEnum<T>(StackPanel p, string label, T value, Action<T> changed, double labelWidth = 140) where T : struct, Enum
        => AddEnum(p, label, Enum.GetValues<T>(), value, changed, labelWidth);
    private static void AddEnum<T>(StackPanel p, string label, IEnumerable<T> values, T value, Action<T> changed, double labelWidth = 140)
    {
        var box = new ComboBox { ItemsSource = values, SelectedItem = value };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is T v) changed(v); }; FieldRow(p, label, box, labelWidth);
    }
}
