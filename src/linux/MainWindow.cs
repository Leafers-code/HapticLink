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
    private readonly ProgressBar[] _meters = new ProgressBar[5];
    private EqualizerPanel _equalizer;
    private OverlayWindow _overlay;
    private readonly TextBlock _eventEndpoint = Text(""), _eventHistory = Text(""), _gameStatus = Text("No game selected"), _overlayStatus = Text("");
    private readonly ProgressBar _outputMeter = new() { Minimum = 0, Maximum = 100, Height = 10 };
    private readonly Button _arm = new() { Content = "ARM OUTPUT", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 380 };
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
        Title = "HapticLink · " + (OperatingSystem.IsWindows() ? "Windows" : "Linux") + " 1.2 preview"; Width = 1280; Height = 860; MinWidth = 1100; MinHeight = 620;
        Background = new SolidColorBrush(Color.Parse("#11151C"));
        _engine = new Engine(_s);
        _host = new TextBox { Text = _s.Host, Watermark = "192.168.1.42" };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("290,*"), Margin = new Thickness(24) };
        var left = Panel(); left.Margin = new Thickness(0, 0, 22, 0);
        left.Children.Add(new TextBlock { Text = "HapticLink", FontSize = 29, FontWeight = FontWeight.Bold });
        left.Children.Add(Text("Game signals, connected."));
        left.Children.Add(Heading("CONNECTION")); left.Children.Add(_host);
        left.Children.Add(Row(Button("Connect", Connect), Button("Scan LAN", Scan)));
        left.Children.Add(_connection); left.Children.Add(_toys);
        _toys.SelectionChanged += (_, _) => { if (_toys.SelectedItem is Choice c) _s.ToyId = c.Id; };
        left.Children.Add(Button("Refresh devices", async () => { await _engine.Client.RefreshToysAsync(); RefreshToys(); }));
        left.Children.Add(Heading("OUTPUT"));
        _output.FontSize = 44; _output.FontWeight = FontWeight.Bold;
        left.Children.Add(_output); left.Children.Add(_outputMeter); left.Children.Add(_state);
        _arm.Click += (_, _) => ToggleArm(); left.Children.Add(_arm);
        var panic = Button("PANIC — STOP", () => { _engine.Panic(); return Task.CompletedTask; });
        panic.Background = new SolidColorBrush(Color.Parse("#B83247"));
        left.Children.Add(panic);
        left.Children.Add(Button("Test 25% · 2 seconds", () => { _engine.Test(.25, 2); return Task.CompletedTask; }));
        AddSlider(left, "Master cap", 0, 1, _s.MasterCap, x => _s.MasterCap = x, "P0");
        AddEnum(left, "Mix mode", _s.Mix, x => _s.Mix = x);
        left.Children.Add(Text("Global stop: bind Ctrl+Alt+X in your desktop settings to HapticLink --panic."));
        root.Children.Add(new ScrollViewer { Content = left, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var tabs = new TabControl { Margin = new Thickness(0, 0, 0, 0), FontSize = 16 };
        tabs.Items.Add(new TabItem { Header = "Manual", Content = Scroll(ManualPanel()) });
        tabs.Items.Add(new TabItem { Header = "Audio", Content = Scroll(AudioPanel()) });
        tabs.Items.Add(new TabItem { Header = "Controller", Content = Scroll(RumblePanel()) });
        tabs.Items.Add(new TabItem { Header = "Events", Content = Scroll(EventsPanel()) });
        tabs.Items.Add(new TabItem { Header = "Overlay", Content = Scroll(OverlayPanel()) });
        _oscTab.Content = Scroll(OscPanel()); tabs.Items.Add(_oscTab);
        tabs.Items.Add(new TabItem { Header = "Settings", Content = Scroll(SettingsPanel()) });
        tabs.Items.Add(new TabItem { Header = "Log", Content = Scroll(_log) });
        Grid.SetColumn(tabs, 1); root.Children.Add(tabs); Content = root;
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
            _closing = true; _timer.Stop(); _lifetime.Cancel(); _overlay?.Close(); _engine.Panic(); _s.Save();
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
        var p = Panel(); p.Children.Add(Heading(title)); p.Children.Add(Text(description));
        p.Children.Add(Check("Enable source", _engine.SettingsFor(index).Enabled, x => _engine.SetSourceEnabled(index, x)));
        _meters[index] = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8 };
        _status[index] = Text("Idle"); p.Children.Add(_meters[index]); p.Children.Add(_status[index]);
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
        var p = SourcePanel(1, "GAME AUDIO", "Capture your output monitor through PipeWire or PulseAudio.");
        if (OperatingSystem.IsWindows()) p.Children.Add(Text("Windows uses WASAPI output-device capture. The EQ, overlay and game-focus pause work here; per-application audio isolation currently requires Linux."));
        var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        choices.Items.Add(new Choice("", "Default output monitor"));
        if (_s.AudioDeviceId.Length > 0) choices.Items.Add(new Choice(_s.AudioDeviceId, _s.AudioDeviceId));
        choices.SelectedIndex = choices.Items.Count - 1;
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is Choice c) _s.AudioDeviceId = c.Id; };
        p.Children.Add(choices);
        var modeLabels = new[] { "Chosen output device", "One application", "Selected game window" };
        AddEnum(p, "Capture", modeLabels, modeLabels[(int)_s.AudioCaptureMode], x => _s.AudioCaptureMode = (AudioCaptureMode)Array.IndexOf(modeLabels, x));
        var apps = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        apps.SelectionChanged += (_, _) => { if (apps.SelectedItem is AudioApplication a) { _s.AudioProcessId = a.ProcessId; _s.AudioStreamIndex = a.Stream; } };
        p.Children.Add(apps);
        p.Children.Add(Button("Refresh playing applications", async () =>
        {
            var applications = await Task.Run(AudioRouting.Applications);
            var selected = _s.AudioProcessId;
            apps.ItemsSource = applications;
            apps.SelectedItem = applications.FirstOrDefault(a => a.ProcessId == selected);
        }));
        p.Children.Add(Text("Application captures only that app's selected playback stream. SelectedGame follows the game window picked on the Overlay tab. If its audio disappears, capture waits; it does not switch to all desktop audio."));
        p.Children.Add(Button("Refresh output monitors", async () =>
        {
            var devices = await Task.Run(AudioSource.ListDevices);
            SetChoices(choices, devices, _s.AudioDeviceId, "Default output monitor");
        }));
        p.Children.Add(Button("Apply / restart audio", () => { _engine.RestartSource(1); return Task.CompletedTask; }));
        AddEnum(p, "Band", _s.AudioBand, x => _s.AudioBand = x);
        AddSlider(p, "Quiet floor (dB)", -90, -1, _s.AudioFloorDb, x => _s.AudioFloorDb = x, "F0");
        AddSlider(p, "Loud ceiling (dB)", -60, 0, _s.AudioCeilDb, x => _s.AudioCeilDb = x, "F0");
        _equalizer = new EqualizerPanel(_s, _engine.Audio); p.Children.Add(_equalizer);
        Shape(p, _s.Audio); return p;
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
        p.Children.Add(Check("Pause all output when the selected game loses focus", _s.PauseWhenGameUnfocused, x => _s.PauseWhenGameUnfocused = x));
        p.Children.Add(Check("Show overlay", _s.OverlayEnabled, x => _s.OverlayEnabled = x));
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
        var p = SourcePanel(0, "CONTROLLER RUMBLE", "Mirrors a physical gamepad through a Linux virtual gamepad and reads the game's rumble.");
        var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        choices.Items.Add(new Choice(_s.RumbleDevice, _s.RumbleDevice.Length == 0 ? "First accessible gamepad" : _s.RumbleDevice));
        choices.SelectedIndex = 0;
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is Choice c) _s.RumbleDevice = c.Id; };
        p.Children.Add(choices);
        p.Children.Add(Button("Refresh gamepads", async () =>
        {
            var devices = await Task.Run(() => RumbleSource.ListDevices(_s));
            SetChoices(choices, devices, _s.RumbleDevice, "First accessible gamepad");
        }));
        p.Children.Add(Check("Forward controller input", _s.RumblePassthrough, x => _s.RumblePassthrough = x));
        p.Children.Add(Check("Grab physical gamepad exclusively", _s.RumbleGrab, x => _s.RumbleGrab = x));
        p.Children.Add(Button("Apply / restart controller", () => { _engine.RestartSource(0); return Task.CompletedTask; }));
        p.Children.Add(Text("Requires python3-evdev, /dev/uinput access, and access to the selected gamepad. Select HapticLink in Steam's controller order before launching the game. See README-Linux.md for permissions and Steam Input details."));
        if (OperatingSystem.IsWindows()) p.Children.Add(Text("Windows uses the recovered ViGEm/XInput bridge. Install ViGEmBus if needed and use HidHide to hide the physical pad from games. The exclusive-grab checkbox above is Linux-only."));
        AddSlider(p, "Heavy motor weight", 0, 2, _s.RumbleLargeWeight, x => _s.RumbleLargeWeight = x);
        AddSlider(p, "Light motor weight", 0, 2, _s.RumbleSmallWeight, x => _s.RumbleSmallWeight = x);
        Shape(p, _s.Rumble); return p;
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
        AddNumber(p, "Update rate (Hz)", 1, 20, _s.UpdateHz, x => _s.UpdateHz = x);
        AddNumber(p, "Auto-stop minutes (0 = off)", 0, 240, _s.MaxContinuousMinutes, x => _s.MaxContinuousMinutes = x);
        AddNumber(p, "Command expiry (seconds)", 1, 10, _s.DeadManSeconds, x => _s.DeadManSeconds = x);
        AddEnum(p, "Device function", new[] { "Vibrate", "Rotate", "Pump", "Thrusting", "Suction", "Oscillate", "All" }, _s.Action, x => _s.Action = x);
        AddNumber(p, "Phone HTTP port", 1, 65535, _s.HttpPort, x => _s.HttpPort = x);
        AddNumber(p, "Phone HTTPS port", 1, 65535, _s.HttpsPort, x => _s.HttpsPort = x);
        p.Children.Add(Check("Prefer HTTPS", _s.PreferHttps, x => _s.PreferHttps = x));
        AddField(p, "Python executable", _s.PythonCommand, x => _s.PythonCommand = x);
        p.Children.Add(Button("Save settings", () => { _s.Save(); return Task.CompletedTask; }));
        p.Children.Add(Text("Config and log: " + Settings.Dir));
        p.Children.Add(Text("Copy a Windows config.json into this folder while HapticLink is closed. Linux audio and gamepad selections must be chosen again. The app always starts disarmed. Global shortcuts are configured in your desktop; --panic also works on Wayland."));
        return p;
    }

    private void Refresh()
    {
        if (_latest is not { } st) return;
        _output.Text = st.Output.ToString("P0"); _outputMeter.Value = st.Output * 100;
        _state.Text = st.Panicked ? "PANIC STOPPED" : st.FocusPaused ? "PAUSED · GAME UNFOCUSED" : st.SafetyTripped ? "AUTO-STOP REACHED" : st.Armed ? "ARMED" : "DISARMED";
        _arm.Content = st.Armed ? "DISARM OUTPUT" : "ARM OUTPUT";
        for (int i = 0; i < 5; i++) { _meters[i].Value = st.Shaped[i] * 100; _status[i].Text = st.SourceStatus[i]; }
        _equalizer.Refresh();
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
                    else { Show(); Activate(); }
                });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Write("Shortcut listener: " + ex.Message); await Task.Delay(1000); }
        }
    }

    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private static void SetChoices(ComboBox combo, List<(string Id, string Name)> items, string selected, string defaultName)
    {
        var choices = new List<Choice> { new("", defaultName) };
        choices.AddRange(items.Select(x => new Choice(x.Id, x.Name)));
        // Replace as one collection to avoid persisting the transient empty selection.
        if (combo.ItemsSource == null) combo.Items.Clear();
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices[0];
    }
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.Bold, FontSize = 15, Margin = new Thickness(0, 16, 0, 4) };
    private static StackPanel Panel() => new() { Spacing = 10 };
    private static ScrollViewer Scroll(Control content) => new() { Content = content, Padding = new Thickness(16), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private static StackPanel Row(params Control[] controls)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in controls) p.Children.Add(c); return p;
    }
    private static Button Button(string title, Func<Task> action)
    {
        var b = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Stretch };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { Log.Write(ex.Message); }
            finally { b.IsEnabled = true; }
        }; return b;
    }
    private static CheckBox Check(string title, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = Text(title), IsChecked = value };
        c.IsCheckedChanged += (_, _) => changed(c.IsChecked == true); return c;
    }
    private static void AddSlider(StackPanel panel, string label, double min, double max, double value, Action<double> changed, string format = "F2")
    {
        var caption = Text(label + " · " + value.ToString(format));
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };
        slider.ValueChanged += (_, _) => { caption.Text = label + " · " + slider.Value.ToString(format); changed(slider.Value); };
        panel.Children.Add(caption); panel.Children.Add(slider);
    }
    private static void AddNumber(StackPanel p, string label, int min, int max, int value, Action<int> changed)
    {
        p.Children.Add(Text(label));
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Increment = 1, FormatString = "F0" };
        n.ValueChanged += (_, _) => { if (n.Value is { } v) changed((int)v); }; p.Children.Add(n);
    }
    private static void AddField(StackPanel p, string label, string value, Action<string> changed)
    {
        p.Children.Add(Text(label)); var box = new TextBox { Text = value };
        box.TextChanged += (_, _) => changed(box.Text ?? ""); p.Children.Add(box);
    }
    private static void AddEnum<T>(StackPanel p, string label, T value, Action<T> changed) where T : struct, Enum
        => AddEnum(p, label, Enum.GetValues<T>(), value, changed);
    private static void AddEnum<T>(StackPanel p, string label, IEnumerable<T> values, T value, Action<T> changed)
    {
        p.Children.Add(Text(label)); var box = new ComboBox { ItemsSource = values, SelectedItem = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is T v) changed(v); }; p.Children.Add(box);
    }
}
