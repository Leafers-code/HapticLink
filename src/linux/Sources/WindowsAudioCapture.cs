#if WINDOWS_BACKEND
using HapticLink.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HapticLink.Sources;

internal sealed class WindowsAudioCapture : IDisposable
{
    private WasapiLoopbackCapture _capture;
    private MMDevice _device;
    private readonly StereoPcmNormalizer _normalizer = new();
    public string Name => _device?.FriendlyName ?? "Windows output";
    public string Error { get; private set; }
    public bool Running { get; private set; }
    public static List<(string Id, string Name)> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<(string, string)>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        { list.Add((device.ID, device.FriendlyName)); device.Dispose(); }
        return list;
    }
    public void Start(Settings settings, Action<byte[]> samples)
    {
        if (settings.AudioCaptureMode != AudioCaptureMode.OutputMonitor)
            throw new NotSupportedException("Windows: select Chosen output device. Per-application audio isolation is currently Linux-only; game-focus pausing is available on Windows.");
        using var enumerator = new MMDeviceEnumerator();
        _device = string.IsNullOrWhiteSpace(settings.AudioDeviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(settings.AudioDeviceId);
        _capture = new WasapiLoopbackCapture(_device);
        _capture.DataAvailable += (_, e) =>
        {
            try
            {
                var f = _capture.WaveFormat;
                var floating = f.Encoding == WaveFormatEncoding.IeeeFloat || f is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
                var buffer = _normalizer.Convert(e.Buffer, e.BytesRecorded, f.SampleRate, f.Channels, f.BitsPerSample, floating);
                if (buffer.Length > 0) samples(buffer);
            }
            catch (Exception ex) { Error = ex.Message; }
        };
        _capture.RecordingStopped += (_, e) => { Running = false; if (e.Exception != null) Error = e.Exception.Message; };
        _capture.StartRecording(); Running = true;
    }
    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose(); _device?.Dispose(); _capture = null; _device = null; Running = false;
    }
}
#endif
