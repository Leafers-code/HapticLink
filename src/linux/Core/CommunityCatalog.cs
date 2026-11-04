using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HapticLink.Core;

public sealed class CommunityEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Section { get; set; } = "Games";
    public string Category { get; set; } = "Game mods";
    public string Details { get; set; } = "";
    public bool ListsIntiface { get; set; }
    [JsonIgnore] public string Id => CommunityCatalog.ProfileId(this);
    [JsonIgnore] public string Platform => Details.Contains("Linux only", StringComparison.OrdinalIgnoreCase) ? "Linux"
        : Details.Contains("Windows", StringComparison.OrdinalIgnoreCase) && !Details.Contains("Linux", StringComparison.OrdinalIgnoreCase) && !Details.Contains("cross platform", StringComparison.OrdinalIgnoreCase) ? "Windows"
        : Details.Contains("cross platform", StringComparison.OrdinalIgnoreCase) || Details.Contains("Windows/Linux", StringComparison.OrdinalIgnoreCase) ? "Cross platform" : "Check project";
    [JsonIgnore] public string SetupKind => Category == "FFXIV" ? "In-game · Dalamud"
        : Details.Contains("Web based", StringComparison.OrdinalIgnoreCase) ? "Website"
        : Id == "overwatch2" ? "Companion app" : Id == "cs2-events" ? "Game config"
        : Section == "Games" ? "Game mod / app" : "External app";
    [JsonIgnore] public string SearchText => Name + " " + Category + " " + Details + " " + SetupKind + " " + Platform;
}

public sealed class CommunitySnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset FetchedAtUtc { get; set; }
    public List<CommunityEntry> Entries { get; set; } = new();
}

/// <summary>Reads a single public project index as data. It never downloads or executes mod binaries.</summary>
public sealed class CommunityCatalog
{
    public const string SourcePage = "https://github.com/Renpona/awesome-intiface-sfw-list/blob/sfw-list/README.md";
    public const string FeedUrl = "https://raw.githubusercontent.com/Renpona/awesome-intiface-sfw-list/sfw-list/README.md";
    public const string FfxivRepository = "https://raw.githubusercontent.com/kaciexx/FFXIV_Vibe_Plugin/master/repo.json";
    private const int MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _cache;
    public CommunitySnapshot Snapshot { get; private set; } = new();
    public string Origin { get; private set; } = "Bundled list";
    public CommunityCatalog(string directory = null, string bundledPath = null)
    {
        _cache = Path.Combine(directory ?? Path.Combine(Settings.Dir, "plugins"), "community-cache.json");
        foreach (var path in new[] { _cache, bundledPath ?? Path.Combine(AppContext.BaseDirectory, "integrations", "community-catalog.json") })
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) continue;
                var snapshot = JsonSerializer.Deserialize<CommunitySnapshot>(File.ReadAllText(path), Json);
                Validate(snapshot); Snapshot = snapshot; Origin = path == _cache ? "Saved community list" : "Bundled list"; break;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        }
    }
    public async Task RefreshAsync(HttpClient client = null, CancellationToken cancellation = default)
    {
        using var ownClient = client == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) } : null;
        var http = client ?? ownClient;
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
        request.Headers.UserAgent.ParseAdd("HapticLink/1.5");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Community list could not be refreshed (HTTP " + (int)response.StatusCode + "). Your saved list is still available.");
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Community list is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var data = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (data.Length + count > MaxBytes) throw new InvalidDataException("Community list is too large.");
            data.Write(buffer, 0, count);
        }
        var snapshot = ParseMarkdown(Encoding.UTF8.GetString(data.ToArray()));
        Validate(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(_cache));
        var temporary = _cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, Json), cancellation); File.Move(temporary, _cache, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Snapshot = snapshot; Origin = "Refreshed community list";
    }
    public static CommunitySnapshot ParseMarkdown(string markdown)
    {
        if (Encoding.UTF8.GetByteCount(markdown) > MaxBytes) throw new InvalidDataException("Community list is too large.");
        var result = new CommunitySnapshot { FetchedAtUtc = DateTimeOffset.UtcNow };
        string section = "", category = ""; CommunityEntry current = null; var details = new StringBuilder();
        void Finish()
        {
            if (current == null) return;
            current.Details = Plain(details.ToString());
            if (current.Details.Length > 1800) current.Details = current.Details[..1800];
            current.ListsIntiface = current.Details.Contains("Intiface", StringComparison.OrdinalIgnoreCase) || current.Details.Contains("Buttplug", StringComparison.OrdinalIgnoreCase);
            if (PluginManager.ValidProjectUrl(current.Url) && current.Url.Length > 0 && current.Name.Length is > 0 and <= 100 && !result.Entries.Any(e => e.Id == current.Id)) result.Entries.Add(current);
            current = null; details.Clear();
        }
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Finish(); var heading = line[3..].Trim();
                section = heading == "Game Mods" ? "Games" : heading == "Applications" ? "Apps" : "";
                category = heading; continue;
            }
            if (line.StartsWith("### ", StringComparison.Ordinal)) { Finish(); category = Plain(line[4..]); continue; }
            if (section.Length == 0) continue;
            var item = Regex.Match(line, @"^- \[(?<name>[^\]\r\n]+)\]\((?<url>https://\S+?)\)\s*$", RegexOptions.CultureInvariant);
            if (item.Success)
            {
                Finish(); if (result.Entries.Count >= 256) throw new InvalidDataException("Community list contains too many projects.");
                current = new CommunityEntry { Name = Plain(item.Groups["name"].Value), Url = item.Groups["url"].Value.TrimEnd('/'), Section = section, Category = category }; continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal)) { Finish(); continue; }
            if (current != null && !string.IsNullOrWhiteSpace(line)) details.Append(' ').Append(line.Trim().TrimStart('-', ' '));
        }
        Finish(); Validate(result); return result;
    }
    private static string Plain(string text)
    {
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\s]+?\)", "$1");
        text = Regex.Replace(text, "<[^>]*>", "");
        return Regex.Replace(text.Replace("**", "").Replace("`", ""), @"\s+", " ").Trim();
    }
    private static void Validate(CommunitySnapshot snapshot)
    {
        if (snapshot?.SchemaVersion != 1 || snapshot.Entries == null || snapshot.Entries.Count is < 1 or > 256 || snapshot.Entries.Any(e => e == null || string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 100 || !PluginManager.ValidProjectUrl(e.Url) || string.IsNullOrEmpty(e.Url) || e.Details == null || e.Details.Length > 1800 || e.Section is not ("Games" or "Apps") || e.Category == null || e.Category.Length > 100) || snapshot.Entries.Select(e => e.Id).Distinct().Count() != snapshot.Entries.Count)
            throw new InvalidDataException("Community list format changed. Your saved list is still available.");
    }
    public static string ProfileId(CommunityEntry entry)
    {
        var url = entry.Url.TrimEnd('/').ToLowerInvariant();
        if (url == "https://github.com/gloss-click/cs2-buttplug") return "cs2-events";
        if (url == "https://codeberg.org/pharmercy/overstim") return "overwatch2";
        // The URL supplies identity so renamed projects keep their existing tab.
        return "community-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
    }
    public static PluginProfile Profile(CommunityEntry entry)
    {
        var bundled = PluginManager.Catalog().FirstOrDefault(p => p.Id == entry.Id);
        if (bundled != null) { bundled.SourceUrl = entry.Url; bundled.SetupKind = entry.SetupKind; return bundled; }
        if (!entry.ListsIntiface) throw new InvalidDataException("This project does not list an Intiface connection. Check its guide before adding a custom integration.");
        var ffxiv = entry.Url.TrimEnd('/').Equals("https://github.com/kaciexx/FFXIV_Vibe_Plugin", StringComparison.OrdinalIgnoreCase);
        return PluginManager.Parse(PluginManager.Serialize(new PluginProfile
        {
            Id = entry.Id, Name = entry.Name, TabName = entry.Name.Length > 24 ? entry.Name[..23] + "…" : entry.Name,
            Kind = "buttplug", SourceUrl = entry.Url, SetupKind = entry.SetupKind,
            InstallRepositoryUrl = ffxiv ? FfxivRepository : "",
            Description = entry.Category + " · Community integration. Vibration connection available; game behavior has not been tested by HapticLink.",
            Setup = ffxiv
                ? "1. In FFXIV QuickLauncher / Dalamud, open /xlplugins → Settings → Experimental → Custom Plugin Repositories. Copy the repository URL below, add it there, then save.\n2. Find FFXIV Vibe Plugin in that installer and install it. Open /fvp.\n3. Enable this HapticLink tab and choose Use host / port mode below. In /fvp, set IP to 127.0.0.1 and port to the number shown here, connect, and select HapticLink virtual vibration.\n4. Configure chat / spell / HP triggers in the in-game plugin. The project has free and paid features. Follow its guide for current game-version support."
                : "1. Open the project's install guide below. Install its game mod, mod loader or companion as directed.\n2. Enable this HapticLink tab. In the external integration's Intiface / Buttplug connection settings, paste the connection address below, connect, scan and select HapticLink virtual vibration.\n3. If it only offers separate host and port fields, choose Use host / port mode and enter the values shown here.\n4. Configure game triggers in the external integration. This catalog entry is not a live compatibility test; clients that require rotation, linear motion or sensor features need additional support."
        }));
    }
}
