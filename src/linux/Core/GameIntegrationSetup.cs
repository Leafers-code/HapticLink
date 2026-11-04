using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HapticLink.Core;

public static class GameIntegrationSetup
{
    public const string OverStimProject = "https://codeberg.org/pharmercy/OverStim";
    public static string InstallCs2Config(string cfgDirectory, string config)
    {
        var directory = Path.GetFullPath(cfgDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Choose CS2's game/csgo/cfg folder.");
        var file = Path.Combine(directory, "gamestate_integration_hapticlink_cs2-events.cfg");
        if (File.Exists(file)) File.Copy(file, file + ".backup-" + DateTime.UtcNow.Ticks);
        File.WriteAllText(file, config); return file;
    }
    public static string[] FindCs2CfgDirectories()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steam) roots.Add(steam);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        }
        else
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(user, ".steam", "steam")); roots.Add(Path.Combine(user, ".local", "share", "Steam"));
        }
        foreach (var root in roots.ToArray())
            try
            {
                var libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraries)) roots.AddRange(ParseSteamLibraries(File.ReadAllText(libraries)));
            }
            catch (IOException) { }
        return roots.Distinct().Select(p => Path.Combine(p, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg")).Where(Directory.Exists).ToArray();
    }
    public static string[] ParseSteamLibraries(string vdf) => Regex.Matches(vdf, "\"path\"\\s*\"((?:\\\\.|[^\"])*)\"")
        .Select(m => m.Groups[1].Value.Replace("\\\\", "\\")).ToArray();
    public static string ConfigureOverStim(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "ws" || uri.Host != "127.0.0.1") throw new ArgumentException("Expected HapticLink's local WebSocket address.");
        var running = Process.GetProcessesByName("OverStim");
        try { if (running.Length > 0) throw new InvalidOperationException("Close OverStim before configuring its connection."); }
        finally { foreach (var process in running) process.Dispose(); }
        var backups = Path.Combine(Settings.Dir, "companion-backups"); Directory.CreateDirectory(backups);
        var backup = Path.Combine(backups, "overstim-" + DateTime.UtcNow.Ticks);
        // Qt QSettings: organization/application are both OverStim; config keys verified upstream.
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\OverStim\OverStim\config");
            var old = new Dictionary<string, string>();
            foreach (var name in new[] { "websocket_address", "using_intiface", "read_sensors" }) old[name] = key.GetValue(name)?.ToString();
            File.WriteAllText(backup + ".json", JsonSerializer.Serialize(old));
            key.SetValue("websocket_address", endpoint, RegistryValueKind.String);
            key.SetValue("using_intiface", "true", RegistryValueKind.String);
            key.SetValue("read_sensors", "false", RegistryValueKind.String);
        }
        else if (OperatingSystem.IsLinux())
        {
            var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var file = Path.Combine(root, "OverStim", "OverStim.conf");
            var previous = File.Exists(file) ? File.ReadAllText(file) : "";
            File.WriteAllText(backup + ".conf", previous); Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, OverStimIni(previous, endpoint));
        }
        else throw new PlatformNotSupportedException("Companion setup supports Windows and Linux.");
        return backup;
    }
    public static string OverStimIni(string original, string endpoint)
    {
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
        var section = lines.FindIndex(l => l.Trim().Equals("[config]", StringComparison.OrdinalIgnoreCase));
        if (section < 0) { if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(""); section = lines.Count; lines.Add("[config]"); }
        var end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith('[')); if (end < 0) end = lines.Count;
        var replacements = new Dictionary<string, string> { ["websocket_address"] = endpoint, ["using_intiface"] = "true", ["read_sensors"] = "false" };
        foreach (var item in replacements)
        {
            var index = lines.FindIndex(section + 1, end - section - 1, l => l.Split('=', 2)[0].Trim().Equals(item.Key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) lines[index] = item.Key + "=" + item.Value;
            else { lines.Insert(end++, item.Key + "=" + item.Value); }
        }
        return string.Join("\n", lines).TrimEnd() + "\n";
    }
}
