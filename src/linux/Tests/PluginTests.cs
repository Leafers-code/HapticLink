using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HapticLink.Core;
using HapticLink.Sources;

internal static partial class Tests
{
    private static async Task TestPlugins(string home)
    {
        var root = Path.Combine(home, "profile-test-" + Guid.NewGuid().ToString("N"));
        var s = new Settings(); var manager = new PluginManager(s, root);
        var catalog = PluginManager.Catalog();
        Check(catalog.Length == 4 && catalog.All(p => PluginManager.Parse(PluginManager.Serialize(p)).Id == p.Id), "bundled integration profiles satisfy the import schema");
        Check(PluginManager.Parse("{\"schemaVersion\":1,\"id\":\"legacy-game\",\"name\":\"Legacy game\",\"version\":\"1.0.0\",\"kind\":\"http-events\",\"rules\":[]}").Name == "Legacy game", "existing version-one plugin exports remain importable");
        var custom = catalog.Single(p => p.Kind == "http-events"); custom.Id = "test-events";
        custom.Rules = new() { new() { Event = "damage", Level = .7, DurationMs = 1000, CooldownMs = 5000 } };
        manager.Install(PluginManager.Serialize(custom));
        Check(manager.Installed.Single().Enabled == false && !s.Events.Enabled, "installing a profile leaves it and the bridge disabled");
        manager.SetEnabled(custom.Id, true); s.Save();
        var persisted = new PluginManager(Settings.Load(), root);
        Check(persisted.Installed.Single().Enabled && persisted.GetEnabled(custom.Id).Rules[0].Level == .7, "installed profile and enabled choice survive reload");
        custom.Version = "1.1.0"; custom.Rules[0].Level = .8; manager.Install(PluginManager.Serialize(custom));
        Check(manager.Installed.Single().Enabled && PluginManager.Parse(manager.Export(custom.Id)).Version == "1.1.0", "profile update preserves enabled state and exports the new version");
        bool Reject(Action action) { try { action(); return false; } catch (Exception e) when (e is InvalidDataException or JsonException) { return true; } }
        var bad = PluginManager.Parse(manager.Export(custom.Id)); bad.Id = "../escape";
        Check(Reject(() => manager.Install(PluginManager.Serialize(bad))) && !File.Exists(Path.Combine(home, "escape.json")), "profile IDs cannot escape the installation folder");
        bad = PluginManager.Parse(manager.Export(custom.Id)); bad.Kind = "executable";
        Check(Reject(() => manager.Install(PluginManager.Serialize(bad))), "unsupported executable profile kinds are rejected");
        bad = PluginManager.Parse(manager.Export(custom.Id)); bad.SchemaVersion = 99;
        Check(Reject(() => manager.Install(PluginManager.Serialize(bad))), "unsupported profile schema is rejected");
        bad = PluginManager.Parse(manager.Export(custom.Id)); bad.Rules.Add(new() { Event = "DAMAGE" });
        Check(Reject(() => manager.Install(PluginManager.Serialize(bad))), "duplicate event names are rejected regardless of case");
        File.WriteAllText(Path.Combine(root, "installed", "broken.json"), "not JSON"); manager.Reload();
        Check(manager.Errors.Length == 1 && manager.Installed.Length == 1, "a malformed installed profile is reported without losing valid profiles");
        File.Delete(Path.Combine(root, "installed", "broken.json"));

        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); s.IntegrationPort = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var bridge = new IntegrationSource(s, manager); bridge.Start();
        for (int i = 0; i < 100 && !bridge.Running; i++) await Task.Delay(20);
        Check(bridge.Running, "profile integration routes start on the shared local bridge");
        using var http = new HttpClient();
        var endpoint = manager.Endpoint(custom);
        await http.PostAsJsonAsync(endpoint, new { @event = "damage" }); bridge.Tick(25);
        Check(Math.Abs(bridge.Raw - .8) < .001, "profile HTTP endpoint applies its installed event mapping");
        var unknown = await http.PostAsJsonAsync(endpoint, new { @event = "unknown" });
        var overrideLevel = await http.PostAsJsonAsync(endpoint, new { @event = "damage", level = 1 });
        Check(unknown.StatusCode == HttpStatusCode.BadRequest && overrideLevel.StatusCode == HttpStatusCode.BadRequest, "profile endpoints accept only installed mappings without caller overrides");
        await Task.Delay(1050); bridge.Tick(25);
        await http.PostAsJsonAsync(endpoint, new { @event = "DAMAGE" }); bridge.Tick(25);
        Check(bridge.Raw == 0, "profile event cooldown also applies to differently cased names");
        bridge.Clear();
        await http.PostAsJsonAsync(endpoint, new { @event = "damage" }); bridge.Tick(25);
        await Task.Delay(20);
        var other = PluginManager.Parse(manager.Export(custom.Id)); other.Id = "other-events"; other.Rules[0].Level = .3;
        manager.Install(PluginManager.Serialize(other)); manager.SetEnabled(other.Id, true);
        await http.PostAsJsonAsync(manager.Endpoint(other), new { @event = "damage" }); bridge.Tick(25);
        var revision = bridge.StopRevision; manager.SetEnabled(custom.Id, false); bridge.Tick(25);
        Check(Math.Abs(bridge.Raw - .3) < .001 && bridge.StopRevision > revision, "disabling one profile clears its pulse while preserving another profile");
        var disabled = await http.PostAsJsonAsync(endpoint, new { @event = "damage" });
        Check(disabled.StatusCode == HttpStatusCode.NotFound, "disabled profiles reject new HTTP events");
        var wrongKind = await http.PostAsJsonAsync(manager.Endpoint(other).Replace("/event", "/cs2"), new { });
        Check(wrongKind.StatusCode == HttpStatusCode.NotFound, "profile endpoints enforce their integration kind");
        var backup = manager.Uninstall(other.Id); bridge.Tick(25);
        Check(bridge.Raw == 0 && File.Exists(backup) && manager.GetEnabled(other.Id) == null, "removing an active profile stops it and retains a restorable copy");
        manager.Install(File.ReadAllText(backup));
        Check(manager.Installed.Single(p => p.Profile.Id == other.Id).Enabled == false, "restoring a removed profile does not silently enable it");

        var buttplug = catalog.Single(p => p.Id == "overwatch2"); manager.Install(PluginManager.Serialize(buttplug)); manager.SetEnabled(buttplug.Id, true);
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(new Uri(manager.Endpoint(buttplug)), default);
            async Task Exchange(string json)
            {
                using var timeout = new CancellationTokenSource(3000);
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[4096]; var response = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                using var parsed = JsonDocument.Parse(buffer.AsMemory(0, response.Count));
                if (parsed.RootElement[0].TryGetProperty("Error", out _)) throw new Exception("Profile protocol request failed");
            }
            await Exchange("[{\"RequestServerInfo\":{\"Id\":1,\"ClientName\":\"Profile test\",\"MessageVersion\":3}}]");
            await Exchange("[{\"ScalarCmd\":{\"Id\":2,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.6,\"ActuatorType\":\"Vibrate\"}]}}]");
            bridge.Tick(25); Check(Math.Abs(bridge.Raw - .6) < .001, "installed Buttplug profile receives vibration over its dedicated WebSocket");
            manager.SetGain(buttplug.Id, .5); bridge.Tick(25);
            Check(Math.Abs(bridge.Raw - .3) < .001 && Math.Abs(bridge.ProfileLevel(buttplug.Id) - .3) < .001, "game-specific intensity scale applies live to the incoming vibration");
            manager.SetEnabled(buttplug.Id, false); bridge.Tick(25);
            Check(bridge.Raw == 0, "disabling a Buttplug profile immediately clears held vibration"); socket.Abort();
        }
        var cs2 = catalog.Single(p => p.Kind == "cs2"); manager.Install(PluginManager.Serialize(cs2)); manager.SetEnabled(cs2.Id, true);
        object Frame(int kills) => new { provider = new { steamid = "own" }, player = new { steamid = "own", activity = "playing", team = "CT", state = new { health = 100 }, match_stats = new { kills, deaths = 0 } }, round = new { phase = "live" } };
        await http.PostAsJsonAsync(manager.Endpoint(cs2), Frame(3)); bridge.Tick(25);
        Check(bridge.Raw == 0, "CS2 profile establishes its own baseline without firing");
        await http.PostAsJsonAsync(manager.Endpoint(cs2), Frame(4)); bridge.Tick(25);
        Check(bridge.Raw > .79 && bridge.LastEvent.Contains(cs2.Id), "CS2 profile maps a real snapshot delta through its own rules");
        var config = manager.Cs2Config(cs2);
        Check(config.Contains(manager.Endpoint(cs2)) && config.Contains("player_weapons"), "exported CS2 game config targets the profile and current bridge port");
        manager.SetEnabled(cs2.Id, false); manager.SetEnabled(cs2.Id, true);
        await http.PostAsJsonAsync(manager.Endpoint(cs2), Frame(20)); bridge.Tick(25);
        Check(bridge.Raw == 0, "re-enabling a CS2 profile starts a fresh baseline");
        bridge.Stop();
        var gameSettings = new Settings(); var games = new PluginManager(gameSettings, Path.Combine(root, "bundled-games")); games.EnsureGameDefaults();
        Check(games.Installed.Length == 2 && games.Installed.All(g => !g.Enabled) && games.Installed.Any(g => g.Profile.Companion == "overstim"), "CS2 and Overwatch game plugins are preconfigured without being enabled");
        games.Uninstall("overwatch2"); games.EnsureGameDefaults();
        Check(games.Installed.Length == 1, "removed bundled games are not reinstalled on every startup");
        var ini = GameIntegrationSetup.OverStimIni("[config]\nwebsocket_address=ws://localhost:12345\nmonitor_id=2\n[other]\nkeep=true\n", "ws://127.0.0.1:12346/plugins/overwatch2");
        Check(ini.Contains("websocket_address=ws://127.0.0.1:12346/plugins/overwatch2") && ini.Contains("monitor_id=2") && ini.Contains("[other]\nkeep=true") && ini.Contains("read_sensors=false"), "OverStim setup changes the connection while preserving other settings");
        var cfgDirectory = Path.Combine(root, "game-cfg"); Directory.CreateDirectory(cfgDirectory);
        var configFile = GameIntegrationSetup.InstallCs2Config(cfgDirectory, "old config"); GameIntegrationSetup.InstallCs2Config(cfgDirectory, config);
        Check(File.ReadAllText(configFile) == config && Directory.GetFiles(cfgDirectory, "*.backup-*").Any(f => File.ReadAllText(f) == "old config"), "CS2 config installer writes the ready-made file and preserves an existing config");
        // Restore the GUI fixture without enabling local backends.
        new Settings().Save();
    }
}
