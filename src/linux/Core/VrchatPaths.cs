using System.Diagnostics;

namespace HapticLink.Core;

internal static class VrchatPaths
{
    public static IEnumerable<string> OscDirectories(Settings s)
    {
        if (!string.IsNullOrWhiteSpace(s.VrchatOscDirectory)) { yield return s.VrchatOscDirectory; yield break; }
        if (!OperatingSystem.IsLinux())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "VRChat", "VRChat", "OSC");
            yield break;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamRoots = new List<string> {
            Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"),
            Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam") };
        foreach (var steam in steamRoots.ToArray())
        {
            var vdf = Path.Combine(steam, "steamapps/libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(vdf), "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\""))
                steamRoots.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }
        foreach (var steam in steamRoots.Distinct())
            yield return Path.Combine(steam, "steamapps/compatdata/438100/pfx/drive_c/users/steamuser/AppData/LocalLow/VRChat/VRChat/OSC");
    }

    public static IEnumerable<int> QueryPorts(Settings s)
    {
        if (s.OscQueryPort > 0) { yield return s.OscQueryPort; yield break; }
        if (!OperatingSystem.IsLinux()) yield break;
        var ports = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!process.ProcessName.StartsWith("VRChat", StringComparison.OrdinalIgnoreCase)) continue;
                    var sockets = Directory.EnumerateFiles($"/proc/{process.Id}/fd")
                        .Select(path => new FileInfo(path).LinkTarget)
                        .Where(target => target?.StartsWith("socket:[") == true)
                        .Select(target => target[8..^1]).ToHashSet();
                    foreach (var file in new[] { $"/proc/{process.Id}/net/tcp", $"/proc/{process.Id}/net/tcp6" })
                    {
                        if (!File.Exists(file)) continue;
                        foreach (var row in File.ReadLines(file).Skip(1))
                        {
                            var cells = row.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                            if (cells.Length < 10 || cells[3] != "0A" || !sockets.Contains(cells[9])) continue;
                            ports.Add(Convert.ToInt32(cells[1].Split(':')[1], 16));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        foreach (var port in ports) yield return port;
    }
}
