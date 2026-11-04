using System.Text.Json;

namespace HapticLink.Core;

public sealed record AudioApplication(int Stream, int ProcessId, string Name, int Sink)
{
    public override string ToString() => $"{Name} · PID {ProcessId} · stream {Stream}";
}
public sealed record AudioTarget(string Monitor, int? Stream, string Label);
public static class AudioRouting
{
    public static List<AudioApplication> Applications() => ParseApplications(LinuxProcess.Run("pactl", "--format=json", "list", "sink-inputs"));
    public static List<AudioApplication> ParseApplications(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<AudioApplication>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("properties", out var props)) continue;
            if (!props.TryGetProperty("application.process.id", out var p) || !int.TryParse(p.ToString(), out var pid)) continue;
            var name = props.TryGetProperty("application.name", out var n) ? n.GetString() : "Application";
            result.Add(new(entry.GetProperty("index").GetInt32(), pid, name, entry.GetProperty("sink").GetInt32()));
        }
        return result;
    }
    public static AudioTarget Resolve(Settings s, GameFocus focus)
    {
        if (s.AudioCaptureMode == AudioCaptureMode.OutputMonitor)
        {
            var monitor = string.IsNullOrWhiteSpace(s.AudioDeviceId) ? LinuxProcess.Run("pactl", "get-default-sink").Trim() + ".monitor" : s.AudioDeviceId;
            return new(monitor, null, monitor);
        }
        var pid = s.AudioCaptureMode == AudioCaptureMode.SelectedGame ? focus.State.Window?.ProcessId ?? 0 : s.AudioProcessId;
        if (pid <= 0) return null;
        var applications = Applications().Where(a => a.ProcessId == pid).ToList();
        var stream = s.AudioCaptureMode == AudioCaptureMode.Application ? applications.FirstOrDefault(a => a.Stream == s.AudioStreamIndex) ?? applications.FirstOrDefault() : applications.FirstOrDefault();
        if (stream == null) return null; // Never fall back to capturing the whole output.
        using var sinks = JsonDocument.Parse(LinuxProcess.Run("pactl", "--format=json", "list", "sinks"));
        foreach (var sink in sinks.RootElement.EnumerateArray())
            if (sink.GetProperty("index").GetInt32() == stream.Sink)
                return new(sink.GetProperty("name").GetString() + ".monitor", stream.Stream, stream.Name);
        return null;
    }
}
