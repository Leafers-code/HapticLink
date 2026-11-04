using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using HapticLink.Core;
using HapticLink.Sources;

internal static partial class Tests
{
    private const string CatalogFixture = """
        ## Applications
        ### Utilities
        - [A browser client](https://example.org/browser)
          - Web based, Cross platform, requires Intiface Central
        ## Game Mods
        ### FFXIV
        - [FFXIV Vibe Plugin](https://github.com/kaciexx/FFXIV_Vibe_Plugin)
          - Windows 10+ only, requires Intiface Central and Dalamud
        - [A renamed duplicate](https://github.com/kaciexx/FFXIV_Vibe_Plugin/)
          - requires Intiface Central
        - [Unsafe](javascript:bad)
        - [No connection documented](https://example.org/offline)
          - Standalone
        ### Overwatch 2
        - [OverStim](https://codeberg.org/pharmercy/OverStim)
          - Windows 10+ only, requires Intiface Central
        ## Development and Libraries
        - [A library](https://example.org/library)
        ## Deprecated Projects
        - [An abandoned project](https://example.org/abandoned)
        """;
    private static async Task TestCommunity(string home)
    {
        var root = Path.Combine(home, "community-" + Guid.NewGuid().ToString("N"));
        var catalog = new CommunityCatalog(root);
        Check(catalog.Snapshot.Entries.Count >= 60 && catalog.Snapshot.Entries.Any(e => e.Category == "FFXIV"), "offline community list includes game mods and FFXIV");
        foreach (var entry in catalog.Snapshot.Entries.Where(e => e.ListsIntiface)) PluginManager.Parse(PluginManager.Serialize(CommunityCatalog.Profile(entry)));
        Check(true, "every connectable bundled community entry creates a valid HapticLink profile");
        var parsed = CommunityCatalog.ParseMarkdown(CatalogFixture);
        Check(parsed.Entries.Count == 4 && parsed.Entries.Count(e => e.Section == "Apps") == 1 && parsed.Entries.All(e => !e.Url.Contains("abandoned") && !e.Url.Contains("library")), "discovery excludes deprecated projects, development libraries, unsafe links and duplicate URLs");
        var ffxiv = parsed.Entries.Single(e => e.Category == "FFXIV" && e.ListsIntiface);
        var profile = CommunityCatalog.Profile(ffxiv);
        Check(profile.Kind == "buttplug" && profile.InstallRepositoryUrl == CommunityCatalog.FfxivRepository && profile.Setup.Contains("/xlplugins") && profile.Setup.Contains("/fvp"), "FFXIV tab contains its actual Dalamud repository and in-game setup");
        var renamed = new CommunityEntry { Name = "Renamed", Url = ffxiv.Url.ToUpperInvariant() + "/" };
        Check(renamed.Id == ffxiv.Id && CommunityCatalog.Profile(parsed.Entries.Single(e => e.Category == "Overwatch 2")).Id == "overwatch2", "community identity survives renames and reuses the existing Overwatch tab");
        bool Rejected(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        Check(Rejected(() => CommunityCatalog.Profile(parsed.Entries.Single(e => e.Url.Contains("offline")))), "entries with undocumented connections offer a project guide rather than a pretend integration");
        var bad = PluginManager.Parse(PluginManager.Serialize(profile)); bad.SourceUrl = "file:///C:/private";
        Check(Rejected(() => PluginManager.Parse(PluginManager.Serialize(bad))), "plugin project links reject local and executable URLs");
        using var handler = new CatalogHandler(); using var http = new HttpClient(handler);
        await catalog.RefreshAsync(http);
        Check(handler.Url == CommunityCatalog.FeedUrl && catalog.Snapshot.Entries.Count == 4 && File.Exists(Path.Combine(root, "community-cache.json")), "refresh reads the fixed public source and atomically saves the new catalog");
        var cached = new CommunityCatalog(root);
        Check(cached.Snapshot.Entries.Count == 4 && cached.Origin == "Saved community list", "community refresh remains available offline after restart");
        handler.Body = "format changed";
        try { await catalog.RefreshAsync(http); throw new Exception("Accepted invalid list"); } catch (InvalidDataException) { }
        handler.Body = new string('x', 1024 * 1024 + 1);
        try { await catalog.RefreshAsync(http); throw new Exception("Accepted oversized list"); } catch (InvalidDataException) { }
        handler.Status = HttpStatusCode.Found;
        try { await catalog.RefreshAsync(http); throw new Exception("Accepted redirect"); } catch (IOException) { }
        Check(catalog.Snapshot.Entries.Count == 4 && new CommunityCatalog(root).Snapshot.Entries.Count == 4, "failed, oversized and redirected refreshes preserve the previous catalog");
        File.WriteAllText(Path.Combine(root, "community-cache.json"), "broken");
        Check(new CommunityCatalog(root).Snapshot.Entries.Count >= 60, "damaged local catalog falls back to the bundled list");
        await TestHostPortRouting(root, profile);
        new Settings().Save();
    }
    private static async Task TestHostPortRouting(string root, PluginProfile profile)
    {
        var s = new Settings(); var manager = new PluginManager(s, Path.Combine(root, "profiles")); manager.Install(PluginManager.Serialize(profile));
        manager.SetHostPortPlugin(profile.Id); s.Save();
        Check(Settings.Load().HostPortPluginId == profile.Id, "host / port assignment survives a settings round trip");
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); s.IntegrationPort = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var bridge = new IntegrationSource(s, manager); bridge.Start();
        for (int i = 0; i < 100 && !bridge.Running; i++) await Task.Delay(20);
        Check(bridge.Running, "host / port routing test starts a real loopback bridge");
        using var http = new HttpClient();
        var disabled = await http.GetAsync(bridge.Endpoint.Replace("ws:", "http:"));
        Check(disabled.StatusCode == HttpStatusCode.NotFound, "host / port clients cannot bypass a disabled target plugin");
        manager.SetEnabled(profile.Id, true); manager.SetGain(profile.Id, .5);
        var other = PluginManager.Parse(PluginManager.Serialize(profile)); other.Id = "separate-game"; manager.Install(PluginManager.Serialize(other)); manager.SetEnabled(other.Id, true);
        using var rootSocket = new ClientWebSocket(); using var separate = new ClientWebSocket();
        await rootSocket.ConnectAsync(new Uri(bridge.Endpoint), default); await separate.ConnectAsync(new Uri(manager.Endpoint(other)), default);
        async Task Exchange(ClientWebSocket socket, string json)
        {
            using var timeout = new CancellationTokenSource(3000); await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[4096]; var read = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (Encoding.UTF8.GetString(buffer, 0, read.Count).Contains("\"Error\"")) throw new Exception("Protocol exchange failed");
        }
        const string hello = "[{\"RequestServerInfo\":{\"Id\":1,\"ClientName\":\"Host port test\",\"MessageVersion\":3}}]";
        await Exchange(rootSocket, hello); await Exchange(separate, hello);
        await Exchange(rootSocket, "[{\"ScalarCmd\":{\"Id\":2,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.8,\"ActuatorType\":\"Vibrate\"}]}}]");
        await Exchange(separate, "[{\"ScalarCmd\":{\"Id\":2,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.3,\"ActuatorType\":\"Vibrate\"}]}}]");
        bridge.Tick(0);
        Check(bridge.ProfileClientCount(profile.Id) == 1 && Math.Abs(bridge.ProfileLevel(profile.Id) - .4) < .001 && Math.Abs(bridge.Raw - .4) < .001, "host / port-only mods reach the assigned tab and its intensity scale");
        var revision = bridge.StopRevision; manager.SetHostPortPlugin(other.Id); bridge.Tick(0);
        Check(bridge.StopRevision > revision && Math.Abs(bridge.Raw - .3) < .001, "switching host / port target stops its old input while preserving dedicated-path clients");
        manager.SetEnabled(other.Id, false);
        Check((await http.GetAsync(bridge.Endpoint.Replace("ws:", "http:"))).StatusCode == HttpStatusCode.NotFound, "disabled host / port targets do not fall back to shared unscoped input");
        manager.Uninstall(other.Id);
        Check((await http.GetAsync(bridge.Endpoint.Replace("ws:", "http:"))).StatusCode == HttpStatusCode.NotFound, "removed host / port targets stay blocked until explicitly reassigned");
        manager.SetHostPortPlugin(null);
        Check((await http.GetAsync(bridge.Endpoint.Replace("ws:", "http:"))).StatusCode == HttpStatusCode.BadRequest, "explicit shared-bridge selection restores the legacy root endpoint");
        rootSocket.Abort(); separate.Abort(); bridge.Stop();
    }
    private sealed class CatalogHandler : HttpMessageHandler
    {
        public string Body = CatalogFixture; public HttpStatusCode Status = HttpStatusCode.OK; public string Url;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }
}
