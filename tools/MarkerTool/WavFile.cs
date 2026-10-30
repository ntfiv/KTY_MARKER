using System;
using System.IO;
using System.Text;

namespace KTY_MARKER.Tool;

/// <summary>WAVファイルの読み書き(PCM 8/16/24/32bit・浮動小数点32bitを読み、16bit PCMで書く)。</summary>
internal sealed class WavFile
{
    public int SampleRate;

    /// <summary>[チャンネル][サンプル]、-1.0～1.0。</summary>
    public float[][] Channels;

    public int Frames => Channels.Length == 0 ? 0 : Channels[0].Length;

    public static WavFile Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("not a RIFF file");
        }

        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("not a WAVE file");
        }

        int tag = 0, channels = 0, rate = 0, bits = 0, blockAlign = 0;
        byte[] data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                tag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                blockAlign = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (tag == 0xFFFE && size >= 40)
                {
                    reader.ReadUInt16(); // cbSize
                    reader.ReadUInt16(); // valid bits
                    reader.ReadInt32();  // channel mask
                    tag = reader.ReadUInt16(); // SubFormatの先頭2バイト
                }
            }
            else if (id == "data")
            {
                data = reader.ReadBytes(Math.Min(size, (int)(reader.BaseStream.Length - reader.BaseStream.Position)));
            }

            if (next > reader.BaseStream.Length)
            {
                break;
            }

            reader.BaseStream.Position = next;
        }

        if (data == null || channels == 0)
        {
            throw new InvalidDataException("fmt or data chunk is missing");
        }

        var frames = data.Length / blockAlign;
        var wav = new WavFile { SampleRate = rate, Channels = new float[channels][] };
        for (var c = 0; c < channels; c++)
        {
            wav.Channels[c] = new float[frames];
        }

        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                var o = f * blockAlign + c * (bits / 8);
                float v;
                if (tag == 3 && bits == 32)
                {
                    v = BitConverter.ToSingle(data, o);
                }
                else if (tag == 1 || tag == 0xFFFE)
                {
                    switch (bits)
                    {
                        case 8: v = (data[o] - 128) / 128f; break;
                        case 16: v = BitConverter.ToInt16(data, o) / 32768f; break;
                        case 24: v = ((data[o] | (data[o + 1] << 8) | (data[o + 2] << 16)) << 8 >> 8) / 8388608f; break;
                        case 32: v = (float)(BitConverter.ToInt32(data, o) / 2147483648.0); break;
                        default: throw new NotSupportedException($"{bits}bit PCM is not supported");
                    }
                }
                else
                {
                    throw new NotSupportedException($"format tag 0x{tag:X4} / {bits}bit is not supported (use PCM or 32bit float WAV)");
                }

                wav.Channels[c][f] = v;
            }
        }

        return wav;
    }

    /// <summary>16bit PCMで書き出す(BVEが確実に読める形式)。はみ出した値は丸め、その数を返す。</summary>
    public int Write16(string path)
    {
        var channels = Channels.Length;
        var frames = Frames;
        var clipped = 0;
        using var writer = new BinaryWriter(File.Create(path));
        var dataSize = frames * channels * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * channels * 2);
        writer.Write((ushort)(channels * 2));
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                var v = Channels[c][f] * 32767.0;
                if (v > 32767 || v < -32768)
                {
                    clipped++;
                    v = Math.Max(-32768, Math.Min(32767, v));
                }

                writer.Write((short)Math.Round(v));
            }
        }

        return clipped;
    }

    /// <summary>
    /// サンプリング周波数を変える(窓付きsinc補間)。マーカーの18～20kHzを入れるため、22.05kHzなどの
    /// WAVを48kHzに上げるのに使う(元の音の高音が増えるわけではない)。
    /// </summary>
    public WavFile Resample(int newRate)
    {
        if (newRate == SampleRate)
        {
            return this;
        }

        const int halfTaps = 32;
        var ratio = (double)SampleRate / newRate;
        var cutoff = 0.5 * Math.Min(1.0, 1.0 / ratio) * 0.95; // 入力のサンプリング周波数に対する比
        var newFrames = (int)Math.Floor(Frames / ratio);
        var result = new WavFile { SampleRate = newRate, Channels = new float[Channels.Length][] };
        for (var c = 0; c < Channels.Length; c++)
        {
            var src = Channels[c];
            var dst = new float[newFrames];
            for (var n = 0; n < newFrames; n++)
            {
                var t = n * ratio;
                var center = (int)Math.Floor(t);
                double sum = 0;
                for (var k = center - halfTaps + 1; k <= center + halfTaps; k++)
                {
                    if (k < 0 || k >= src.Length)
                    {
                        continue;
                    }

                    var x = t - k;
                    var arg = 2 * Math.PI * cutoff * x;
                    var sinc = Math.Abs(x) < 1e-9 ? 2 * cutoff : Math.Sin(arg) / (Math.PI * x);
                    var w = 0.5 + 0.5 * Math.Cos(Math.PI * x / halfTaps); // Hann窓
                    sum += src[k] * sinc * w;
                }

                dst[n] = (float)sum;
            }

            result.Channels[c] = dst;
        }

        return result;
    }
}
