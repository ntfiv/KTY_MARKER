using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KTY_MARKER;

/// <summary>
/// マーカーを検知したことを知らせる確認音(「ピッ」を何回か)。音はWAVファイルではなく、ここで作った
/// データをメモリから鳴らす(Windows標準のPlaySound。依存DLLもSound.cfgの変更も要らない)。
///
/// 回数でマーカーを区別する(既定ではマーカーの番号と同じ回数: 発車放送=1回、戸閉開始=2回、戸開開始=3回)。
/// 音の高さは既定1kHzで、マーカー(18.5～19.5kHz)から十分離れているので、確認音を自分で検知することはない
/// (MarkerToolで確認済み)。
///
/// ドアの側が分かったときは、その側(左・右)のチャンネルだけで鳴らす(両側・不明は左右両方)。
///
/// 注意: PlaySoundは同じプロセス(BVE)の中で1つしか鳴らせず、新しく鳴らすと前の音は止まる。BVE本体や
/// 他のプラグインがPlaySoundを使っていると、互いに止め合う可能性がある(確認用の機能なので許容する)。
/// </summary>
internal sealed class ConfirmBeeper
{
    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private const int SampleRate = 48000;

    // 非同期で鳴らしている間、PlaySoundはこのメモリを読み続ける。途中で解放・移動されないよう、
    // 一度作った音はプロセスが終わるまで固定(ピン留め)したまま持っておく(数十KBなので問題ない)。
    private static readonly Dictionary<string, GCHandle> PinnedSounds = new();
    private static readonly object PinnedLock = new();

    private readonly double _frequency;
    private readonly double _volume;

    public ConfirmBeeper(double frequency, int volumePercent)
    {
        _frequency = frequency;
        _volume = Math.Max(0, Math.Min(100, volumePercent)) / 100.0;
    }

    /// <summary>
    /// 「ピッ」をcount回鳴らす(すぐに戻る)。frequencyを指定するとその高さで鳴らす(ドアの再開閉で、
    /// 何回目かを高さで表すのに使う)。sideがLeft/Rightならその側のチャンネルだけで鳴らす。鳴らせなかったらfalse。
    /// </summary>
    public bool Play(int count, double frequency = 0, DoorSide side = DoorSide.Unknown)
    {
        if (count <= 0 || _volume <= 0)
        {
            return true;
        }

        var pointer = GetPinnedSound(count, frequency > 0 ? frequency : _frequency, side);
        return PlaySound(pointer, IntPtr.Zero, SndAsync | SndMemory | SndNoDefault);
    }

    /// <summary>鳴っている確認音を止める(Disposeの時に呼ぶ)。</summary>
    public static void StopAll()
    {
        try
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        }
        catch
        {
            // 終了処理中の失敗は無視する。
        }
    }

    private IntPtr GetPinnedSound(int count, double frequency, DoorSide side)
    {
        return GetPinned($"beep/{frequency}/{_volume}/{count}/{side}", () => BuildWav(frequency, _volume, count, side));
    }

    private static IntPtr GetPinned(string key, Func<byte[]> build)
    {
        lock (PinnedLock)
        {
            if (!PinnedSounds.TryGetValue(key, out var handle))
            {
                handle = GCHandle.Alloc(build(), GCHandleType.Pinned);
                PinnedSounds[key] = handle;
            }

            return handle.AddrOfPinnedObject();
        }
    }

    /// <summary>
    /// 自己診断の試験音を鳴らす(すぐに戻る)。toneSets[i]の周波数を同時に、toneMsずつ順番に、間にgapMsの無音を
    /// 挟んで鳴らす。マーカーと同じ作り(前後10msのフェード)。sides[i]がLeft/Rightなら、その音はその側のチャンネル
    /// だけで鳴らす(左右を見分けられるかの確認。sidesがnullなら全部左右両方)。鳴らせなかったらfalse。
    /// </summary>
    public static bool PlayTestTones(IList<double[]> toneSets, IList<DoorSide> sides, double levelDb, int toneMs, int gapMs)
    {
        var key = "test/" + string.Join(";", toneSets.Select((t, i) => string.Join("+", t) + "@" + SideAt(sides, i))) + $"/{levelDb}/{toneMs}/{gapMs}";
        var pointer = GetPinned(key, () => BuildToneSequenceWav(toneSets, sides, levelDb, toneMs, gapMs));
        return PlaySound(pointer, IntPtr.Zero, SndAsync | SndMemory | SndNoDefault);
    }

    private static DoorSide SideAt(IList<DoorSide> sides, int i) => sides != null && i < sides.Count ? sides[i] : DoorSide.Unknown;

    /// <summary>試験音を鳴らし終わるまでの時間(ミリ秒)。</summary>
    public static int TestTonesDurationMs(int count, int toneMs, int gapMs) => gapMs + count * (toneMs + gapMs);

    /// <summary>試験音のWAV(16bit・ステレオ・48kHz)。先頭にgapMsの無音を置く(再生の立ち上がりで欠けないように)。</summary>
    public static byte[] BuildToneSequenceWav(IList<double[]> toneSets, IList<DoorSide> sides, double levelDb, int toneMs, int gapMs)
    {
        var amplitude = Math.Pow(10, levelDb / 20);
        var tone = SampleRate * toneMs / 1000;
        var gap = SampleRate * gapMs / 1000;
        var fade = Math.Min(tone / 2, SampleRate / 100);
        var samples = new short[gap + toneSets.Count * (tone + gap)];
        var pans = new DoorSide[samples.Length];
        for (var i = 0; i < toneSets.Count; i++)
        {
            var start = gap + i * (tone + gap);
            for (var n = 0; n < tone; n++)
            {
                var envelope = 1.0;
                if (n < fade)
                {
                    envelope = 0.5 - 0.5 * Math.Cos(Math.PI * n / fade);
                }
                else if (n >= tone - fade)
                {
                    envelope = 0.5 - 0.5 * Math.Cos(Math.PI * (tone - 1 - n) / fade);
                }

                var v = 0.0;
                foreach (var f in toneSets[i])
                {
                    v += Math.Sin(2 * Math.PI * f * n / SampleRate);
                }

                samples[start + n] = (short)Math.Max(-32768, Math.Min(32767, Math.Round(v * amplitude * envelope * 32767)));
                pans[start + n] = SideAt(sides, i);
            }
        }

        return ToStereoWav(samples, pans);
    }

    /// <summary>
    /// 確認音のWAV(16bit・ステレオ・48kHz)を作る。1回の「ピッ」は100ms、間隔は80ms。前後に10msのフェードを
    /// 付ける(急に始まる・終わると「プチッ」という広い帯域のノイズになり、マーカーの帯域にも漏れるため)。
    /// sideがLeft/Rightならその側のチャンネルだけ、それ以外は左右両方で鳴らす。
    /// </summary>
    public static byte[] BuildWav(double frequency, double volume, int count, DoorSide side = DoorSide.Unknown)
    {
        const int beepMs = 100;
        const int gapMs = 80;
        var beep = SampleRate * beepMs / 1000;
        var gap = SampleRate * gapMs / 1000;
        var fade = SampleRate / 100;
        var frames = count * beep + (count - 1) * gap + gap; // 最後に少し無音を付ける
        var samples = new short[frames];
        for (var b = 0; b < count; b++)
        {
            var start = b * (beep + gap);
            for (var n = 0; n < beep; n++)
            {
                var envelope = 1.0;
                if (n < fade)
                {
                    envelope = 0.5 - 0.5 * Math.Cos(Math.PI * n / fade);
                }
                else if (n >= beep - fade)
                {
                    envelope = 0.5 - 0.5 * Math.Cos(Math.PI * (beep - 1 - n) / fade);
                }

                var v = Math.Sin(2 * Math.PI * frequency * n / SampleRate) * volume * envelope;
                samples[start + n] = (short)Math.Round(v * 32767);
            }
        }

        var pans = new DoorSide[frames];
        for (var i = 0; i < frames; i++)
        {
            pans[i] = side;
        }

        return ToStereoWav(samples, pans);
    }

    // モノラルの音を、サンプルごとの側(Left = 左だけ、Right = 右だけ、それ以外 = 左右両方)でステレオにしてWAVにする。
    private static byte[] ToStereoWav(short[] samples, DoorSide[] pans)
    {
        var frames = samples.Length;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = frames * 4;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1);            // PCM
        writer.Write((ushort)2);            // ステレオ
        writer.Write(SampleRate);
        writer.Write(SampleRate * 4);
        writer.Write((ushort)4);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        for (var i = 0; i < frames; i++)
        {
            var s = samples[i];
            writer.Write(pans[i] == DoorSide.Right ? (short)0 : s);
            writer.Write(pans[i] == DoorSide.Left ? (short)0 : s);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
