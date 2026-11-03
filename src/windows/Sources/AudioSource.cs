using System;
using System.Collections.Generic;
using HapticLink.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HapticLink.Sources
{
    /// <summary>
    /// WASAPI loopback capture of whatever is playing, mapped to intensity.
    /// Works with every game with zero per-title setup — explosions, engines and
    /// impacts all carry low-frequency energy, so Bass mode tracks "action" well.
    /// </summary>
    public class AudioSource : SourceBase
    {
        public override string Name => "Audio";

        private readonly Settings _s;
        private WasapiLoopbackCapture _capture;
        private MMDevice _device;

        // one-pole filter state, per band
        private double _lp1, _lp2, _hp;
        private DateTime _lastDataUtc = DateTime.MinValue;

        public AudioSource(Settings s) => _s = s;

        public static List<(string Id, string Name)> ListDevices()
        {
            var list = new List<(string, string)>();
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    list.Add((d.ID, d.FriendlyName));
            }
            catch (Exception ex)
            {
                Log.Write("Audio device enumeration failed: " + ex.Message);
            }
            return list;
        }

        public override void Start()
        {
            if (Running) return;
            try
            {
                using var en = new MMDeviceEnumerator();
                _device = string.IsNullOrWhiteSpace(_s.AudioDeviceId)
                    ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : en.GetDevice(_s.AudioDeviceId);

                _capture = new WasapiLoopbackCapture(_device);
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += OnStopped;
                _capture.StartRecording();

                Available = true;
                Running = true;
                Status = $"Listening: {_device.FriendlyName}";
                Log.Write("Audio: " + Status);
            }
            catch (Exception ex)
            {
                Available = false;
                Status = "Capture failed: " + ex.Message;
                Log.Write("Audio: " + ex.Message);
                Cleanup();
            }
        }

        private void OnStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null) Log.Write("Audio capture stopped: " + e.Exception.Message);
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            var fmt = _capture?.WaveFormat;
            if (fmt == null || e.BytesRecorded == 0) return;

            var channels = Math.Max(1, fmt.Channels);
            var sampleRate = fmt.SampleRate;
            double sumSq = 0;
            int frames = 0;

            // per-frame smoothing coefficients
            var lpAlpha = Coef(180.0, sampleRate);
            var hpAlpha = Coef(2500.0, sampleRate);

            foreach (var mono in ReadFrames(e.Buffer, e.BytesRecorded, fmt, channels))
            {
                double v;
                switch (_s.AudioBand)
                {
                    case AudioBand.Bass:
                        // two cascaded one-poles = steeper rolloff, less mid bleed
                        _lp1 += (mono - _lp1) * lpAlpha;
                        _lp2 += (_lp1 - _lp2) * lpAlpha;
                        v = _lp2;
                        break;
                    case AudioBand.Treble:
                        _hp += (mono - _hp) * hpAlpha;
                        v = mono - _hp;
                        break;
                    default:
                        v = mono;
                        break;
                }
                sumSq += v * v;
                frames++;
            }

            if (frames == 0) return;
            var rms = Math.Sqrt(sumSq / frames);
            var db = 20.0 * Math.Log10(rms + 1e-9);

            var floor = _s.AudioFloorDb;
            var ceil = Math.Max(floor + 1.0, _s.AudioCeilDb);
            Raw = Sig.Clamp01((db - floor) / (ceil - floor));
            _lastDataUtc = DateTime.UtcNow;
        }

        private static double Coef(double cutoffHz, int sampleRate)
        {
            if (sampleRate <= 0) return 0.1;
            var dt = 1.0 / sampleRate;
            var rc = 1.0 / (2 * Math.PI * cutoffHz);
            return dt / (rc + dt);
        }

        /// <summary>Yields channel-averaged samples, handling the formats WASAPI actually hands back.</summary>
        private static IEnumerable<double> ReadFrames(byte[] buf, int count, WaveFormat fmt, int channels)
        {
            int bps = fmt.BitsPerSample;
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (fmt.Encoding == WaveFormatEncoding.Extensible && bps == 32);
            int bytesPerSample = bps / 8;
            if (bytesPerSample <= 0) yield break;
            int stride = bytesPerSample * channels;

            for (int off = 0; off + stride <= count; off += stride)
            {
                double acc = 0;
                for (int c = 0; c < channels; c++)
                {
                    int p = off + c * bytesPerSample;
                    if (isFloat && bps == 32) acc += BitConverter.ToSingle(buf, p);
                    else if (bps == 16) acc += BitConverter.ToInt16(buf, p) / 32768.0;
                    else if (bps == 32) acc += BitConverter.ToInt32(buf, p) / 2147483648.0;
                    else if (bps == 24) acc += ((buf[p] | (buf[p + 1] << 8) | ((sbyte)buf[p + 2] << 16)) / 8388608.0);
                    else if (bps == 8) acc += (buf[p] - 128) / 128.0;
                }
                yield return acc / channels;
            }
        }

        public override void Tick(double dtMs)
        {
            // WASAPI stops delivering buffers when the endpoint goes idle; don't hold a stale level.
            if (Running && (DateTime.UtcNow - _lastDataUtc).TotalMilliseconds > 300)
                Raw = 0;
        }

        public override void Stop()
        {
            Cleanup();
            Running = false;
            Raw = 0;
            _lp1 = _lp2 = _hp = 0;
            if (Available) Status = "Stopped";
        }

        private void Cleanup()
        {
            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnData;
                    _capture.RecordingStopped -= OnStopped;
                    _capture.StopRecording();
                    _capture.Dispose();
                }
            }
            catch { }
            try { _device?.Dispose(); } catch { }
            _capture = null;
            _device = null;
        }
    }
}
