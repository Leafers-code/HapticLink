using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HapticLink;
using HapticLink.Core;
using HapticLink.Sources;
using HapticLink.Toy;

internal static partial class Tests
{
    private static int _passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); _passed++;
    }

    public static async Task Main(string[] args)
    {
        var home = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "hapticlink-tests-" + Guid.NewGuid()));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HAPTICLINK_HOME", home);
        var s = new Settings { ManualLevel = 1, MasterCap = .4, UpdateHz = 20 };
        s.Manual.Deadzone = 0;
        await TestNetworkAndEngine(s);
        await TestOsc();
        TestAudio();
        TestConfig(s);
        await TestIntegrations();
        await TestPlugins(home);
        TestEqualizerAndRouting();
        TestPcmNormalization();
        await TestFocusGate();
        await TestEventStopGate();
        // All GUI work stays on one thread; this does not open native windows or use devices.
        RunUi(home);
        Console.WriteLine($"{_passed} checks passed.");
    }

    private static async Task TestNetworkAndEngine(Settings s)
    {
        await using var server = new FakeRemote();
        s.Host = "127.0.0.1"; s.HttpPort = server.Port;
        using var engine = new Engine(s);
        Check(await engine.Client.ConnectAsync(s), "LAN client connects to fake Remote");
        Check(engine.Client.Toys.Count == 1, "double-encoded toy JSON parsed");
        EngineState current = null;
        engine.Updated += state => current = state;
        engine.Start();
        async Task Next(Func<EngineState, bool> predicate)
        {
            current = null;
            for (int i = 0; i < 100; i++)
            {
                if (current != null && predicate(current)) return;
                await Task.Delay(20);
            }
            throw new Exception("Engine state timeout");
        }
        await Next(x => !x.Armed && x.Output == 0);
        Check(current.Output == 0, "startup is disarmed with nonzero manual input");
        engine.Arm(true); await Next(x => x.Armed && x.Output > .39);
        Check(current.Output <= .4 && current.Channels.All(c => c.Output <= .4), "master cap applied per motor");
        Check(server.Messages.Any(x => x.Contains("Vibrate:8") && x.Contains("\"timeSec\":4")), "network output carries finite command expiry");
        engine.Test(.8, 2); engine.Arm(false); await Next(x => x.Output == 0);
        Check(current.Output == 0, "disarming cancels active test pulse");
        engine.Test(.8, 2); await Next(x => x.Output > 0);
        Check(current.Output <= .4, "explicit test pulse respects master cap");
        engine.Panic(); await Next(x => x.Panicked && x.Output == 0);
        Check(!current.Armed && s.ManualLevel == 0 && current.Channels.All(c => c.Output == 0), "panic clears manual level and silences all channels");
        engine.ClearPanic(); await Next(x => !x.Panicked);
        Check(current.Output == 0 && !current.Armed, "clearing panic cannot resume a test pulse");
        await engine.ShutdownAsync();
        Check(server.Messages.Any(x => x.Contains("\"action\":\"Stop\"")), "stop command sent on disarm/panic/shutdown");
    }

    private static async Task TestOsc()
    {
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint).Port; reservation.Close();
        var s = new Settings { OscPort = port, OscOgbEnable = false, OscAutoSensors = false, OscBuildUp = false, OscParams = new[] { "/avatar/parameters/Test" } };
        using var osc = new OscSource(s); osc.Start();
        var address = Encoding.ASCII.GetBytes("/avatar/parameters/Test\0");
        var offset = (address.Length + 3) & ~3;
        var packet = new byte[offset + 8]; address.CopyTo(packet, 0); packet[offset] = (byte)','; packet[offset + 1] = (byte)'f';
        BinaryPrimitives.WriteSingleBigEndian(packet.AsSpan(offset + 4), .6f);
        using var udp = new UdpClient(); await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port));
        for (int i = 0; i < 50 && !osc.Seen.ContainsKey(s.OscParams[0]); i++) await Task.Delay(10);
        osc.Tick(50);
        Check(Math.Abs(osc.Raw - .6) < .001, "real UDP OSC float reaches configured signal");
        osc.Stop(); Check(osc.Raw == 0 && !osc.Running, "OSC stop releases socket and clears signal");
    }

    private static void TestAudio()
    {
        var s = new Settings { AudioFloorDb = -60, AudioCeilDb = 0 };
        using var audio = new AudioSource(s);
        var process = typeof(AudioSource).GetMethod("ProcessSamples", BindingFlags.Instance | BindingFlags.NonPublic);
        var buffer = new byte[3840];
        for (int off = 0; off < buffer.Length; off += 8)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(off, 4), .5f);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(off + 4, 4), -.5f);
        }
        process.Invoke(audio, new object[] { buffer });
        Check(audio.Raw > .89 && audio.Raw < .91, "stereo energy survives opposite phase channels");
        process.Invoke(audio, new object[] { new byte[3840] });
        Check(audio.Raw == 0, "silent PCM produces zero");
        var tone = new byte[38400];
        for (int n = 0; n < tone.Length / 8; n++)
            for (int c = 0; c < 2; c++)
                BinaryPrimitives.WriteSingleLittleEndian(tone.AsSpan(n * 8 + c * 4, 4), (float)(.5 * Math.Sin(2 * Math.PI * 80 * n / 48000)));
        s.AudioBand = AudioBand.Bass; process.Invoke(audio, new object[] { tone }); var bass = audio.Raw;
        s.AudioBand = AudioBand.Treble; process.Invoke(audio, new object[] { tone });
        Check(bass > audio.Raw + .3, "bass and treble filters distinguish low-frequency audio");
        s.AudioBand = AudioBand.Custom; s.AudioCaptureLowHz = 60; s.AudioCaptureHighHz = 120;
        process.Invoke(audio, new object[] { tone }); var selectedBass = audio.Raw;
        s.AudioCaptureLowHz = 2000; s.AudioCaptureHighHz = 5000;
        // Let the transition settle across multiple input buffers, as a live capture does.
        for (int i = 0; i < 4; i++) process.Invoke(audio, new object[] { tone });
        Check(selectedBass > .7 && audio.Raw == 0, "audio detector derives haptics from the chosen frequency range");
    }

    private static void TestConfig(Settings s)
    {
        s.Host = ""; s.AudioBand = AudioBand.Custom; s.AudioCaptureLowHz = 70; s.AudioCaptureHighHz = 140; s.OscShared = s.CaptureOsc(); s.Save();
        var loaded = Settings.Load();
        Check(loaded.MasterCap == s.MasterCap && loaded.OscShared != null, "config round-trip retains existing settings schema");
        Check(loaded.AudioBand == AudioBand.Custom && AudioFrequencyRange.For(loaded) == (70d, 140d), "custom capture frequency selection survives configuration round-trip");
        loaded.SwitchOsc("avtr_test"); loaded.RememberOscAvatar("Test avatar"); loaded.Osc.Gain = 2;
        loaded.SwitchOsc("avtr_other"); Check(loaded.Osc.Gain != 2, "other avatars use shared profile");
        loaded.SwitchOsc("avtr_test"); Check(loaded.Osc.Gain == 2, "remembered avatar profile returns");
        // GUI test starts with all backends disabled and no phone configured.
        new Settings().Save();
    }

    private static void RunUi(string home)
    {
        Environment.SetEnvironmentVariable("HAPTICLINK_HOME", Path.Combine(home, "ui-config-" + Guid.NewGuid().ToString("N")));
        new Settings().Save();
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window = new MainWindow(); window.Show();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        Check(tabs.Items.Count == 9, "all nine UI tabs constructed");
        foreach (TabItem tab in tabs.Items)
        {
            tabs.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var image = window.CaptureRenderedFrame();
            Check(image != null && image.PixelSize.Width > 800, "render tab: " + tab.Header);
            image.Save(Path.Combine(home, "ui-" + tab.Header.ToString().Replace(' ', '-') + ".png"));
            if (tab.Header.ToString() == "EQ")
            {
                var eq = (EqualizerPanel)typeof(MainWindow).GetField("_equalizer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                eq.BringIntoView(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var detail = window.CaptureRenderedFrame(); detail.Save(Path.Combine(home, "ui-Equalizer.png"));
            }
        }
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => t.Header.ToString() == "EQ");
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var bassButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Bass");
        bassButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var liveSettings = (Settings)typeof(MainWindow).GetField("_s", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        Check(liveSettings.AudioBand == AudioBand.Bass && AudioFrequencyRange.For(liveSettings) == (20d, 180d), "Bass preset selects the actual audio capture band");
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var bassImage = window.CaptureRenderedFrame()) bassImage.Save(Path.Combine(home, "ui-EQ-Bass.png"));
        var cutoffs = window.GetVisualDescendants().OfType<NumericUpDown>().ToList();
        cutoffs.Single(c => c.Name == "CaptureFromHz").Value = 80;
        cutoffs.Single(c => c.Name == "CaptureToHz").Value = 140;
        Check(liveSettings.AudioBand == AudioBand.Custom && AudioFrequencyRange.For(liveSettings) == (80d, 140d), "cutoff controls select a custom audio capture range");
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var customImage = window.CaptureRenderedFrame()) customImage.Save(Path.Combine(home, "ui-EQ-Custom.png"));
        window.Width = 1040; window.Height = 700;
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => t.Header.ToString() == "Manual");
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        bool InViewport(Control control)
        {
            var point = control.TranslatePoint(new Avalonia.Point(), window);
            return point.HasValue && new Avalonia.Rect(window.ClientSize).Contains(new Avalonia.Rect(point.Value, control.Bounds.Size));
        }
        Check(window.GetVisualDescendants().OfType<Button>().Where(b => b.Name is "ArmOutput" or "PanicStop").All(InViewport), "arm and panic remain visible at minimum window size");
        Check(window.GetVisualDescendants().OfType<TextBox>().Where(b => b.Name == "ActivityLog").All(InViewport), "persistent log remains visible at minimum window size");
        Check(tabs.Items.Cast<TabItem>().All(InViewport), "all tab headers fit at minimum window size");
        using (var compactImage = window.CaptureRenderedFrame()) compactImage.Save(Path.Combine(home, "ui-Minimum-size.png"));
        window.Width = 1200; window.Height = 800;
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => t.Header.ToString() == "Plugins");
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var manualFeature = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "PluginFeature-manual");
        manualFeature.IsChecked = false;
        typeof(MainWindow).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
        var sourceToggles = (CheckBox[])typeof(MainWindow).GetField("_sourceToggles", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        Check(!liveSettings.Manual.Enabled && sourceToggles[3].IsChecked == false, "plugin source toggle stays synchronized with the original source controls");
        manualFeature.IsChecked = true;
        var frequencyFeature = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "PluginFeature-frequency");
        frequencyFeature.IsChecked = false;
        Check(!liveSettings.AudioFrequencyCaptureEnabled && AudioFrequencyRange.For(liveSettings) == (20d, 20000d), "plugin manager bypasses the live frequency capture filter");
        frequencyFeature.IsChecked = true;
        void ClickButton(string title) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == title).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ClickButton("Available"); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var availableImage = window.CaptureRenderedFrame()) availableImage.Save(Path.Combine(home, "ui-Plugins-Available.png"));
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PluginSearch"); search.Text = "Counter-Strike";
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Install-cs2-events").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var liveEngine = (Engine)typeof(MainWindow).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        Check(liveEngine.Plugins.Installed.Single().Profile.Kind == "cs2" && !liveSettings.Events.Enabled && !liveEngine.Armed, "UI catalog installs a profile without enabling the bridge or arming output");
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var installedImage = window.CaptureRenderedFrame()) installedImage.Save(Path.Combine(home, "ui-Plugins-Installed.png"));
        ClickButton("Remove"); Check(liveEngine.Plugins.Installed.Length == 0, "plugin UI removes the installed profile");
        ClickButton("Undo removal"); Check(liveEngine.Plugins.Installed.Length == 1 && !liveEngine.Plugins.Installed[0].Enabled, "plugin UI can undo a removal without enabling the profile");
        // Rebuild after avatar changes: repeated controls must not retain an old parent.
        var oscPanel = typeof(MainWindow).GetMethod("OscPanel", BindingFlags.NonPublic | BindingFlags.Instance);
        var rebuilt = (Control)oscPanel.Invoke(window, null);
        Check(rebuilt != null, "OSC controls can rebuild on avatar change");
        var combos = window.GetVisualDescendants().OfType<ComboBox>().ToList();
        var setChoices = typeof(MainWindow).GetMethod("SetChoices", BindingFlags.NonPublic | BindingFlags.Static);
        var scratch = new ComboBox(); scratch.Items.Add("initial");
        setChoices.Invoke(null, new object[] { scratch, new List<(string, string)> { ("test", "Device") }, "test", "Default" });
        Check(scratch.SelectedIndex == 1, "device refresh replaces initial items and retains selection");
        using (var overlayEngine = new Engine(new Settings()))
        {
            var overlay = new OverlayWindow(new Settings { OverlayInteractive = true }, overlayEngine);
            overlay.Show(); overlay.Refresh(new EngineState(), new FocusState(false, false, null, "Preview game"));
            Dispatcher.UIThread.RunJobs(); overlay.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var overlayImage = overlay.CaptureRenderedFrame();
            Check(overlayImage != null && overlayImage.PixelSize.Width >= 350, "overlay renders with five source meters and controls");
            overlayImage.Save(Path.Combine(home, "ui-Overlay-preview.png"));
            var eqButton = overlay.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "EQ");
            eqButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); overlay.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Check(overlay.Height > 500, "interactive overlay expands live EQ controls");
            using var overlayEq = overlay.CaptureRenderedFrame(); overlayEq.Save(Path.Combine(home, "ui-Overlay-EQ.png"));
            overlay.Close();
        }
        window.Close();
        for (int i = 0; i < 30 && window.IsVisible; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Check(!window.IsVisible, "GUI shutdown completes");
    }

    private sealed class FakeRemote : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public ConcurrentQueue<string> Messages { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public FakeRemote()
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                        int length = 0;
                        while (await reader.ReadLineAsync() is { Length: > 0 } header)
                            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Split(':')[1]);
                        var chars = new char[length];
                        for (int read = 0; read < length;)
                        {
                            var count = await reader.ReadAsync(chars.AsMemory(read));
                            if (count == 0) throw new EndOfStreamException();
                            read += count;
                        }
                        var body = new string(chars); Messages.Enqueue(body);
                        var map = JsonSerializer.Serialize(new { toy1 = new { id = "toy1", name = "Hush", status = 1, battery = 80 } });
                        var response = body.Contains("GetToys") ? JsonSerializer.Serialize(new { code = 200, data = new { toys = map } }) : "{\"code\":200}";
                        var bytes = Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(bytes);
                    }
                    catch (OperationCanceledException) { break; }
                }
            });
        }
        public async ValueTask DisposeAsync() { _cts.Cancel(); _listener.Stop(); await _loop; _cts.Dispose(); }
    }
}
