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

internal static class Tests
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
    }

    private static void TestConfig(Settings s)
    {
        s.Host = ""; s.OscShared = s.CaptureOsc(); s.Save();
        var loaded = Settings.Load();
        Check(loaded.MasterCap == s.MasterCap && loaded.OscShared != null, "config round-trip retains existing settings schema");
        loaded.SwitchOsc("avtr_test"); loaded.RememberOscAvatar("Test avatar"); loaded.Osc.Gain = 2;
        loaded.SwitchOsc("avtr_other"); Check(loaded.Osc.Gain != 2, "other avatars use shared profile");
        loaded.SwitchOsc("avtr_test"); Check(loaded.Osc.Gain == 2, "remembered avatar profile returns");
        // GUI test starts with all backends disabled and no phone configured.
        new Settings().Save();
    }

    private static void RunUi(string home)
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window = new MainWindow(); window.Show();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        Check(tabs.Items.Count == 6, "all six UI tabs constructed");
        foreach (TabItem tab in tabs.Items)
        {
            tabs.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var image = window.CaptureRenderedFrame();
            Check(image != null && image.PixelSize.Width > 800, "render tab: " + tab.Header);
            image.Save(Path.Combine(home, "ui-" + tab.Header.ToString().Replace(' ', '-') + ".png"));
        }
        // Rebuild after avatar changes: repeated controls must not retain an old parent.
        var oscPanel = typeof(MainWindow).GetMethod("OscPanel", BindingFlags.NonPublic | BindingFlags.Instance);
        var rebuilt = (Control)oscPanel.Invoke(window, null);
        Check(rebuilt != null, "OSC controls can rebuild on avatar change");
        var combos = window.GetVisualDescendants().OfType<ComboBox>().ToList();
        var setChoices = typeof(MainWindow).GetMethod("SetChoices", BindingFlags.NonPublic | BindingFlags.Static);
        var scratch = new ComboBox(); scratch.Items.Add("initial");
        setChoices.Invoke(null, new object[] { scratch, new List<(string, string)> { ("test", "Device") }, "test", "Default" });
        Check(scratch.SelectedIndex == 1, "device refresh replaces initial items and retains selection");
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
