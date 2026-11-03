using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HapticLink.Core;
using HapticLink.Sources;

namespace HapticLink
{
    public partial class MainWindow : Window
    {
        private readonly Settings _s;
        private readonly Engine _engine;
        private bool _ready;                 // guards handlers while populating the UI
        private bool _oscListRebuilding;
        private int _oscSeenCount = -1;
        private DateTime _lastOscListRefresh = DateTime.MinValue;

        // meter plumbing, indexed like Engine.Idx*
        private RectangleGeometry[] _clips;
        private FrameworkElement[] _hosts;
        private TextBlock[] _vals;
        private Ellipse[] _dots;
        private TextBlock[] _statuses;
        private CheckBox[] _toggles;

        public MainWindow()
        {
            InitializeComponent();

            _s = Settings.Load();
            _engine = new Engine(_s);

            _clips = new[] { M0Clip, M1Clip, M2Clip, M3Clip };
            _hosts = new FrameworkElement[] { M0Host, M1Host, M2Host, M3Host };
            _vals = new[] { M0Val, M1Val, M2Val, M3Val };
            _dots = new[] { M0Dot, M1Dot, M2Dot, M3Dot };
            _statuses = new[] { RumbleStatus, AudioStatus, OscStatus, ManualStatus };
            _toggles = new[] { RumbleOn, AudioOn, OscOn, ManualOn };

            Log.Line += OnLogLine;
            _engine.Updated += OnEngineUpdated;

            BuildShapers();
            PopulateAudioDevices();
            LoadUi();
            _ready = true;

            _engine.Start();
            Log.Write("HapticLink ready. Config: " + Settings.FilePath);

            // Reconnect to the last known host so the app is usable straight from launch.
            Loaded += async (_, __) =>
            {
                if (!string.IsNullOrWhiteSpace(_s.Host) && !_engine.Client.IsConnected)
                    await DoConnectAsync();
            };

            // Settings otherwise only land on clean exit; this keeps them across a crash or kill.
            _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _autosave.Tick += (_, __) =>
            {
                _s.Host = HostBox.Text.Trim();
                _s.Save();
            };
            _autosave.Start();
        }

        private DispatcherTimer _autosave;

        // ---------------------------------------------------------------- UI build

        private void BuildShapers()
        {
            RumbleShaper.Child = ShaperPanel(_s.Rumble);
            AudioShaper.Child = ShaperPanel(_s.Audio);
            OscShaper.Child = ShaperPanel(_s.Osc);
            ManualShaper.Child = ShaperPanel(_s.Manual);
            OscFeel.Child = OscFeelPanel();
        }

        private TextBlock _oscRouting;

        /// <summary>How touch is turned into strength, and which parts reach which toy. Shared by every avatar, unlike the shaping below it.</summary>
        private FrameworkElement OscFeelPanel()
        {
            Func<double, string> pct = v => (v * 100).ToString("0") + "%";
            // Shown as a sensitivity so that right means "more", stored as the speed that reaches full strength.
            const double slowest = 8, fastest = 1;

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "FEEL", Style = (Style)FindResource("Header") });
            sp.Children.Add(SliderRow("Follow motion", 0, 1, _s.OscMotion, v => _s.OscMotion = v, pct));
            sp.Children.Add(SliderRow("Motion sensitivity", 0, 1,
                (slowest - _s.OscMotionFullSpeed) / (slowest - fastest),
                v => _s.OscMotionFullSpeed = slowest - v * (slowest - fastest), pct));
            sp.Children.Add(SliderRow("Held-still level", 0, 1, _s.OscIdle, v => _s.OscIdle = v, pct));
            sp.Children.Add(SliderRow("Touch strength", 0, 1, _s.OscTouchWeight, v => _s.OscTouchWeight = v, pct));
            sp.Children.Add(Note("Follow motion makes each stroke a swell and lets stillness settle to the held-still level, " +
                                 "instead of buzzing flat out whenever contact is deep. 0% is the old behaviour. " +
                                 "Touch strength is how far a hand can push it compared with full contact."));

            sp.Children.Add(CheckRow("Build up over time", _s.OscBuildUp, v => _s.OscBuildUp = v, new Thickness(0, 10, 0, 4)));
            sp.Children.Add(SliderRow("Starts at", 0.2, 1, _s.OscBuildStart, v => _s.OscBuildStart = v, pct));
            sp.Children.Add(SliderRow("Time to full", 1, 15, _s.OscBuildMinutes, v => _s.OscBuildMinutes = v, v => v.ToString("0") + " min"));
            sp.Children.Add(Note("Strength starts lower and climbs while contact keeps going — faster when it is intense — " +
                                 "then eases back down when you are left alone. The status line shows where it is."));

            sp.Children.Add(new TextBlock { Text = "PARTS → TOYS", Style = (Style)FindResource("Header"), Margin = new Thickness(0, 14, 0, 6) });
            sp.Children.Add(CheckRow("Pick which body parts drive each toy automatically", _s.OscAutoParts,
                v => _s.OscAutoParts = v, new Thickness(0, 0, 0, 4)));
            _oscRouting = new TextBlock
            {
                Style = (Style)FindResource("Lbl"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("Good"),
                Margin = new Thickness(20, 0, 0, 8)
            };
            AutomationProperties.SetAutomationId(_oscRouting, "OscRouting");
            sp.Children.Add(_oscRouting);
            sp.Children.Add(SliderRow("Other parts", 0, 1, _s.OscSympathy, v => _s.OscSympathy = v, pct));
            sp.Children.Add(CheckRow("Two-motor toys: move from the entrance motor to the deep one with depth", _s.OscTravel,
                v => _s.OscTravel = v, new Thickness(0, 6, 0, 2)));
            sp.Children.Add(CheckRow("Swap the Edge motors (tick if that travel feels backwards)", _s.OscEdgeSwap,
                v => _s.OscEdgeSwap = v, new Thickness(20, 0, 0, 4)));
            sp.Children.Add(Note("Each toy follows the parts that sit where it is worn — a plug follows the rear of the avatar, a stroker its " +
                                 "penis — and everything else reaches it at the Other parts level. If the avatar has nothing in the area a toy " +
                                 "belongs to, its main parts are used instead, so the toy is never left with only scraps."));
            return sp;
        }

        private TextBlock Note(string text) => new TextBlock
        {
            Style = (Style)FindResource("Lbl"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Text = text
        };

        private CheckBox CheckRow(string text, bool value, Action<bool> setter, Thickness margin)
        {
            var cb = new CheckBox { Content = text, IsChecked = value, Margin = margin };
            cb.Click += (_, _) => setter(cb.IsChecked == true);
            return cb;
        }

        private FrameworkElement ShaperPanel(SourceSettings cfg)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = "SHAPING",
                Style = (Style)FindResource("Header")
            });
            sp.Children.Add(SliderRow("Gain", 0, 2, cfg.Gain, v => cfg.Gain = v, v => v.ToString("0.00")));
            sp.Children.Add(SliderRow("Curve", 0.3, 3, cfg.Curve, v => cfg.Curve = v, v => v.ToString("0.00")));
            sp.Children.Add(SliderRow("Deadzone", 0, 0.5, cfg.Deadzone, v => cfg.Deadzone = v, v => (v * 100).ToString("0") + "%"));
            sp.Children.Add(SliderRow("Attack", 0, 500, cfg.AttackMs, v => cfg.AttackMs = v, v => v.ToString("0") + " ms"));
            sp.Children.Add(SliderRow("Release", 0, 2000, cfg.ReleaseMs, v => cfg.ReleaseMs = v, v => v.ToString("0") + " ms"));
            return sp;
        }

        private UIElement SliderRow(string label, double min, double max, double value,
                                    Action<double> setter, Func<double, string> fmt)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

            var lbl = new TextBlock { Text = label, Style = (Style)FindResource("Lbl") };
            var sld = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                VerticalAlignment = VerticalAlignment.Center,
                IsMoveToPointEnabled = true
            };
            var val = new TextBlock { Text = fmt(value), Style = (Style)FindResource("Val") };

            sld.ValueChanged += (_, e) =>
            {
                setter(e.NewValue);
                val.Text = fmt(e.NewValue);
            };

            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(sld, 1);
            Grid.SetColumn(val, 2);
            g.Children.Add(lbl);
            g.Children.Add(sld);
            g.Children.Add(val);
            return g;
        }

        private void PopulateAudioDevices()
        {
            AudioDevice.Items.Clear();
            AudioDevice.Items.Add(new ComboBoxItem { Content = "Default output", Tag = "" });
            foreach (var (id, name) in AudioSource.ListDevices())
                AudioDevice.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            AudioDevice.SelectedIndex = 0;
        }

        private void LoadUi()
        {
            HostBox.Text = _s.Host;
            SelectByContent(ActionCombo, _s.Action);
            SelectByContent(MixCombo, _s.Mix.ToString());
            SelectByContent(AudioBandCombo, _s.AudioBand.ToString());
            SelectByContent(PatternCombo, _s.Pattern.ToString());

            CapSlider.Value = _s.MasterCap;
            RateSlider.Value = Math.Clamp(_s.UpdateHz, 2, 20);
            SafetySlider.Value = Math.Clamp(_s.MaxContinuousMinutes, 0, 120);
            ManualLevel.Value = _s.ManualLevel;
            PatternPeriod.Value = Math.Clamp(_s.PatternPeriodSec, 0.2, 20);
            PatternDepth.Value = _s.PatternDepth;

            RumblePass.IsChecked = _s.RumblePassthrough;
            RumbleLarge.Value = Math.Clamp(_s.RumbleLargeWeight, 0, 2);
            RumbleSmall.Value = Math.Clamp(_s.RumbleSmallWeight, 0, 2);
            RumblePoll.Value = Math.Clamp(_s.RumblePollHz, 60, 1000);

            AudioFloor.Value = Math.Clamp(_s.AudioFloorDb, -80, -20);
            AudioCeil.Value = Math.Clamp(_s.AudioCeilDb, -40, 0);
            foreach (ComboBoxItem it in AudioDevice.Items)
                if ((string)it.Tag == _s.AudioDeviceId) { AudioDevice.SelectedItem = it; break; }

            OscPort.Text = _s.OscPort.ToString();
            OscAuto.IsChecked = _s.OscAutoSensors;
            OscSelf.IsChecked = _s.OscAutoSelf;
            OscSelf.IsEnabled = _s.OscAutoSensors;

            RumbleOn.IsChecked = _s.Rumble.Enabled;
            AudioOn.IsChecked = _s.Audio.Enabled;
            OscOn.IsChecked = _s.Osc.Enabled;
            ManualOn.IsChecked = _s.Manual.Enabled;

            UpdateStaticLabels();
            UpdateArmButton();
        }

        private void UpdateStaticLabels()
        {
            CapVal.Text = (_s.MasterCap * 100).ToString("0") + "%";
            RateVal.Text = _s.UpdateHz + " Hz";
            SafetyVal.Text = _s.MaxContinuousMinutes == 0 ? "off" : _s.MaxContinuousMinutes + " m";
            ManualLevelVal.Text = (_s.ManualLevel * 100).ToString("0") + "%";
            PatternPeriodVal.Text = _s.PatternPeriodSec.ToString("0.0") + " s";
            PatternDepthVal.Text = (_s.PatternDepth * 100).ToString("0") + "%";
            RumbleLargeVal.Text = _s.RumbleLargeWeight.ToString("0.00");
            RumbleSmallVal.Text = _s.RumbleSmallWeight.ToString("0.00");
            RumblePollVal.Text = _s.RumblePollHz + " Hz";
            AudioFloorVal.Text = _s.AudioFloorDb.ToString("0") + " dB";
            AudioCeilVal.Text = _s.AudioCeilDb.ToString("0") + " dB";
        }

        private static void SelectByContent(ComboBox cb, string content)
        {
            foreach (ComboBoxItem it in cb.Items)
                if (string.Equals(it.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
                {
                    cb.SelectedItem = it;
                    return;
                }
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        private static string SelectedContent(ComboBox cb) =>
            (cb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        // ---------------------------------------------------------------- live update

        private void OnEngineUpdated(EngineState st)
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyState(st)));
        }

        private void ApplyState(EngineState st)
        {
            if (!IsLoaded) return;

            SetMeter(OutMeterClip, OutMeterHost, st.Output);
            OutPercent.Text = (st.Output * 100).ToString("0") + "%";
            OutLevel.Text = st.OutputLevel20 + " / 20";

            for (int i = 0; i < 4; i++)
            {
                SetMeter(_clips[i], _hosts[i], st.Shaped[i]);
                _vals[i].Text = (st.Shaped[i] * 100).ToString("0") + "%";
                _statuses[i].Text = st.SourceStatus[i] ?? "";
                _dots[i].Fill = !_engine.SettingsFor(i).Enabled
                    ? (Brush)FindResource("Line")
                    : st.SourceAvailable[i] && st.SourceRunning[i]
                        ? (Brush)FindResource("Good")
                        : (Brush)FindResource("Danger");
            }

            var connected = st.Connected;
            ConnPillText.Text = connected ? "CONNECTED" : "DISCONNECTED";
            ConnPillText.Foreground = (Brush)FindResource(connected ? "Good" : "Danger");
            ConnPill.BorderBrush = (Brush)FindResource(connected ? "Good" : "Danger");
            ConnPill.Background = new SolidColorBrush(connected
                ? Color.FromRgb(0x16, 0x2A, 0x1D)
                : Color.FromRgb(0x2A, 0x1D, 0x1D));

            SafetyPill.Visibility = st.SafetyTripped ? Visibility.Visible : Visibility.Collapsed;
            if (st.SafetyTripped)
                SafetyPillText.Text = $"AUTO-STOPPED AFTER {_s.MaxContinuousMinutes} MIN";

            UpdateArmButton();
            UpdateGraph();
            MaybeSwitchOscProfile();
            MaybeRefreshOscList();
            if (_oscRouting != null && _oscRouting.Text != _engine.RoutingSummary) _oscRouting.Text = _engine.RoutingSummary;
        }

        private static void SetMeter(RectangleGeometry clip, FrameworkElement host, double v)
        {
            var w = host.ActualWidth;
            var h = host.ActualHeight;
            if (w <= 0 || h <= 0) return;
            clip.Rect = new Rect(0, 0, w * Math.Clamp(v, 0, 1), h);
        }

        private void UpdateGraph()
        {
            var w = GraphCanvas.ActualWidth;
            var h = GraphCanvas.ActualHeight;
            if (w <= 1 || h <= 1) return;

            var hist = _engine.OutputHistory();
            var pts = new PointCollection(hist.Length);
            for (int i = 0; i < hist.Length; i++)
                pts.Add(new Point(i * w / (hist.Length - 1.0), h - Math.Clamp(hist[i], 0, 1) * (h - 2) - 1));
            GraphLine.Points = pts;
        }

        private void UpdateArmButton()
        {
            if (_engine.Panicked)
            {
                ArmBtn.Content = "CLEAR PANIC";
                ArmBtn.BorderBrush = (Brush)FindResource("Warn");
            }
            else if (_engine.Armed)
            {
                ArmBtn.Content = "ARMED — CLICK TO STOP";
                ArmBtn.BorderBrush = (Brush)FindResource("Good");
            }
            else
            {
                ArmBtn.Content = "ARM OUTPUT";
                ArmBtn.BorderBrush = (Brush)FindResource("Line");
            }
        }

        private void OnLogLine(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                if (LogBox.Text.Length > 40000)
                    LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - 20000);
                LogBox.ScrollToEnd();
            }));
        }

        // ---------------------------------------------------------------- connection

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            _s.Host = HostBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_s.Host))
            {
                Log.Write("Enter the IP shown in Lovense Remote > Discover > Game Mode, or hit Scan LAN.");
                return;
            }
            await DoConnectAsync();
        }

        private async Task DoConnectAsync()
        {
            ConnectBtn.IsEnabled = false;
            ConnectBtn.Content = "Connecting...";
            try
            {
                Log.Write($"Connecting to {_s.Host} ...");
                var ok = await _engine.Client.ConnectAsync(_s);
                if (ok) PopulateToys();
                else ToyInfo.Text = "Connection failed. Check the log.";
            }
            finally
            {
                ConnectBtn.IsEnabled = true;
                ConnectBtn.Content = "Connect";
                _s.Save();
            }
        }

        private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            _engine.Arm(false);
            _engine.Client.Disconnect();
            ToyCombo.Items.Clear();
            ToyInfo.Text = "Disconnected.";
            Log.Write("Disconnected.");
        }

        private async void ScanBtn_Click(object sender, RoutedEventArgs e)
        {
            ScanBtn.IsEnabled = false;
            ScanBtn.Content = "Scanning…";
            try
            {
                Log.Write("Scanning the local network for Game Mode …");
                var hosts = await Task.Run(() =>
                    Toy.LovenseClient.DiscoverAsync(_s.HttpPort, _s.HttpsPort));
                if (hosts.Count > 0)
                {
                    HostBox.Text = hosts[0];
                    _s.Host = hosts[0];
                    Log.Write($"Using {hosts[0]}. Click Connect.");
                    if (hosts.Count > 1)
                        Log.Write("Other candidates: " + string.Join(", ", hosts.Skip(1)));
                }
            }
            finally
            {
                ScanBtn.IsEnabled = true;
                ScanBtn.Content = "Scan LAN";
            }
        }

        private void PopulateToys()
        {
            _oscListRebuilding = true;
            ToyCombo.Items.Clear();
            ToyCombo.Items.Add(new ComboBoxItem { Content = "All toys", Tag = "" });
            foreach (var t in _engine.Client.Toys)
                ToyCombo.Items.Add(new ComboBoxItem { Content = t.Display, Tag = t.Id });

            var idx = 0;
            for (int i = 0; i < ToyCombo.Items.Count; i++)
                if ((string)((ComboBoxItem)ToyCombo.Items[i]).Tag == _s.ToyId) { idx = i; break; }
            ToyCombo.SelectedIndex = idx;
            _oscListRebuilding = false;

            ToyInfo.Text = _engine.Client.Toys.Count == 0
                ? "Connected, but no toys reported. Is the toy paired and switched on?"
                : string.Join("\n", _engine.Client.Toys.Select(t => "• " + t.Display));
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            if (await _engine.Client.RefreshToysAsync()) PopulateToys();
        }

        private void ToyCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _oscListRebuilding) return;
            _s.ToyId = (ToyCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        }

        private void ActionCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            _s.Action = SelectedContent(ActionCombo);
        }

        private void TestBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_engine.Client.IsConnected)
            {
                Log.Write("Not connected — nothing to test.");
                return;
            }
            _engine.Test(0.25, 2.0);
            Log.Write("Test pulse: 25% for 2s.");
        }

        // ---------------------------------------------------------------- arm / panic

        private void ArmBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.Panicked) { _engine.ClearPanic(); UpdateArmButton(); return; }
            if (!_engine.Armed && !_engine.Client.IsConnected)
            {
                Log.Write("Connect to the toy before arming.");
                return;
            }
            _engine.Arm(!_engine.Armed);
            UpdateArmButton();
        }

        private void PanicBtn_Click(object sender, RoutedEventArgs e)
        {
            _engine.Panic();
            _ready = false;
            ManualLevel.Value = 0;
            _ready = true;
            UpdateArmButton();
        }

        // ---------------------------------------------------------------- sources

        private void SourceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            var cb = (CheckBox)sender;
            var idx = int.Parse((string)cb.Tag);
            _engine.SetSourceEnabled(idx, cb.IsChecked == true);
        }

        private void RumblePass_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _s.RumblePassthrough = RumblePass.IsChecked == true;
        }

        private void RumbleWeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _s.RumbleLargeWeight = RumbleLarge.Value;
            _s.RumbleSmallWeight = RumbleSmall.Value;
            RumbleLargeVal.Text = _s.RumbleLargeWeight.ToString("0.00");
            RumbleSmallVal.Text = _s.RumbleSmallWeight.ToString("0.00");
        }

        private void RumblePoll_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _s.RumblePollHz = (int)RumblePoll.Value;
            RumblePollVal.Text = _s.RumblePollHz + " Hz";
        }

        private void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            _s.AudioDeviceId = (AudioDevice.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            _engine.RestartSource(Engine.IdxAudio);
        }

        private void AudioBand_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (Enum.TryParse<AudioBand>(SelectedContent(AudioBandCombo), out var b)) _s.AudioBand = b;
        }

        private void AudioDb_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _s.AudioFloorDb = AudioFloor.Value;
            _s.AudioCeilDb = AudioCeil.Value;
            AudioFloorVal.Text = _s.AudioFloorDb.ToString("0") + " dB";
            AudioCeilVal.Text = _s.AudioCeilDb.ToString("0") + " dB";
        }

        private void OscApplyPort_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(OscPort.Text.Trim(), out var p) && p > 0 && p < 65536)
            {
                _s.OscPort = p;
                _engine.RestartSource(Engine.IdxOsc);
                Log.Write($"OSC port set to {p}.");
            }
            else Log.Write("Invalid OSC port.");
        }

        // ---- per-avatar profiles ----

        private string _oscUiAvatarName;

        /// <summary>
        /// Follows the avatar VRChat reports: what was tuned goes back to the profile it came from,
        /// the new avatar's profile (or the shared one) becomes live, and the tab is repainted.
        /// </summary>
        private void MaybeSwitchOscProfile()
        {
            var id = _engine.Osc.AvatarId;
            var name = _engine.Osc.AvatarName;
            var switched = id != _s.OscLiveAvatarId;
            if (!switched && name == _oscUiAvatarName) return;

            if (switched)
            {
                _s.SwitchOsc(id);
                RefreshOscControls();
                if (_s.OscLiveIsOwnProfile)
                    Log.Write("OSC: loaded the settings saved for " + (name ?? _s.OscAvatars[id].Name));
            }

            _oscUiAvatarName = name;
            if (name != null && _s.OscLiveIsOwnProfile) _s.OscAvatars[id].Name = name;
            UpdateOscProfileInfo();
        }

        /// <summary>Repaint every control on the tab that a profile covers, after the live copy changed under them.</summary>
        private void RefreshOscControls()
        {
            OscAuto.IsChecked = _s.OscAutoSensors;
            OscSelf.IsChecked = _s.OscAutoSelf;
            OscSelf.IsEnabled = _s.OscAutoSensors;
            OscShaper.Child = ShaperPanel(_s.Osc);
            _oscSeenCount = -1; // re-tick the list from the OscParams just loaded
        }

        private void UpdateOscProfileInfo()
        {
            var id = _s.OscLiveAvatarId;
            var own = _s.OscLiveIsOwnProfile;
            OscRemember.IsEnabled = id != null;
            OscRemember.IsChecked = own;

            var who = _engine.Osc.AvatarName ?? (own ? _s.OscAvatars[id].Name : null);
            if (string.IsNullOrEmpty(who)) who = "this avatar";
            var others = _s.OscAvatars.Count - (own ? 1 : 0);
            var tail = others > 0 ? $" ({others} other avatar{(others == 1 ? "" : "s")} remembered.)" : "";

            OscProfileInfo.Text = id == null
                ? "Waiting to learn which avatar you're wearing."
                : own
                    ? $"Everything below is saved for {who} and comes back whenever you wear it.{tail}"
                    : $"{who} is using the shared settings, like every avatar without its own.{tail}";
        }

        private void OscRemember_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (OscRemember.IsChecked == true)
                _s.RememberOscAvatar(_engine.Osc.AvatarName);
            else
            {
                _s.ForgetOscAvatar();
                RefreshOscControls();
            }
            UpdateOscProfileInfo();
            _s.Save();
        }

        private void OscAuto_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _s.OscAutoSensors = OscAuto.IsChecked == true;
            _s.OscAutoSelf = OscSelf.IsChecked == true;
            OscSelf.IsEnabled = _s.OscAutoSensors;
        }

        private void OscClear_Click(object sender, RoutedEventArgs e)
        {
            _engine.Osc.Seen.Clear();
            _oscSeenCount = -1;
        }

        private void MaybeRefreshOscList()
        {
            if ((DateTime.UtcNow - _lastOscListRefresh).TotalMilliseconds < 700) return;
            _lastOscListRefresh = DateTime.UtcNow;

            var seen = _engine.Osc.Seen.Count;
            if (seen == _oscSeenCount) return;
            _oscSeenCount = seen;

            var addresses = _engine.Osc.SeenAddresses();
            _oscListRebuilding = true;
            OscList.Items.Clear();
            foreach (var a in addresses) OscList.Items.Add(a);

            var wanted = new HashSet<string>(_s.OscParams ?? Array.Empty<string>());
            foreach (var item in OscList.Items)
                if (wanted.Contains((string)item)) OscList.SelectedItems.Add(item);
            _oscListRebuilding = false;
        }

        private void OscList_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _oscListRebuilding) return;
            // Mapped params VRChat hasn't sent yet aren't in the list, so they can't be
            // "selected" — carry them over instead of dropping them on the first click.
            var listed = new HashSet<string>(OscList.Items.Cast<string>());
            var unseen = (_s.OscParams ?? Array.Empty<string>()).Where(p => !listed.Contains(p));
            _s.OscParams = OscList.SelectedItems.Cast<string>().Concat(unseen).ToArray();
        }

        private void ManualLevel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _s.ManualLevel = ManualLevel.Value;
            ManualLevelVal.Text = (_s.ManualLevel * 100).ToString("0") + "%";
        }

        private void Pattern_Changed(object sender, EventArgs e)
        {
            if (!_ready) return;
            if (Enum.TryParse<PatternMode>(SelectedContent(PatternCombo), out var p)) _s.Pattern = p;
            _s.PatternPeriodSec = PatternPeriod.Value;
            _s.PatternDepth = PatternDepth.Value;
            PatternPeriodVal.Text = _s.PatternPeriodSec.ToString("0.0") + " s";
            PatternDepthVal.Text = (_s.PatternDepth * 100).ToString("0") + "%";
        }

        // ---------------------------------------------------------------- master

        private void CapSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _s.MasterCap = CapSlider.Value;
            CapVal.Text = (_s.MasterCap * 100).ToString("0") + "%";
        }

        private void RateSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _s.UpdateHz = (int)RateSlider.Value;
            RateVal.Text = _s.UpdateHz + " Hz";
        }

        private void SafetySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _s.MaxContinuousMinutes = (int)SafetySlider.Value;
            SafetyVal.Text = _s.MaxContinuousMinutes == 0 ? "off" : _s.MaxContinuousMinutes + " m";
        }

        private void MixCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (Enum.TryParse<MixMode>(SelectedContent(MixCombo), out var m)) _s.Mix = m;
        }

        // ---------------------------------------------------------------- hotkeys

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        private const int HK_PANIC = 1, HK_ARM = 2, HK_UP = 3, HK_DOWN = 4;
        private HwndSource _hwnd;

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            _hwnd = HwndSource.FromHwnd(handle);
            _hwnd?.AddHook(WndProc);

            var mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
            var ok = RegisterHotKey(handle, HK_PANIC, mods, 0x58)   // X
                   & RegisterHotKey(handle, HK_ARM, mods, 0x41)     // A
                   & RegisterHotKey(handle, HK_UP, mods, 0x26)      // Up
                   & RegisterHotKey(handle, HK_DOWN, mods, 0x28);   // Down
            if (!ok) Log.Write("Some global hotkeys could not be registered (another app may own them).");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_HOTKEY) return IntPtr.Zero;
            switch (wParam.ToInt32())
            {
                case HK_PANIC:
                    PanicBtn_Click(null, null);
                    break;
                case HK_ARM:
                    ArmBtn_Click(null, null);
                    break;
                case HK_UP:
                    ManualLevel.Value = Math.Min(1, ManualLevel.Value + 0.1);
                    break;
                case HK_DOWN:
                    ManualLevel.Value = Math.Max(0, ManualLevel.Value - 0.1);
                    break;
                default:
                    return IntPtr.Zero;
            }
            handled = true;
            return IntPtr.Zero;
        }

        // ---------------------------------------------------------------- shutdown

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                UnregisterHotKey(handle, HK_PANIC);
                UnregisterHotKey(handle, HK_ARM);
                UnregisterHotKey(handle, HK_UP);
                UnregisterHotKey(handle, HK_DOWN);
                _hwnd?.RemoveHook(WndProc);
            }
            catch { }

            try { _autosave?.Stop(); } catch { }
            Log.Line -= OnLogLine;
            _engine.Updated -= OnEngineUpdated;

            _s.Host = HostBox.Text.Trim();
            _s.Save();

            // Stop the toy before we go. The dead-man timeSec is the backstop if this fails.
            try { Task.Run(() => _engine.ShutdownAsync()).Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { _engine.Dispose(); } catch { }
        }
    }
}
