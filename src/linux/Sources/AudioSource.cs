using System.Diagnostics;
using System.Text.Json;
using HapticLink.Core;

namespace HapticLink.Sources;

/// <summary>Output-monitor capture through PulseAudio or PipeWire's PulseAudio server.</summary>
public sealed class AudioSource : SourceBase
{
    public override string Name => "Audio";
    private readonly Settings _s;
#if WINDOWS_BACKEND
    private WindowsAudioCapture _windows;
#endif
    private Process _process;
    private CancellationTokenSource _cts;
    private Task _reader;
    private readonly GameFocus _focus;
    private readonly HapticEqualizer _eq = new();
    public double[] Spectrum => _eq.Levels;
    private DateTime _lastData;
    private readonly double[] _lp1 = new double[2], _lp2 = new double[2], _hp = new double[2];
    public AudioSource(Settings s, GameFocus focus = null) { _s = s; _focus = focus ?? new GameFocus(s); }

    public static List<(string Id, string Name)> ListDevices()
    {
#if WINDOWS_BACKEND
        if (OperatingSystem.IsWindows()) return WindowsAudioCapture.Devices();
#endif
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
#if WINDOWS_BACKEND
        if (OperatingSystem.IsWindows())
        {
            Stop();
            try { _windows = new WindowsAudioCapture(); _windows.Start(_s, ProcessSamples); Available = Running = true; Status = "Listening: " + _windows.Name; }
            catch (Exception ex) { _windows?.Dispose(); _windows = null; Available = Running = false; Status = "Audio: " + ex.Message; }
            return;
        }
#endif
        if (_reader is { IsCompleted: false }) Stop();
        if (!OperatingSystem.IsLinux()) { Available = false; Status = "Linux audio backend"; return; }
        _cts = new();
        Available = Running = true;
        var ct = _cts.Token;
        _reader = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                Process process = null;
                try
                {
                    var target = AudioRouting.Resolve(_s, _focus);
                    ct.ThrowIfCancellationRequested();
                    if (target == null) { Raw = 0; Status = "Waiting for audio from the selected application/game"; await Task.Delay(750, ct); continue; }
                    Array.Clear(_lp1); Array.Clear(_lp2); Array.Clear(_hp); _eq.Reset();
                    var args = new List<string> { "--raw", "--format=float32le", "--rate=48000", "--channels=2", "--latency-msec=20", "--device=" + target.Monitor };
                    if (target.Stream is { } index) args.Add("--monitor-stream=" + index);
                    process = LinuxProcess.Start("parec", args.ToArray()); _process = process;
                    var errors = process.StandardError.ReadToEndAsync();
                    using var capture = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var buffer = new byte[3840];
                    var reader = Task.Run(async () =>
                    {
                        while (!capture.IsCancellationRequested)
                        {
                            await process.StandardOutput.BaseStream.ReadExactlyAsync(buffer, capture.Token);
                            ProcessSamples(buffer);
                        }
                    }, ct);
                    Status = "Listening: " + target.Label; Available = true;
                    try
                    {
                        while (!ct.IsCancellationRequested && !reader.IsCompleted)
                        {
                            await Task.WhenAny(reader, Task.Delay(1000, ct));
                            ct.ThrowIfCancellationRequested();
                            if (AudioRouting.Resolve(_s, _focus) != target) break;
                        }
                    }
                    finally
                    {
                        capture.Cancel();
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                        try { await reader; } catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException) { }
                        var error = await errors;
                        if (!ct.IsCancellationRequested && error.Length > 0) Status = "Audio: " + error.Trim();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Available = false; Status = "Audio: install pulseaudio-utils / check application. " + ex.Message;
                }
                finally { Raw = 0; process?.Dispose(); if (_process == process) _process = null; }
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
            Raw = 0; Running = false;
        });
    }

    internal void ProcessSamples(byte[] buffer)
    {
        double sum = 0;
        _eq.Configure(_s);
        double lp = Coef(180), hp = Coef(2500);
        for (int off = 0; off + 8 <= buffer.Length; off += 8)
            for (int c = 0; c < 2; c++)
            {
                double sample = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(off + c * 4, 4));
                if (!double.IsFinite(sample)) sample = 0;
                sample = _eq.Process(sample, c);
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
        _eq.FinishBuffer(buffer.Length / 4);
    }

    private static double Coef(double cutoff) { var dt = 1d / 48000; return dt / (1 / (2 * Math.PI * cutoff) + dt); }
    public override void Tick(double dtMs)
    {
#if WINDOWS_BACKEND
        if (_windows != null) { Running = _windows.Running; if (_windows.Error is { Length: > 0 } error) Status = "Audio: " + error; }
#endif
        if ((DateTime.UtcNow - _lastData).TotalMilliseconds > 300) Raw = 0;
    }
    public override void Stop()
    {
#if WINDOWS_BACKEND
        _windows?.Dispose(); _windows = null;
#endif
        _cts?.Cancel();
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { _reader?.Wait(1500); } catch { }
        _process?.Dispose(); _process = null;
        _cts?.Dispose(); _cts = null;
        Running = false; Raw = 0;
        Array.Clear(_lp1); Array.Clear(_lp2); Array.Clear(_hp);
        _eq.Reset();
        if (Available) Status = "Stopped";
    }
}
