using System.Diagnostics;
using System.Text.Json;
using HapticLink.Core;

namespace HapticLink.Sources;

/// <summary>Owns an evdev/uinput helper; no Windows drivers or Wine are involved.</summary>
public sealed class RumbleSource : SourceBase
{
    public override string Name => "Rumble";
    private readonly Settings _s;
    private Process _process;
    private Task _reader;
    private volatile int _strong, _weak;
    private DateTime _lastData;
    public RumbleSource(Settings s) => _s = s;
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "linux", "rumble_bridge.py");

    public static List<(string Id, string Name)> ListDevices(Settings s)
    {
        try
        {
            using var doc = JsonDocument.Parse(LinuxProcess.Run(s.PythonCommand, Helper, "--list"));
            return doc.RootElement.EnumerateArray().Select(x =>
                (x.GetProperty("path").GetString(), x.GetProperty("name").GetString())).ToList();
        }
        catch (Exception ex) { Log.Write("Controller list: " + ex.Message); return new(); }
    }

    public override void Start()
    {
        if (_process != null) Stop();
        if (!OperatingSystem.IsLinux()) { Available = false; Status = "Linux controller backend"; return; }
        try
        {
            var args = new List<string> { Helper };
            if (!string.IsNullOrWhiteSpace(_s.RumbleDevice)) args.AddRange(new[] { "--device", _s.RumbleDevice });
            if (_s.RumbleGrab) args.Add("--grab");
            if (!_s.RumblePassthrough) args.Add("--no-passthrough");
            _process = LinuxProcess.Start(_s.PythonCommand, args.ToArray());
            var process = _process;
            Available = Running = true;
            Status = "Starting virtual controller…";
            _reader = Task.Run(async () =>
            {
                var errors = process.StandardError.ReadToEndAsync();
                try
                {
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("status", out var status)) Status = status.GetString();
                        if (root.TryGetProperty("error", out var error)) { Status = error.GetString(); Available = false; }
                        if (root.TryGetProperty("strong", out var strong))
                        {
                            _strong = strong.GetInt32(); _weak = root.GetProperty("weak").GetInt32();
                            _lastData = DateTime.UtcNow;
                        }
                    }
                    var stderr = (await errors).Trim();
                    if (stderr.Length > 0) Status = "Controller: " + stderr;
                }
                catch (Exception ex) { Log.Write("Controller bridge: " + ex.Message); }
                finally { Running = false; Raw = 0; _strong = _weak = 0; }
            });
        }
        catch (Exception ex) { Available = Running = false; Status = "Controller bridge failed: " + ex.Message; }
    }

    public override void Tick(double dtMs) => Raw = Running && (DateTime.UtcNow - _lastData).TotalMilliseconds < 350
        ? Sig.Clamp01(Math.Max(_strong / 65535d * _s.RumbleLargeWeight, _weak / 65535d * _s.RumbleSmallWeight)) : 0;

    public override void Stop()
    {
        try { _process?.StandardInput.Close(); } catch { }
        try { if (_process is { HasExited: false } && !_process.WaitForExit(1000)) _process.Kill(true); } catch { }
        try { _reader?.Wait(1500); } catch { }
        _process?.Dispose(); _process = null;
        Running = false; Raw = 0; _strong = _weak = 0;
        if (Available) Status = "Stopped";
    }
}
