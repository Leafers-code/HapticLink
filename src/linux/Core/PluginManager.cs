using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HapticLink.Core;

public sealed class PluginProfile
{
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "http-events";
    public string Setup { get; set; } = "";
    public string TabName { get; set; } = "";
    public string Companion { get; set; } = "";
    public List<GameEventRule> Rules { get; set; } = new();
}

public sealed record InstalledPlugin(PluginProfile Profile, bool Enabled);

/// <summary>Installs declarative integration profiles; no game binaries are executed by the host.</summary>
public sealed class PluginManager
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly object _gate = new();
    private readonly Settings _settings;
    private readonly string _directory;
    private readonly Dictionary<string, PluginProfile> _installed = new(StringComparer.Ordinal);
    private readonly List<string> _errors = new();
    public event Action<string> Changed;
    public PluginManager(Settings settings, string directory = null)
    {
        _settings = settings; _directory = directory ?? Path.Combine(Settings.Dir, "plugins");
        _settings.PluginEnabled ??= new();
        _settings.PluginGains ??= new(); _settings.PluginApplications ??= new();
        Reload();
    }
    public InstalledPlugin[] Installed { get { lock (_gate) return _installed.Values.OrderBy(p => p.Name).Select(p => new InstalledPlugin(Clone(p), Enabled(p.Id))).ToArray(); } }
    public string[] Errors { get { lock (_gate) return _errors.ToArray(); } }
    private bool Enabled(string id) => _settings.PluginEnabled.TryGetValue(id, out var value) && value;
    public double Gain(string id) => _settings.PluginGains.TryGetValue(id, out var gain) && double.IsFinite(gain) ? Math.Clamp(gain, 0, 2) : 1;
    public void SetGain(string id, double gain) => _settings.PluginGains = new(_settings.PluginGains) { [id] = double.IsFinite(gain) ? Math.Clamp(gain, 0, 2) : 1 };
    public static string TabTitle(PluginProfile p) => p.Id == "cs2-events" ? "CS2" : p.Id == "overwatch2" ? "Overwatch 2" : !string.IsNullOrWhiteSpace(p.TabName) ? p.TabName : p.Name.Length > 18 ? p.Name[..18] + "…" : p.Name;
    public void EnsureGameDefaults()
    {
        if (_settings.GamePluginsInitialized) return;
        foreach (var profile in Catalog().Where(p => p.Id is "cs2-events" or "overwatch2"))
            if (!Installed.Any(p => p.Profile.Id == profile.Id)) Install(Serialize(profile));
        _settings.GamePluginsInitialized = true; _settings.Save();
    }
    public PluginProfile GetEnabled(string id, string kind = null)
    {
        lock (_gate) return _installed.TryGetValue(id, out var p) && Enabled(id) && (kind == null || kind == p.Kind) ? Clone(p) : null;
    }
    public void Reload()
    {
        string[] previous;
        lock (_gate)
        {
            previous = _installed.Keys.ToArray(); _installed.Clear(); _errors.Clear();
            var path = Path.Combine(_directory, "installed");
            string[] files = Array.Empty<string>();
            try { if (Directory.Exists(path)) files = Directory.EnumerateFiles(path, "*.json").Take(128).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _errors.Add("Cannot read installed profiles: " + ex.Message); }
            foreach (var file in files)
                try
                {
                    if (_installed.Count >= 64) throw new InvalidDataException("At most 64 profiles can be loaded.");
                    if (new FileInfo(file).Length > 131072) throw new InvalidDataException("Profile exceeds 128 KiB.");
                    var profile = Parse(File.ReadAllText(file));
                    if (Path.GetFileName(file) != profile.Id + ".json") throw new InvalidDataException("Profile ID does not match its filename.");
                    _installed.Add(profile.Id, profile);
                }
                catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { _errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
        }
        foreach (var id in previous) Changed?.Invoke(id);
    }
    public PluginProfile Install(string json)
    {
        var profile = Parse(json);
        lock (_gate)
        {
            if (_installed.Count >= 64 && !_installed.ContainsKey(profile.Id)) throw new InvalidDataException("At most 64 profiles can be installed.");
            var directory = Path.Combine(_directory, "installed"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, profile.Id + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(profile)); File.Move(temporary, path, overwrite: true);
            _installed[profile.Id] = profile;
            // New profiles are installed disabled; updates preserve the existing choice.
            if (!_settings.PluginEnabled.ContainsKey(profile.Id)) SetFlag(profile.Id, false);
        }
        Changed?.Invoke(profile.Id); return Clone(profile);
    }
    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate) { if (!_installed.ContainsKey(id)) throw new InvalidDataException("Profile is not installed."); SetFlag(id, enabled); }
        Changed?.Invoke(id);
    }
    private void SetFlag(string id, bool enabled) => _settings.PluginEnabled = new(_settings.PluginEnabled, StringComparer.Ordinal) { [id] = enabled };
    public string Uninstall(string id)
    {
        string backup;
        lock (_gate)
        {
            if (!_installed.ContainsKey(id)) throw new InvalidDataException("Profile is not installed.");
            var removed = Path.Combine(_directory, "removed"); Directory.CreateDirectory(removed);
            backup = Path.Combine(removed, id + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.Move(Path.Combine(_directory, "installed", id + ".json"), backup);
            _installed.Remove(id); SetFlag(id, false);
        }
        Changed?.Invoke(id); return backup;
    }
    public string Export(string id)
    {
        lock (_gate) return _installed.TryGetValue(id, out var p) ? Serialize(p) : throw new InvalidDataException("Profile is not installed.");
    }
    public static string Serialize(PluginProfile profile) => JsonSerializer.Serialize(profile, Json);
    private static PluginProfile Clone(PluginProfile p) => JsonSerializer.Deserialize<PluginProfile>(Serialize(p), Json);
    public static PluginProfile Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 131072) throw new InvalidDataException("Profile exceeds 128 KiB.");
        var p = JsonSerializer.Deserialize<PluginProfile>(json, Json) ?? throw new InvalidDataException("Empty profile.");
        if (p.SchemaVersion is not (1 or 2)) throw new InvalidDataException("Unsupported profile schema version.");
        if (p.TabName == null || p.TabName.Length > 24 || p.Companion is not ("" or "overstim")) throw new InvalidDataException("Invalid game tab or companion.");
        if (!Regex.IsMatch(p.Id ?? "", "^[a-z0-9][a-z0-9-]{2,63}$") || Regex.IsMatch(p.Id, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$")) throw new InvalidDataException("Use a 3–64 character lowercase ID with letters, digits and hyphens.");
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100 || !Version.TryParse(p.Version, out _)) throw new InvalidDataException("A name and numeric version are required.");
        if (p.Kind is not ("http-events" or "cs2" or "buttplug")) throw new InvalidDataException("Supported profile kinds: http-events, cs2, buttplug.");
        if (p.Description == null || p.Description.Length > 1000 || p.Setup == null || p.Setup.Length > 8000 || p.Rules == null || p.Rules.Count > 64) throw new InvalidDataException("Profile text or rule count is too large.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in p.Rules)
            if (rule == null || string.IsNullOrWhiteSpace(rule.Event) || rule.Event.Length > 80 || !names.Add(rule.Event) || !double.IsFinite(rule.Level) || rule.Level < 0 || rule.Level > 1 || rule.DurationMs < 0 || rule.DurationMs > 5000 || rule.CooldownMs < 0 || rule.CooldownMs > 10000) throw new InvalidDataException("Invalid or duplicate event rule.");
        return p;
    }
    public string Endpoint(PluginProfile profile, int? activePort = null)
        => $"{(profile.Kind == "buttplug" ? "ws" : "http")}://127.0.0.1:{activePort ?? _settings.IntegrationPort}/plugins/{profile.Id}" + (profile.Kind == "buttplug" ? "" : profile.Kind == "cs2" ? "/cs2" : "/event");
    public string Cs2Config(PluginProfile profile)
    {
        if (profile.Kind != "cs2") throw new InvalidDataException("This profile is not a CS2 integration.");
        return "\"HapticLink\"\n{\n  \"uri\" \"" + Endpoint(profile) + "\"\n  \"timeout\" \"5.0\"\n  \"buffer\" \"0.1\"\n  \"throttle\" \"0.1\"\n  \"heartbeat\" \"5.0\"\n  \"data\"\n  {\n    \"provider\" \"1\"\n    \"player_id\" \"1\"\n    \"player_state\" \"1\"\n    \"player_weapons\" \"1\"\n    \"player_match_stats\" \"1\"\n    \"round\" \"1\"\n  }\n}\n";
    }
    public static PluginProfile[] Catalog() => new[]
    {
        new PluginProfile { Id = "cs2-events", Name = "Counter-Strike 2", TabName = "CS2", Kind = "cs2", Description = "Ready-made kill, damage, death, shot and round-win effects via Game State Integration.", Setup = "Install the game config into CS2's game/csgo/cfg folder, then restart the game. Enable this plugin to receive events.", Rules = new Settings().EventRules },
        new PluginProfile { Id = "overwatch2", Name = "Overwatch 2", TabName = "Overwatch 2", Kind = "buttplug", Companion = "overstim", Description = "Overwatch event haptics through the OverStim screen-detection companion.", Setup = "Get OverStim from its official project, close it, and choose Configure OverStim below. HapticLink sets its server address automatically. Launch OverStim, select your hero and start its detector. The game detector and hero-specific triggers run in OverStim." },
        new PluginProfile { Id = "intiface-vibration", Name = "Intiface / Buttplug integration", Kind = "buttplug", Description = "Receive vibration from compatible apps and game mods through a dedicated profile endpoint.", Setup = "Set your integration's server URL to the address below. Connect, scan and select HapticLink virtual vibration. The game mod or app must be installed separately." },
        new PluginProfile { Id = "http-game-events", Name = "Custom game events", Kind = "http-events", Description = "Map named events from a game adapter or script to bounded haptic pulses.", Setup = "POST JSON such as {\"event\":\"quest_complete\"} to the address below. Export this profile to edit event names, levels, durations and cooldowns, then import it again.", Rules = new() { new() { Event = "quest_complete", Level = .6, DurationMs = 500 }, new() { Event = "damage", Level = .4, DurationMs = 250 } } }
    };
}
