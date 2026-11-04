using System.Buffers.Binary;

namespace HapticLink.Core;

/// <summary>Converts WASAPI PCM buffers into continuous 48 kHz stereo float32 for the shared DSP.</summary>
public sealed class StereoPcmNormalizer
{
    private bool _primed;
    private double _lastLeft, _lastRight, _position;
    public byte[] Convert(byte[] data, int length, int rate, int channels, int bits, bool floating)
    {
        if (rate <= 0 || channels <= 0 || bits is not (8 or 16 or 24 or 32)) throw new NotSupportedException("Unsupported capture PCM format");
        int stride = channels * (bits / 8), frames = length / stride;
        if (frames == 0) return Array.Empty<byte>();
        int prefix = _primed ? 1 : 0;
        var left = new double[frames + prefix]; var right = new double[left.Length];
        if (_primed) { left[0] = _lastLeft; right[0] = _lastRight; }
        double Sample(int p)
        {
            var value = floating && bits == 32 ? BitConverter.ToSingle(data, p) : bits switch
            {
                8 => (data[p] - 128) / 128d,
                16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p, 2)) / 32768d,
                24 => (data[p] | data[p + 1] << 8 | (sbyte)data[p + 2] << 16) / 8388608d,
                32 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p, 4)) / 2147483648d,
                _ => 0
            };
            return double.IsFinite(value) ? value : 0;
        }
        for (int frame = 0; frame < frames; frame++)
        {
            left[frame + prefix] = Sample(frame * stride);
            right[frame + prefix] = channels == 1 ? left[frame + prefix] : Sample(frame * stride + bits / 8);
        }
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        double step = rate / 48000d;
        while (_position + 1 < left.Length)
        {
            int at = (int)_position; var f = _position - at;
            writer.Write((float)(left[at] + (left[at + 1] - left[at]) * f));
            writer.Write((float)(right[at] + (right[at + 1] - right[at]) * f));
            _position += step;
        }
        _position -= left.Length - 1;
        _lastLeft = left[^1]; _lastRight = right[^1]; _primed = true;
        return output.ToArray();
    }
}
