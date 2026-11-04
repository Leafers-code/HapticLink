using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HapticLink.Core;
using HapticLink.Sources;
using Buttplug.Client;
using Buttplug.Core;

internal static partial class Tests
{
    private static async Task TestIntegrations()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var s = new Settings { IntegrationPort = port, IntegrationTimeoutMs = 1500 };
        using var bridge = new IntegrationSource(s); bridge.Start();
        for (int i = 0; i < 100 && !bridge.Running; i++) await Task.Delay(20);
        Check(bridge.Running, "loopback integration server starts without administrator access");
        var endpoint = new Uri($"ws://127.0.0.1:{port}/");
        async Task<JsonDocument> Exchange(ClientWebSocket socket, string message)
        {
            using var timeout = new CancellationTokenSource(3000);
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, timeout.Token);
            using var stream = new MemoryStream(); var data = new byte[4096]; WebSocketReceiveResult result;
            do { result = await socket.ReceiveAsync(new ArraySegment<byte>(data), timeout.Token); stream.Write(data, 0, result.Count); } while (!result.EndOfMessage);
            return JsonDocument.Parse(stream.ToArray());
        }
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(endpoint, default);
            using var before = await Exchange(socket, "[{\"RequestDeviceList\":{\"Id\":1}}]");
            Check(before.RootElement[0].TryGetProperty("Error", out _), "bridge requires a protocol handshake");
            using var hello = await Exchange(socket, "[{\"RequestServerInfo\":{\"Id\":2,\"ClientName\":\"Compatibility test\",\"MessageVersion\":3}}]");
            Check(hello.RootElement[0].GetProperty("ServerInfo").GetProperty("MessageVersion").GetInt32() == 3, "Buttplug v3 handshake");
            using var list = await Exchange(socket, "[{\"RequestDeviceList\":{\"Id\":3}}]");
            Check(list.RootElement[0].GetProperty("DeviceList").GetProperty("Devices")[0].GetProperty("DeviceMessages").TryGetProperty("ScalarCmd", out _), "v3 virtual device advertises scalar vibration");
            using var scan = await Exchange(socket, "[{\"StartScanning\":{\"Id\":4}}]");
            Check(scan.RootElement.GetArrayLength() == 3 && scan.RootElement[1].TryGetProperty("DeviceAdded", out _), "scan emits device-added and scanning-finished events");
            using var speed = await Exchange(socket, "[{\"ScalarCmd\":{\"Id\":5,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.7,\"ActuatorType\":\"Vibrate\"}]}}]");
            bridge.Tick(50); Check(Math.Abs(bridge.Raw - .7) < .001, "real WebSocket vibration reaches event source");
            using var bad = await Exchange(socket, "[{\"ScalarCmd\":{\"Id\":6,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":2,\"ActuatorType\":\"Vibrate\"}]}}]");
            Check(bad.RootElement[0].TryGetProperty("Error", out _), "out-of-range integration commands rejected");
            using var stop = await Exchange(socket, "[{\"StopAllDevices\":{\"Id\":7}}]"); bridge.Tick(50);
            Check(bridge.Raw == 0, "integration stop clears virtual output");
            using var restart = await Exchange(socket, "[{\"ScalarCmd\":{\"Id\":8,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.6,\"ActuatorType\":\"Vibrate\"}]}}]");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }
        for (int i = 0; i < 30 && bridge.ClientCount > 0; i++) await Task.Delay(10);
        bridge.Tick(50); Check(bridge.Raw == 0 && bridge.ClientCount == 0, "client disconnect drops its vibration signal");
        foreach (var version in new[] { 2, 4 })
        {
            using var socket = new ClientWebSocket(); await socket.ConnectAsync(endpoint, default);
            var handshake = version == 4 ? "[{\"RequestServerInfo\":{\"Id\":1,\"ClientName\":\"v4 test\",\"ProtocolVersionMajor\":4,\"ProtocolVersionMinor\":0}}]"
                : "[{\"RequestServerInfo\":{\"Id\":1,\"ClientName\":\"v2 test\",\"MessageVersion\":2}}]";
            using var hello = await Exchange(socket, handshake);
            using var list = await Exchange(socket, "[{\"RequestDeviceList\":{\"Id\":2}}]");
            Check(version == 4 ? list.RootElement[0].GetProperty("DeviceList").GetProperty("Devices").GetProperty("0").TryGetProperty("DeviceFeatures", out _)
                : list.RootElement[0].GetProperty("DeviceList").GetProperty("Devices")[0].GetProperty("DeviceMessages").TryGetProperty("VibrateCmd", out _), $"Buttplug v{version} device enumeration");
            using var output = await Exchange(socket, version == 4
                ? "[{\"OutputCmd\":{\"Id\":3,\"DeviceIndex\":0,\"FeatureIndex\":0,\"Command\":{\"Vibrate\":{\"Value\":12}}}}]"
                : "[{\"VibrateCmd\":{\"Id\":3,\"DeviceIndex\":0,\"Speeds\":[{\"Index\":0,\"Speed\":0.6}]}}]");
            bridge.Tick(50); Check(Math.Abs(bridge.Raw - .6) < .001, $"Buttplug v{version} output mapping");
            if (version == 4)
            {
                await Task.Delay(1600); bridge.Tick(50);
                Check(bridge.Raw == 0, "missing client ping expires held output");
            }
            else await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }
        using (var official = new ButtplugClient("HapticLink interoperability check"))
        {
            await official.ConnectAsync(new ButtplugWebsocketConnector(endpoint)).WaitAsync(TimeSpan.FromSeconds(5));
            Check(official.Devices.Length == 1, "official Buttplug 5.0.1 client discovers HapticLink");
            await official.Devices.Single().RunOutputAsync(DeviceOutput.Vibrate.Percent(.5)).WaitAsync(TimeSpan.FromSeconds(5));
            bridge.Tick(50); Check(Math.Abs(bridge.Raw - .5) < .001, "official client vibration command reaches mixer input");
            await official.StopAllDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));
            bridge.Tick(50); Check(bridge.Raw == 0, "official client StopCmd clears output");
            await official.DisconnectAsync();
        }
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var pulse = await http.PostAsJsonAsync("/event", new { @event = "custom", level = .8, durationMs = 100 });
        bridge.Tick(25); Check(pulse.IsSuccessStatusCode && bridge.Raw > .79, "HTTP event input produces a bounded pulse");
        await Task.Delay(140); bridge.Tick(25); Check(bridge.Raw == 0, "HTTP event pulse expires");
        var invalid = await http.PostAsJsonAsync("/event", new { @event = "custom", level = 5, durationMs = 100 });
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "HTTP event input validates levels");
        object Frame(int kills, int health, string steam = "own") => new
        {
            provider = new { steamid = "own" },
            player = new { steamid = steam, activity = "playing", team = "CT", state = new { health }, match_stats = new { kills, deaths = 0 } },
            round = new { phase = "live" }
        };
        await http.PostAsJsonAsync("/cs2", Frame(5, 100)); bridge.Tick(50);
        Check(bridge.Raw == 0, "CS2 first snapshot establishes a baseline without firing");
        await http.PostAsJsonAsync("/cs2", Frame(6, 100)); bridge.Tick(50);
        Check(bridge.Raw > .79 && bridge.LastEvent.StartsWith("kill"), "CS2 kill delta triggers configured rule");
        bridge.Clear(); await http.PostAsJsonAsync("/cs2", Frame(9, 5, "spectated")); bridge.Tick(50);
        Check(bridge.Raw == 0, "CS2 spectated players cannot generate own-player events");
        bridge.Stop(); Check(!bridge.Running && bridge.Raw == 0, "event server shuts down cleanly");
    }

    private static void TestEqualizerAndRouting()
    {
        double Energy(HapticEqualizer eq, Settings settings, double frequency)
        {
            eq.Configure(settings); double sum = 0;
            for (int n = 0; n < 24000; n++) { var x = .05 * Math.Sin(2 * Math.PI * frequency * n / 48000); var y = eq.Process(x, 0); if (n > 12000) sum += y * y; }
            return sum;
        }
        var s = new Settings(); var eq = new HapticEqualizer();
        var flat = Energy(eq, s, 1000); s.AudioEqGainsDb[5] = 12; var boosted = Energy(eq, s, 1000);
        Check(boosted > flat * 12 && boosted < flat * 18, "EQ provides approximately 12 dB boost at its center frequency");
        s.AudioEqEnabled = false; var bypassed = Energy(eq, s, 1000);
        Check(Math.Abs(bypassed / flat - 1) < .02, "EQ bypass restores flat analysis");
        s.AudioEqEnabled = true; s.AudioEqGainsDb[5] = -12; var cut = Energy(eq, s, 1000);
        Check(cut < flat * .08, "EQ attenuation reduces band energy");
        s.AudioEqEnabled = false; s.AudioBand = AudioBand.Bass;
        var bassPass = Energy(eq, s, 80); var bassReject = Energy(eq, s, 2000);
        Check(bassPass > flat * .8 && bassReject < bassPass * .001, "bass-only capture passes bass and rejects midrange audio");
        s.AudioBand = AudioBand.Custom; s.AudioCaptureLowHz = 400; s.AudioCaptureHighHz = 800;
        var customPass = Energy(eq, s, 600);
        Check(customPass > flat * .7 && Energy(eq, s, 80) < customPass * .001 && Energy(eq, s, 4000) < customPass * .001, "custom capture range rejects audio above and below its selected band");
        s.AudioFrequencyCaptureEnabled = false;
        Check(Math.Abs(Energy(eq, s, 80) / flat - 1) < .02 && AudioFrequencyRange.Selected(s) == (400d, 800d), "frequency plugin bypasses filtering without losing the selected range");
        s.AudioFrequencyCaptureEnabled = true;
        s.AudioCaptureLowHz = 2000; s.AudioCaptureHighHz = 5000;
        Check(Energy(eq, s, 600) < customPass * .001 && Energy(eq, s, 3000) > flat * .8, "changing the capture range applies live without restarting audio");
        s.AudioBand = AudioBand.Loudness;
        Check(Math.Abs(Energy(eq, s, 80) / flat - 1) < .02, "full-range capture restores the unfiltered signal");
        Check(AudioFrequencyRange.Normalize(30000, -1) == (19990d, 20000d) && AudioFrequencyRange.Normalize(double.NaN, double.NaN) == (20d, 20000d), "capture cutoffs remain ordered and finite for malformed settings");
        var apps = AudioRouting.ParseApplications("[{\"index\":7,\"sink\":2,\"properties\":{\"application.process.id\":\"123\",\"application.name\":\"Game\"}}]");
        Check(apps.Single().ProcessId == 123 && apps.Single().Stream == 7, "application audio metadata preserves PID and stream index");
        var windows = GameFocus.ParseWindows("0x00200001  0 123 10 20 1280 720 host My Game Window\n");
        Check(windows.Single().Title == "My Game Window" && windows.Single().Width == 1280, "X11 window parsing preserves title and geometry");
        s.PauseWhenGameUnfocused = true;
        Check(new FocusState(false, false, null, "unavailable").Pauses(s) && !new FocusState(true, true, windows[0], "focused").Pauses(s), "focus policy pauses when unknown and runs when game is focused");
    }

    private static async Task TestFocusGate()
    {
        var s = new Settings { ManualLevel = 1, PauseWhenGameUnfocused = true, UpdateHz = 20 };
        using var engine = new Engine(s); EngineState current = null; engine.Updated += value => current = value;
        engine.Start(); engine.Arm(true); engine.Test(.25, 2);
        await Task.Delay(200);
        Check(current is { FocusPaused: true, Output: 0 } && current.Channels.All(c => c.Output == 0), "unknown game focus gates manual and explicit test output");
        Check(current.ContinuousRun == TimeSpan.Zero, "paused focus does not consume continuous-output budget");
        await engine.ShutdownAsync();
    }
    private static void TestPcmNormalization()
    {
        var normalizer = new StereoPcmNormalizer();
        var mono = new byte[441 * 2];
        for (int i = 0; i < mono.Length; i += 2) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(mono.AsSpan(i, 2), 16384);
        var first = normalizer.Convert(mono, mono.Length, 44100, 1, 16, false);
        var second = normalizer.Convert(mono, mono.Length, 44100, 1, 16, false);
        Check((first.Length + second.Length) / 8 is >= 958 and <= 960, "Windows PCM normalizer resamples 44.1 kHz continuously to 48 kHz");
        Check(Math.Abs(BitConverter.ToSingle(second, 0) - .5) < .001 && BitConverter.ToSingle(second, 0) == BitConverter.ToSingle(second, 4), "mono PCM duplicates into equal stereo channels");
        var stereo = new byte[48 * 8];
        for (int i = 0; i < stereo.Length; i += 8) { System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(stereo.AsSpan(i, 4), .25f); System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(stereo.AsSpan(i + 4, 4), -.25f); }
        var preserved = new StereoPcmNormalizer().Convert(stereo, stereo.Length, 48000, 2, 32, true);
        Check(BitConverter.ToSingle(preserved, 0) == .25f && BitConverter.ToSingle(preserved, 4) == -.25f, "Windows float PCM preserves opposite-phase stereo");
    }
    private static async Task TestEventStopGate()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var s = new Settings { IntegrationPort = port, UpdateHz = 20 };
        s.Manual.Enabled = false; s.Events.Enabled = true; s.Events.ReleaseMs = 2000;
        using var engine = new Engine(s); EngineState state = null; engine.Updated += next => state = next;
        engine.Start(); engine.Arm(true);
        for (int i = 0; i < 100 && !engine.Events.Running; i++) await Task.Delay(10);
        engine.Events.Trigger("death"); await Task.Delay(100);
        Check(state is { Output: > .9 }, "game event reaches the armed mixer");
        engine.Events.Clear(); await Task.Delay(100);
        Check(state.Output == 0, "integration stop clears release smoothing immediately");
        await engine.ShutdownAsync();
    }
}
