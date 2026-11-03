using System.Diagnostics;
using System.Text.Json;
using HapticLink.Core;

namespace HapticLink.Sources;

/// <summary>Output-monitor capture through PulseAudio or PipeWire's PulseAudio server.</summary>
public sealed class AudioSource : SourceBase
{
    public override string Name => "Audio";
    private readonly Settings _s;
    private Process _process;
    private CancellationTokenSource _cts;
    private Task _reader;
    private DateTime _lastData;
    private readonly double[] _lp1 = new double[2], _lp2 = new double[2], _hp = new double[2];
    public AudioSource(Settings s) => _s = s;

    public static List<(string Id, string Name)> ListDevices()
    {
        try
        {
            using var doc = JsonDocument.Parse(LinuxProcess.Run("pactl", "--format=json", "list", "sources"));
            return doc.RootElement.EnumerateArray()
                .Where(x => x.GetProperty("name").GetString().EndsWith(".monitor", StringComparison.Ordinal) ||
                    (x.TryGetProperty("monitor_of_sink", out var m) && m.ValueKind != JsonValueKind.Null &&
                     m.ToString() != "4294967295" && m.ToString() != "n/a"))
                .Select(x => (x.GetProperty("name").GetString(), x.GetProperty("description").GetString())).ToList();
        }
        catch (Exception ex) { Log.Write("Audio monitors: " + ex.Message); return new(); }
    }

    public override void Start()
    {
        if (_process != null) Stop();
        if (!OperatingSystem.IsLinux()) { Available = false; Status = "Linux audio backend"; return; }
        try
        {
            var monitor = _s.AudioDeviceId;
            if (string.IsNullOrWhiteSpace(monitor))
            {
                var sink = LinuxProcess.Run("pactl", "get-default-sink").Trim();
                if (string.IsNullOrEmpty(sink)) throw new InvalidOperationException("No default audio output");
                monitor = sink + ".monitor";
            }
            _cts = new();
            _process = LinuxProcess.Start("parec", "--raw", "--format=float32le", "--rate=48000", "--channels=2",
                "--latency-msec=20", "--device=" + monitor);
            var process = _process;
            var ct = _cts.Token;
            Available = Running = true;
            Status = "Listening: " + monitor;
            _reader = Task.Run(async () =>
            {
                var errors = process.StandardError.ReadToEndAsync();
                try
                {
                    var buffer = new byte[3840];
                    while (!ct.IsCancellationRequested)
                    {
                        await process.StandardOutput.BaseStream.ReadExactlyAsync(buffer, ct);
                        ProcessSamples(buffer);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Running = false; Raw = 0;
                        var error = errors.IsCompleted ? (await errors).Trim() : ex.Message;
                        Status = "Audio stopped: " + error;
                        Log.Write(Status);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Available = Running = false; Raw = 0;
            Status = "Audio: install pulseaudio-utils (parec + pactl). " + ex.Message;
        }
    }

    internal void ProcessSamples(byte[] buffer)
    {
        double sum = 0;
        double lp = Coef(180), hp = Coef(2500);
        for (int off = 0; off + 8 <= buffer.Length; off += 8)
            for (int c = 0; c < 2; c++)
            {
                double sample = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(off + c * 4, 4));
                if (!double.IsFinite(sample)) sample = 0;
                double value;
                switch (_s.AudioBand)
                {
                    case AudioBand.Bass:
                        _lp1[c] += (sample - _lp1[c]) * lp;
                        _lp2[c] += (_lp1[c] - _lp2[c]) * lp;
                        value = _lp2[c]; break;
                    case AudioBand.Treble:
                        _hp[c] += (sample - _hp[c]) * hp;
                        value = sample - _hp[c]; break;
                    default: value = sample; break;
                }
                sum += value * value;
            }
        var rms = Math.Sqrt(sum / Math.Max(1, buffer.Length / 4));
        var db = 20 * Math.Log10(rms + 1e-9);
        Raw = Sig.Clamp01((db - _s.AudioFloorDb) / Math.Max(1, _s.AudioCeilDb - _s.AudioFloorDb));
        _lastData = DateTime.UtcNow;
    }

    private static double Coef(double cutoff) { var dt = 1d / 48000; return dt / (1 / (2 * Math.PI * cutoff) + dt); }
    public override void Tick(double dtMs) { if ((DateTime.UtcNow - _lastData).TotalMilliseconds > 300) Raw = 0; }
    public override void Stop()
    {
        _cts?.Cancel();
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { _reader?.Wait(1500); } catch { }
        _process?.Dispose(); _process = null;
        _cts?.Dispose(); _cts = null;
        Running = false; Raw = 0;
        Array.Clear(_lp1); Array.Clear(_lp2); Array.Clear(_hp);
        if (Available) Status = "Stopped";
    }
}
