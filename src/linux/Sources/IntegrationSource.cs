using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HapticLink.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HapticLink.Sources;

/// <summary>A loopback Buttplug v2/v3/v4 vibration receiver plus HTTP event/GSI input.</summary>
public sealed class IntegrationSource : SourceBase
{
    public override string Name => "Game events";
    private readonly Settings _s;
    private readonly PluginManager _plugins;
    private CancellationTokenSource _cts;
    private Task _runner;
    private readonly ConcurrentDictionary<Guid, Session> _clients = new();
    private readonly ConcurrentDictionary<string, (double level, long until, string profile)> _pulses = new();
    private readonly ConcurrentDictionary<string, long> _cooldowns = new();
    private readonly ConcurrentQueue<string> _history = new();
    private readonly object _gsiGate = new();
    private readonly Dictionary<string, Cs2Snapshot> _cs2 = new();
    private long _sequence;
    private long _stopRevision;
    private int _activePort;
    public long StopRevision => Interlocked.Read(ref _stopRevision);
    public string LastEvent { get; private set; } = "Waiting for an integration";
    public string[] EventHistory => _history.ToArray();
    public int ClientCount => _clients.Count;
    public string Endpoint => $"ws://127.0.0.1:{(Running ? _activePort : _s.IntegrationPort)}";
    public IntegrationSource(Settings s, PluginManager plugins = null)
    {
        _s = s; _plugins = plugins ?? new PluginManager(s); _plugins.Changed += ProfileChanged;
    }
    public int ActivePort => Running ? _activePort : _s.IntegrationPort;
    private void ProfileChanged(string id) => ClearProfile(id, true);
    private void ClearProfile(string id, bool disconnect)
    {
        foreach (var entry in _pulses) if (entry.Value.profile == id) _pulses.TryRemove(entry.Key, out _);
        foreach (var key in _cooldowns.Keys) if (key.StartsWith(id + "\0", StringComparison.Ordinal)) _cooldowns.TryRemove(key, out _);
        foreach (var session in _clients.Values) if (session.ProfileId == id) { session.Level = 0; if (disconnect) session.Socket.Abort(); }
        lock (_gsiGate) _cs2.Remove(id);
        Interlocked.Increment(ref _stopRevision); Tick(0);
    }
    private void StopSession(Session session) { if (session.ProfileId == null) Clear(); else ClearProfile(session.ProfileId, false); }

    public override void Start()
    {
        if (_runner is { IsCompleted: false }) return;
        _cts = new(); var token = _cts.Token; _activePort = _s.IntegrationPort;
        Available = true; Status = "Starting event bridge…";
        _runner = Task.Run(async () =>
        {
            try
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseKestrel(options => { options.Listen(IPAddress.Loopback, _activePort); options.Limits.MaxConcurrentConnections = 32; });
                await using var app = builder.Build();
                app.UseWebSockets();
                app.MapGet("/status", () => Results.Json(new { name = "HapticLink", clients = ClientCount, lastEvent = LastEvent, level = Raw }));
                app.MapPost("/event", context => EventRequest(context, false));
                app.MapPost("/cs2", context => EventRequest(context, true));
                app.MapPost("/plugins/{id}/event", context => EventRequest(context, false, context.Request.RouteValues["id"]?.ToString()));
                app.MapPost("/plugins/{id}/cs2", context => EventRequest(context, true, context.Request.RouteValues["id"]?.ToString()));
                app.MapGet("/plugins/{id}", context => WebSocketRequest(context, token, context.Request.RouteValues["id"]?.ToString()));
                app.MapGet("/", context => WebSocketRequest(context, token));
                await app.StartAsync(token);
                Running = true; Status = "Listening: " + Endpoint;
                try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
                foreach (var session in _clients.Values) session.Socket.Abort();
                using var shutdown = new CancellationTokenSource(2000);
                await app.StopAsync(shutdown.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Available = false; Status = "Event bridge: " + ex.Message; Log.Write(Status); }
            finally { Running = false; Clear(); }
        });
    }

    private async Task WebSocketRequest(HttpContext context, CancellationToken token, string profileId = null)
    {
        if (!IsLoopback(context.Connection.RemoteIpAddress)) { context.Response.StatusCode = 403; return; }
        if (profileId != null && _plugins.GetEnabled(profileId, "buttplug") == null) { context.Response.StatusCode = 404; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (_clients.Count >= 8) { context.Response.StatusCode = 503; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(); await ReceiveClient(socket, token, profileId);
    }

    private async Task EventRequest(HttpContext context, bool cs2, string profileId = null)
    {
        if (!IsLoopback(context.Connection.RemoteIpAddress)) { context.Response.StatusCode = 403; return; }
        if (profileId != null && _plugins.GetEnabled(profileId, cs2 ? "cs2" : "http-events") == null) { context.Response.StatusCode = 404; return; }
        if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
        if (context.Request.ContentLength > 65536) { context.Response.StatusCode = 413; return; }
        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (limited.Length + read > 65536) { context.Response.StatusCode = 413; return; }
                limited.Write(buffer, 0, read);
            }
            using var doc = JsonDocument.Parse(limited.ToArray());
            if (cs2) ProcessCs2(doc.RootElement, profileId);
            else
            {
                var root = doc.RootElement;
                var name = root.GetProperty("event").GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new JsonException("Invalid event name");
                if (profileId != null)
                {
                    var profile = _plugins.GetEnabled(profileId, "http-events");
                    if (profile == null) { context.Response.StatusCode = 404; return; }
                    if (!profile.Rules.Any(r => r.Event.Equals(name, StringComparison.OrdinalIgnoreCase)) || root.TryGetProperty("level", out _) || root.TryGetProperty("durationMs", out _)) throw new JsonException("Profile endpoints use configured event mappings.");
                    Trigger(name, profileId);
                }
                else if (root.TryGetProperty("level", out var level))
                {
                    var value = level.GetDouble();
                    var duration = root.TryGetProperty("durationMs", out var d) ? d.GetInt32() : 300;
                    if (!double.IsFinite(value) || value < 0 || value > 1 || duration < 0 || duration > 5000) throw new JsonException("level must be 0..1 and durationMs 0..5000");
                    Pulse(name, value, duration);
                }
                else Trigger(name);
            }
            await context.Response.WriteAsJsonAsync(new { ok = true }, context.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid game event JSON" }); }
    }

    private static bool IsLoopback(IPAddress ip) => ip != null && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    public void Trigger(string name, string profileId = null)
    {
        var rules = profileId == null ? _s.EventRules : _plugins.GetEnabled(profileId)?.Rules;
        var rule = rules?.FirstOrDefault(r => r.Enabled && string.Equals(r.Event, name, StringComparison.OrdinalIgnoreCase));
        if (rule == null) return;
        var now = Environment.TickCount64;
        var key = (profileId ?? "") + "\0" + rule.Event.ToUpperInvariant();
        if (_cooldowns.TryGetValue(key, out var last) && now - last < Math.Max(0, rule.CooldownMs)) return;
        _cooldowns[key] = now;
        Pulse(name, Sig.Clamp01(rule.Level), Math.Clamp(rule.DurationMs, 0, 5000), profileId);
    }
    private void Pulse(string name, double level, int duration, string profileId = null)
    {
        if (_pulses.Count >= 1024 || profileId != null && _plugins.GetEnabled(profileId) == null) return;
        _pulses[Interlocked.Increment(ref _sequence).ToString()] = (level, Environment.TickCount64 + duration, profileId);
        Note((profileId == null ? "" : profileId + ": ") + name + " · " + level.ToString("P0") + " · " + duration + " ms");
    }
    private void Note(string text)
    {
        LastEvent = text; _history.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
        while (_history.Count > 30) _history.TryDequeue(out _);
    }
    public void Clear()
    {
        Interlocked.Increment(ref _stopRevision);
        _pulses.Clear(); _cooldowns.Clear();
        foreach (var client in _clients.Values) client.Level = 0;
        Raw = 0;
    }
    public override void Tick(double dtMs)
    {
        if (!Running) { Raw = 0; return; }
        var now = Environment.TickCount64;
        double max = 0;
        foreach (var (id, pulse) in _pulses)
            if (now >= pulse.until || pulse.profile != null && _plugins.GetEnabled(pulse.profile) == null) _pulses.TryRemove(id, out _); else max = Math.Max(max, pulse.level);
        var timeout = Math.Clamp(_s.IntegrationTimeoutMs, 1000, 30000);
        foreach (var client in _clients.Values)
            if (now - Interlocked.Read(ref client.LastPing) > timeout || client.ProfileId != null && _plugins.GetEnabled(client.ProfileId, "buttplug") == null) { client.Level = 0; client.Socket.Abort(); }
            else max = Math.Max(max, client.Level);
        Raw = Sig.Clamp01(max);
        Status = $"{Endpoint} · {ClientCount} client(s) · {LastEvent}";
    }

    private sealed class Session
    {
        public WebSocket Socket;
        public string ProfileId;
        public int Version;
        public double Level;
        public long LastPing = Environment.TickCount64;
    }
    private async Task ReceiveClient(WebSocket socket, CancellationToken cancellation, string profileId = null)
    {
        var id = Guid.NewGuid(); var session = new Session { Socket = socket, ProfileId = profileId };
        _clients[id] = session;
        try
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(Math.Clamp(_s.IntegrationTimeoutMs, 1000, 30000) + 500);
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", cancellation); return; }
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65536)
                    { await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "JSON messages limited to 64 KiB", cancellation); return; }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                var replies = new List<object>();
                try
                {
                    using var doc = JsonDocument.Parse(message.ToArray());
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
                    foreach (var command in doc.RootElement.EnumerateArray())
                        if (profileId == null || _plugins.GetEnabled(profileId, "buttplug") != null) Handle(command, session, replies);
                        else replies.Add(Error(0, "Profile is disabled"));
                }
                catch (JsonException) { replies.Add(Error(0, "Expected a Buttplug JSON message array")); }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(replies);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { session.Level = 0; _clients.TryRemove(id, out _); }
    }

    private static object Wrap(string kind, object value) => new Dictionary<string, object> { [kind] = value };
    private static object Ok(long id) => Wrap("Ok", new { Id = id });
    private static object Error(long id, string reason) => Wrap("Error", new { Id = id, ErrorCode = 3, ErrorMessage = reason });
    private void Handle(JsonElement command, Session session, List<object> replies)
    {
        long id = 0;
        try
        {
            if (command.ValueKind != JsonValueKind.Object || command.EnumerateObject().Count() != 1) throw new JsonException();
            var item = command.EnumerateObject().First(); var value = item.Value;
            id = value.GetProperty("Id").GetInt64();
            if (id <= 0 || id > uint.MaxValue) throw new JsonException();
            if (item.Name == "RequestServerInfo")
            {
                var name = value.GetProperty("ClientName").GetString();
                if (string.IsNullOrWhiteSpace(name)) throw new JsonException();
                var version = value.TryGetProperty("ProtocolVersionMajor", out var major) ? major.GetInt32() : value.GetProperty("MessageVersion").GetInt32();
                if (version < 2 || version > 4) { replies.Add(Error(id, "Supported protocol versions: 2, 3, 4.0")); return; }
                session.Version = version; Interlocked.Exchange(ref session.LastPing, Environment.TickCount64);
                var ping = Math.Clamp(_s.IntegrationTimeoutMs, 1000, 30000);
                replies.Add(Wrap("ServerInfo", version == 4
                    ? new { Id = id, ServerName = "HapticLink event bridge", ProtocolVersionMajor = 4, ProtocolVersionMinor = 0, MaxPingTime = ping }
                    : (object)new { Id = id, ServerName = "HapticLink event bridge", MessageVersion = version, MaxPingTime = ping }));
                Note("Connected: " + name); return;
            }
            if (session.Version == 0) { replies.Add(Error(id, "Send RequestServerInfo first")); return; }
            switch (item.Name)
            {
                case "Ping": Interlocked.Exchange(ref session.LastPing, Environment.TickCount64); replies.Add(Ok(id)); return;
                case "RequestDeviceList":
                    replies.Add(Wrap("DeviceList", new { Id = id, Devices = session.Version == 4
                        ? new Dictionary<string, object> { ["0"] = Device(4) } : (object)new[] { Device(session.Version) } })); return;
                case "StartScanning":
                    replies.Add(Ok(id));
                    var device = Device(session.Version); device["Id"] = 0;
                    replies.Add(Wrap("DeviceAdded", device)); replies.Add(Wrap("ScanningFinished", new { Id = 0 })); return;
                case "StopScanning": replies.Add(Ok(id)); return;
                case "StopAllDevices": StopSession(session); replies.Add(Ok(id)); return;
                case "StopCmd" when session.Version == 4:
                    if (value.TryGetProperty("DeviceIndex", out var stoppedDevice) && stoppedDevice.ValueKind != JsonValueKind.Null && stoppedDevice.GetInt32() != 0) throw new JsonException();
                    if (value.TryGetProperty("FeatureIndex", out var stoppedFeature) && stoppedFeature.ValueKind != JsonValueKind.Null && stoppedFeature.GetInt32() != 0) throw new JsonException();
                    if (!value.TryGetProperty("Outputs", out var outputs) || outputs.GetBoolean()) StopSession(session);
                    replies.Add(Ok(id)); return;
            }
            if (value.GetProperty("DeviceIndex").GetInt32() != 0) { replies.Add(Error(id, "Unknown device index")); return; }
            double level;
            switch (item.Name)
            {
                case "StopDeviceCmd": StopSession(session); replies.Add(Ok(id)); return;
                case "ScalarCmd" when session.Version == 3:
                    var scalars = value.GetProperty("Scalars").EnumerateArray().ToArray();
                    if (scalars.Length != 1 || scalars[0].GetProperty("Index").GetInt32() != 0 || scalars[0].GetProperty("ActuatorType").GetString() != "Vibrate") throw new JsonException();
                    level = scalars[0].GetProperty("Scalar").GetDouble(); break;
                case "VibrateCmd" when session.Version <= 3:
                    var speeds = value.GetProperty("Speeds").EnumerateArray().ToArray();
                    if (speeds.Length != 1 || speeds[0].GetProperty("Index").GetInt32() != 0) throw new JsonException();
                    level = speeds[0].GetProperty("Speed").GetDouble(); break;
                case "OutputCmd" when session.Version == 4:
                    if (value.GetProperty("FeatureIndex").GetInt32() != 0) throw new JsonException();
                    var output = value.GetProperty("Command");
                    if (output.EnumerateObject().Count() != 1) throw new JsonException();
                    level = output.GetProperty("Vibrate").GetProperty("Value").GetDouble() / 20; break;
                default: replies.Add(Error(id, "This virtual device supports vibration only")); return;
            }
            if (!double.IsFinite(level) || level < 0 || level > 1) throw new JsonException();
            session.Level = level;
            Note("Integration vibration · " + level.ToString("P0"));
            replies.Add(Ok(id));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { replies.Add(Error(id, "Invalid command or unsupported actuator/value")); }
    }

    private static Dictionary<string, object> Device(int version)
    {
        var d = new Dictionary<string, object> { ["DeviceIndex"] = 0, ["DeviceName"] = "HapticLink virtual vibration" };
        if (version >= 3) d["DeviceMessageTimingGap"] = 0;
        if (version == 4)
            d["DeviceFeatures"] = new Dictionary<string, object> { ["0"] = new { FeatureDescription = "Mixed haptic input", FeatureIndex = 0, Output = new { Vibrate = new { Value = new[] { 0, 20 } } } } };
        else d["DeviceMessages"] = version == 3
            ? new Dictionary<string, object> { ["ScalarCmd"] = new[] { new { FeatureDescriptor = "Mixed haptic input", ActuatorType = "Vibrate", StepCount = 20 } }, ["StopDeviceCmd"] = new { } }
            : new Dictionary<string, object> { ["VibrateCmd"] = new { FeatureCount = 1, StepCount = new[] { 20 } }, ["StopDeviceCmd"] = new { } };
        return d;
    }

    private sealed record Cs2Snapshot(string Player, int Health, int Kills, int Deaths, string Weapon, int Ammo, string Round, string WinTeam, string Team);
    private void ProcessCs2(JsonElement root, string profileId = null)
    {
        lock (_gsiGate)
        {
            var key = profileId ?? "";
            if (!root.TryGetProperty("player", out var player) || !player.TryGetProperty("state", out var state)) { _cs2.Remove(key); return; }
            string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "";
            int Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : 0;
            var own = root.TryGetProperty("provider", out var provider) ? Str(provider, "steamid") : "";
            var id = Str(player, "steamid");
            if (id.Length == 0 || own.Length == 0 || own != id || Str(player, "activity") != "playing") { _cs2.Remove(key); return; }
            var stats = player.TryGetProperty("match_stats", out var m) ? m : default;
            var round = root.TryGetProperty("round", out var r) ? r : default;
            string weapon = ""; int ammo = 0;
            if (player.TryGetProperty("weapons", out var weapons))
                foreach (var item in weapons.EnumerateObject()) if (Str(item.Value, "state") == "active") { weapon = Str(item.Value, "name"); ammo = Num(item.Value, "ammo_clip"); }
            var current = new Cs2Snapshot(id, Num(state, "health"), stats.ValueKind == JsonValueKind.Object ? Num(stats, "kills") : 0,
                stats.ValueKind == JsonValueKind.Object ? Num(stats, "deaths") : 0, weapon, ammo,
                round.ValueKind == JsonValueKind.Object ? Str(round, "phase") : "", round.ValueKind == JsonValueKind.Object ? Str(round, "win_team") : "", Str(player, "team"));
            _cs2.TryGetValue(key, out var last); _cs2[key] = current;
            if (last == null || last.Player != id) return;
            if (current.Kills > last.Kills) Trigger("kill", profileId);
            if (current.Deaths > last.Deaths || current.Health == 0 && last.Health > 0) Trigger("death", profileId);
            else if (current.Health < last.Health && last.Health > 0) Trigger("damage", profileId);
            if (current.Weapon.Length > 0 && current.Weapon == last.Weapon && current.Ammo < last.Ammo) Trigger("shot", profileId);
            if (current.Round == "over" && last.Round != "over" && current.WinTeam.Length > 0 && current.WinTeam == current.Team) Trigger("round_win", profileId);
        }
    }
    public override void Dispose() { _plugins.Changed -= ProfileChanged; base.Dispose(); }
    public override void Stop()
    {
        _cts?.Cancel(); foreach (var session in _clients.Values) session.Socket.Abort();
        try { _runner?.Wait(3000); } catch { }
        _cts?.Dispose(); _cts = null; _runner = null; Running = false; Clear();
        lock (_gsiGate) _cs2.Clear();
        if (Available) Status = "Stopped";
    }
}
