using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;

namespace KTY_MARKER.Tool;

/// <summary>
/// KTY_MARKERのマーカー入りWAVの作成と、検知の確認をするツール。使い方は Usage() を参照。
/// マーカーの周波数は --freq で直接指定するか、--event でKTY_MARKER.ini(--ini、省略時は既定値)の
/// [EventN]を指定する。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        try
        {
            var options = Options.Parse(args.Skip(1).ToArray());
            switch (args[0].ToLowerInvariant())
            {
                case "tone": return Tone(options);
                case "beep": return Beep(options);
                case "seqtest": return SeqTest();
                case "sidetest": return SideTest(options);
                case "bench": return Bench(options);
                case "add": return Add(options);
                case "detect": return Detect(options);
                case "listen": return Listen(options);
                case "selftest": return SelfTest(options);
                default:
                    Usage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 2;
        }
    }

    private static void Usage()
    {
        Console.WriteLine(@"MarkerTool - KTY_MARKER のマーカー入りWAVの作成と検知の確認

  MarkerTool tone <出力.wav> (--event N | --freq 18500[,19000]) [--ms 120] [--db -30] [--rate 48000] [--pad 300] [--channel L|R]
      マーカーだけのWAVを作る(前後に--padミリ秒の無音)。--channelを付けるとステレオで、その側だけに入れる。
  MarkerTool add <入力.wav> <出力.wav> (--event N | --freq ...) [--ms 120] [--db -30] [--at 0] [--channel L|R]
      既存のWAV(発車放送・ドアの音など)の--atミリ秒の位置にマーカーを混ぜる。
      44.1kHz未満のWAVは48kHzに変換してから混ぜる。出力は16bit PCM。
      --channel L/R: 左/右チャンネルだけにマーカーを入れる(左側・右側のドアの音用。モノラルのWAVはステレオにする)。
      省略すると全部のチャンネルに入れる。
  MarkerTool beep <出力.wav> [--count 3] [--volume 30] [--freq 1000]
      プラグインが鳴らす確認音と同じWAVを書き出す(試聴や、マーカーと取り違えないかの確認用)。
  MarkerTool bench [--seconds 60] [--rate 48000]
      検知の計算(ToneDetector)だけの速さを測る。音声1秒あたりのCPU時間を表示する(開発用)。
  MarkerTool seqtest
      ドアの再開閉の判定(DoorSequenceTracker)を、決まった筋書きで試す(開発用)。
  MarkerTool sidetest [--ini ...]
      ドアの左右の判定を、作った音(左だけ・右だけ・両方・差が中途半端など)で試す(開発用)。
  MarkerTool detect <ファイル.wav> [--ini KTY_MARKER.ini]
      WAVファイルをプラグインと同じ処理で解析し、検知した時刻を表示する。
  MarkerTool listen [--device | --pid N] [--seconds 10] [--ini ...] [--levels]
      実際に鳴っている音を取り込んで検知する(既定は出力デバイス全体)。
  MarkerTool selftest <ファイル.wav> [--device] [--ini ...] [--levels]
      このツール自身でWAVを再生し、自分のプロセスの音を取り込んで検知する
      (プラグインがBVEの音を取り込むのと同じ状況の確認)。

  --event N  : KTY_MARKER.ini(--ini、省略時は既定値)の[EventN]の周波数を使う
               既定: 1=Departure 18500Hz, 2=DoorCloseStart 19000Hz, 3=DoorOpenStart 19500Hz
  --db       : マーカー1周波数あたりのレベル(dBFS)。小さいほど聞こえにくいが、BVEの音量を下げると検知しにくくなる
  --ms       : マーカーの長さ(ミリ秒)。前後10ミリ秒ずつフェードを付ける");
    }

    // ---- tone / add ----

    private static int Tone(Options o)
    {
        var output = o.Positional(0, "output wav");
        var freqs = o.Frequencies();
        var rate = o.Int("rate", 48000);
        var pad = o.Int("pad", 300);
        var ms = o.Int("ms", 120);
        var frames = (int)((long)(ms + 2 * pad) * rate / 1000);
        var channel = o.Channel();
        var wav = new WavFile { SampleRate = rate, Channels = channel < 0 ? new[] { new float[frames] } : new[] { new float[frames], new float[frames] } };
        MixMarker(wav, freqs, ms, o.Double("db", -30), pad, channel);
        wav.Write16(output);
        Console.WriteLine($"wrote {output}: {rate}Hz {(channel < 0 ? "mono" : "stereo")}, marker {string.Join("+", freqs)}Hz {ms}ms at {pad}ms{ChannelText(channel)}");
        return 0;
    }

    private static int Add(Options o)
    {
        var input = o.Positional(0, "input wav");
        var output = o.Positional(1, "output wav");
        var freqs = o.Frequencies();
        var wav = WavFile.Read(input);
        var originalRate = wav.SampleRate;
        if (wav.SampleRate < 44100)
        {
            wav = wav.Resample(48000);
        }

        var ms = o.Int("ms", 120);
        var at = o.Int("at", 0);
        var channel = o.Channel();
        if (channel >= 0 && wav.Channels.Length == 1)
        {
            // 片側だけに入れるにはステレオが必要。モノラルのWAVは、同じ音を左右に置いたステレオにする。
            Console.WriteLine("(mono input: converting to stereo so that the marker can go into one channel)");
            wav.Channels = new[] { wav.Channels[0], (float[])wav.Channels[0].Clone() };
        }

        var maxFreq = freqs.Max();
        if (maxFreq >= wav.SampleRate / 2.0 * 0.98)
        {
            throw new InvalidOperationException($"{maxFreq}Hz cannot be stored in a {wav.SampleRate}Hz WAV");
        }

        MixMarker(wav, freqs, ms, o.Double("db", -30), at, channel);
        var clipped = wav.Write16(output);
        Console.WriteLine($"wrote {output}: {wav.SampleRate}Hz (input {originalRate}Hz), {wav.Channels.Length}ch, " +
                          $"marker {string.Join("+", freqs)}Hz {ms}ms at {at}ms{ChannelText(channel)}");
        if (clipped > 0)
        {
            Console.WriteLine($"WARNING: {clipped} samples clipped - lower the input volume or --db");
        }

        return 0;
    }

    private static string ChannelText(int channel) => channel switch
    {
        0 => " (left channel only)",
        1 => " (right channel only)",
        _ => "",
    };

    private static int Beep(Options o)
    {
        var output = o.Positional(0, "output wav");
        var count = o.Int("count", 3);
        var volume = o.Int("volume", 30);
        var freq = o.Double("freq", 1000);
        File.WriteAllBytes(output, ConfirmBeeper.BuildWav(freq, volume / 100.0, count));
        Console.WriteLine($"wrote {output}: confirm sound {count} beep(s), {freq}Hz, volume {volume}%");
        return 0;
    }

    // 検知の計算だけの速さを測る。プラグインと同じく10msずつの塊で流し込む。
    private static int Bench(Options o)
    {
        var seconds = o.Double("seconds", 60);
        var rate = o.Int("rate", 48000);
        var config = o.Config();
        var frames = (int)(seconds * rate);
        var audio = new float[frames];
        var random = new Random(1);
        for (var i = 0; i < frames; i++)
        {
            // 背景の雑音(-60dBFS程度)と、ときどきマーカー(19kHz、-36dBFS)
            audio[i] = (float)((random.NextDouble() * 2 - 1) * 0.001);
            if (i % rate < rate / 8)
            {
                audio[i] += (float)(Math.Sin(2 * Math.PI * 19000 * i / rate) * 0.016);
            }
        }

        var chunk = rate / 100;
        var buffer = new float[chunk];
        var detector = new ToneDetector(rate, config.Events, config.Detector);
        var detections = 0;

        // 1回目はJITの準備を兼ねて捨て、2回目を測る。
        for (var pass = 0; pass < 2; pass++)
        {
            detector.Reset();
            detections = 0;
            var gc = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (var pos = 0; pos + chunk <= frames; pos += chunk)
            {
                Array.Copy(audio, pos, buffer, 0, chunk);
                detector.Process(buffer, chunk, _ => detections++);
            }

            sw.Stop();
            if (pass == 1)
            {
                var msPerSecond = sw.Elapsed.TotalMilliseconds / seconds;
                Console.WriteLine($"{seconds:0}s of {rate}Hz audio, {detector.MarkerFrequencies.Count} markers + {detector.GuardFrequencies.Count} guards, " +
                                  $"window={config.Detector.WindowMs}ms hop={config.Detector.HopMs}ms");
                Console.WriteLine($"  total {sw.Elapsed.TotalMilliseconds:0.0}ms -> {msPerSecond:0.000}ms of CPU per 1s of audio ({msPerSecond / 10:0.000}% of one core)");
                Console.WriteLine($"  detections={detections}, gen0 GCs={GC.CollectionCount(0) - gc}");
            }
        }

        return 0;
    }

    // ドアの再開閉の判定を、決まった筋書きで試す。O=戸開マーカー C=戸閉マーカー o=BVEのDoorOpen c=BVEのDoorClose。
    // L/R=左/右の戸開マーカー、l/r=左/右の戸閉マーカー。結果の「 side=Unknown」は省いて比べる。
    private static int SeqTest()
    {
        var cases = new (string name, string script, string expected)[]
        {
            ("normal stop", "oOCc", "Opened | CloseStart #1 | Closed (after 1 close attempt(s))"),
            ("marker before BVE DoorOpen", "OoCc", "Opened | CloseStart #1 | Closed (after 1 close attempt(s))"),
            ("one reopen", "oOCOCc", "Opened | CloseStart #1 | Reopen #1 | CloseStart #2 | Closed (after 2 close attempt(s))"),
            ("three closes", "oCOCOCc", "Opened | CloseStart #1 | Reopen #1 | CloseStart #2 | Reopen #2 | CloseStart #3 | Closed (after 3 close attempt(s))"),
            ("over the limit", "oCOCOCOCc", "Opened | CloseStart #1 | Reopen #1 | CloseStart #2 | Reopen #2 | CloseStart #3 | Reopen #3 | Closed (after 4 close attempt(s))"),
            ("double markers", "oOOCCOOCCc", "Opened | CloseStart #1 | Reopen #1 | CloseStart #2 | Closed (after 2 close attempt(s))"),
            ("close marker missed", "oc", "Opened | CloseStart #1 (estimated) | Closed (after 1 close attempt(s))"),
            ("close after reopen missed", "oCOc", "Opened | CloseStart #1 | Reopen #1 | CloseStart #2 (estimated) | Closed (after 2 close attempt(s))"),
            ("next station resets", "oCOCcoCc", "Opened | CloseStart #1 | Reopen #1 | CloseStart #2 | Closed (after 2 close attempt(s)) | Opened | CloseStart #1 | Closed (after 1 close attempt(s))"),
            ("door opening not seen", "Cc", "CloseStart #1 | Closed (after 1 close attempt(s))"),
            ("jump while closing", "oCoCc", "Opened | CloseStart #1 | Opened | CloseStart #1 | Closed (after 1 close attempt(s))"),
            ("jump while reopened", "oCOoCc", "Opened | CloseStart #1 | Reopen #1 | Opened | CloseStart #1 | Closed (after 1 close attempt(s))"),
            ("side after BVE DoorOpen", "oLlc", "Opened | OpenSide side=Left | CloseStart #1 side=Left | Closed (after 1 close attempt(s)) side=Left"),
            ("side marker first", "Lolc", "Opened side=Left | CloseStart #1 side=Left | Closed (after 1 close attempt(s)) side=Left"),
            ("side once known", "oRRrc", "Opened | OpenSide side=Right | CloseStart #1 side=Right | Closed (after 1 close attempt(s)) side=Right"),
            ("side estimated close", "oRc", "Opened | OpenSide side=Right | CloseStart #1 (estimated) side=Right | Closed (after 1 close attempt(s)) side=Right"),
            ("side reopen", "oLlLlc", "Opened | OpenSide side=Left | CloseStart #1 side=Left | Reopen #1 side=Left | CloseStart #2 side=Left | Closed (after 2 close attempt(s)) side=Left"),
            ("side unknown marker keeps", "oLCc", "Opened | OpenSide side=Left | CloseStart #1 | Closed (after 1 close attempt(s)) side=Left"),
            ("side resets next stop", "oLlcoc", "Opened | OpenSide side=Left | CloseStart #1 side=Left | Closed (after 1 close attempt(s)) side=Left | Opened | CloseStart #1 (estimated) | Closed (after 1 close attempt(s))"),
        };

        var failed = 0;
        foreach (var (name, script, expected) in cases)
        {
            var tracker = new DoorSequenceTracker(3);
            var events = new List<string>();
            var t = 0;
            foreach (var ch in script)
            {
                t += 1000;
                var ev = ch switch
                {
                    'O' => tracker.OnOpenMarker(t),
                    'C' => tracker.OnCloseMarker(t),
                    'L' => tracker.OnOpenMarker(t, DoorSide.Left),
                    'R' => tracker.OnOpenMarker(t, DoorSide.Right),
                    'l' => tracker.OnCloseMarker(t, DoorSide.Left),
                    'r' => tracker.OnCloseMarker(t, DoorSide.Right),
                    'o' => tracker.OnBveDoorOpen(t),
                    'c' => CloseWithEstimate(tracker, t, events),
                    _ => throw new ArgumentException(script),
                };
                if (ev is { } e)
                {
                    events.Add(SeqDescribe(e));
                }
            }

            var actual = string.Join(" | ", events);
            var ok = actual == expected;
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-28} {script}");
            if (!ok)
            {
                Console.WriteLine($"      expected: {expected}");
                Console.WriteLine($"      actual  : {actual}");
            }
        }

        Console.WriteLine(failed == 0 ? "all passed" : $"{failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // BVEのDoorCloseを流す。戸閉開始を補った場合は、それを先に記録する(プラグインと同じ順番)。
    private static DoorSequenceEvent? CloseWithEstimate(DoorSequenceTracker tracker, int t, List<string> events)
    {
        var closed = tracker.OnBveDoorClose(t, out var estimated);
        if (estimated is { } e)
        {
            events.Add(SeqDescribe(e));
        }

        return closed;
    }

    private static string SeqDescribe(DoorSequenceEvent e) => e.Describe().Replace(" side=Unknown", "");

    // ドアの左右の判定を、作った音で試す。プラグインと同じく10msずつ、左右を混ぜた音と左右の音を流し込む。
    private static int SideTest(Options o)
    {
        var config = o.Config();
        const int rate = 48000;
        var close = config.Events.First(e => e.Role == MarkerRole.DoorClose);
        var depart = config.Events.FirstOrDefault(e => e.Role == MarkerRole.Departure);
        var cases = new List<(string name, MarkerEvent marker, double leftDb, double rightDb, bool stereo, DoorSide expected)>
        {
            ("left only", close, -30, -200, true, DoorSide.Left),
            ("right only", close, -200, -30, true, DoorSide.Right),
            ("both (same level)", close, -30, -30, true, DoorSide.Both),
            ("both (5dB apart)", close, -30, -35, true, DoorSide.Both),
            ("unclear (8dB apart)", close, -30, -38, true, DoorSide.Unknown),
            ("left (12dB apart)", close, -30, -42, true, DoorSide.Left),
            ("left only, quiet (-60)", close, -60, -200, true, DoorSide.Left),
            ("mono capture", close, -30, -200, false, DoorSide.Unknown),
        };
        if (depart != null)
        {
            cases.Add(("departure (side not checked)", depart, -30, -200, true, DoorSide.Unknown));
        }

        var failed = 0;
        foreach (var (name, marker, leftDb, rightDb, stereo, expected) in cases)
        {
            var frames = rate; // 1秒
            var left = new float[frames];
            var right = new float[frames];
            var random = new Random(7);
            var start = rate / 4;
            var length = rate * 120 / 1000;
            var fade = rate / 100;
            for (var i = 0; i < frames; i++)
            {
                // 背景の雑音(左右別々、-70dBFS程度)
                left[i] = (float)((random.NextDouble() * 2 - 1) * 0.0003);
                right[i] = (float)((random.NextDouble() * 2 - 1) * 0.0003);
                var n = i - start;
                if (n >= 0 && n < length)
                {
                    var env = n < fade ? 0.5 - 0.5 * Math.Cos(Math.PI * n / fade)
                        : n >= length - fade ? 0.5 - 0.5 * Math.Cos(Math.PI * (length - 1 - n) / fade) : 1.0;
                    var v = 0.0;
                    foreach (var f in marker.Frequencies)
                    {
                        v += Math.Sin(2 * Math.PI * f * n / rate);
                    }

                    left[i] += (float)(v * env * Math.Pow(10, leftDb / 20));
                    right[i] += (float)(v * env * Math.Pow(10, rightDb / 20));
                }
            }

            var detector = new ToneDetector(rate, config.Events, config.Detector);
            var chunk = rate / 100;
            var mono = new float[chunk];
            var l = new float[chunk];
            var r = new float[chunk];
            var results = new List<string>();
            DoorSide? got = null;
            for (var pos = 0; pos + chunk <= frames; pos += chunk)
            {
                for (var k = 0; k < chunk; k++)
                {
                    l[k] = left[pos + k];
                    r[k] = right[pos + k];
                    mono[k] = (l[k] + r[k]) / 2;
                }

                detector.Process(mono, stereo ? l : null, stereo ? r : null, chunk, d =>
                {
                    var e = config.Events[d.EventIndex];
                    var diff = d.SideDiffDb is { } x ? $"{x:+0.0;-0.0}dB" : "n/a";
                    results.Add($"#{e.Number} {e.Name} side={d.Side} (L-R={diff})");
                    if (d.EventIndex == config.Events.IndexOf(marker))
                    {
                        got = d.Side;
                    }
                });
            }

            var ok = got == expected;
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-30} expected={expected,-7} got={(got?.ToString() ?? "not detected"),-12} {string.Join(", ", results)}");
        }

        Console.WriteLine(failed == 0 ? "all passed" : $"{failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>マーカー(正弦波、前後にフェード)を足す。channel = 0(左)・1(右)ならそのチャンネルだけ、負なら全チャンネル。</summary>
    private static void MixMarker(WavFile wav, double[] freqs, int ms, double db, int atMs, int channel = -1)
    {
        var amplitude = Math.Pow(10, db / 20);
        var start = (int)((long)atMs * wav.SampleRate / 1000);
        var length = (int)((long)ms * wav.SampleRate / 1000);
        var fade = Math.Min(length / 2, wav.SampleRate / 100); // 10ms
        for (var n = 0; n < length; n++)
        {
            var index = start + n;
            if (index < 0 || index >= wav.Frames)
            {
                continue;
            }

            // 急に始まる・終わると「プチッ」というノイズ(広い帯域の音)になるので、上げ下げをなめらかにする。
            var envelope = 1.0;
            if (n < fade)
            {
                envelope = 0.5 - 0.5 * Math.Cos(Math.PI * n / fade);
            }
            else if (n >= length - fade)
            {
                envelope = 0.5 - 0.5 * Math.Cos(Math.PI * (length - 1 - n) / fade);
            }

            var t = (double)n / wav.SampleRate;
            var v = 0.0;
            foreach (var f in freqs)
            {
                v += Math.Sin(2 * Math.PI * f * t);
            }

            var sample = (float)(v * amplitude * envelope);
            for (var c = 0; c < wav.Channels.Length; c++)
            {
                if (channel < 0 || c == channel)
                {
                    wav.Channels[c][index] += sample;
                }
            }
        }
    }

    // ---- detect ----

    private static int Detect(Options o)
    {
        var path = o.Positional(0, "wav");
        var config = o.Config();
        var wav = WavFile.Read(path);
        if (wav.SampleRate < 44100)
        {
            // BVE(Windows)は低いサンプリング周波数のWAVも48kHz前後に変換して鳴らすので、それに合わせる。
            Console.WriteLine($"(resampling {wav.SampleRate}Hz -> 48000Hz, as the Windows mixer would)");
            wav = wav.Resample(48000);
        }

        var mono = new float[wav.Frames];
        for (var f = 0; f < wav.Frames; f++)
        {
            var sum = 0f;
            foreach (var channel in wav.Channels)
            {
                sum += channel[f];
            }

            mono[f] = sum / wav.Channels.Length;
        }

        var detector = new ToneDetector(wav.SampleRate, config.Events, config.Detector);
        Console.WriteLine($"{path}: {wav.SampleRate}Hz {wav.Channels.Length}ch {(double)wav.Frames / wav.SampleRate:0.000}s, " +
                          $"markers=[{string.Join(", ", detector.MarkerFrequencies)}]Hz guards=[{string.Join(", ", detector.GuardFrequencies)}]Hz");
        var count = 0;
        var stereo = wav.Channels.Length >= 2;
        detector.Process(mono, stereo ? wav.Channels[0] : null, stereo ? wav.Channels[1] : null, mono.Length, d =>
        {
            count++;
            var e = config.Events[d.EventIndex];
            Console.WriteLine($"  {d.TimeSeconds * 1000,8:0}ms  #{e.Number} {e.Describe()}  level={string.Join("/", d.LevelsDb.Select(l => l.ToString("0.0")))}dBFS guard={d.GuardDb:0.0}dBFS{SideText(d)}");
        });
        var (peaks, guard) = detector.TakePeakLevels();
        Console.WriteLine($"  max levels: {string.Join(" ", detector.MarkerFrequencies.Select((f, i) => $"{f:0}Hz={peaks[i]:0.0}"))} guard={guard:0.0} (dBFS)");
        Console.WriteLine(config.Detector.MirrorCheck
            ? $"  mirror check: {detector.TakeMirrorRejects()} rejected (MirrorDb={config.Detector.MirrorDb:0.#})"
            : "  mirror check: off");
        Console.WriteLine($"  {count} detection(s)");
        return 0;
    }

    private static string SideText(ToneDetection d) =>
        d.SideDiffDb is { } diff ? $"  side={d.Side} (L-R={diff:+0.0;-0.0}dB)" : "";

    // ---- listen / selftest ----

    private static int Listen(Options o)
    {
        var mode = o.Has("pid") ? CaptureMode.Process : CaptureMode.Device;
        var pid = o.Int("pid", 0);
        var seconds = o.Double("seconds", 10);
        return RunCapture(o, mode, pid, TimeSpan.FromSeconds(seconds), null);
    }

    private static int SelfTest(Options o)
    {
        var path = Path.GetFullPath(o.Positional(0, "wav"));
        var mode = o.Has("device") ? CaptureMode.Device : CaptureMode.Process;
        var wav = WavFile.Read(path);
        var duration = TimeSpan.FromSeconds((double)wav.Frames / wav.SampleRate + 1.5);
        return RunCapture(o, mode, Process.GetCurrentProcess().Id, duration, () =>
        {
            Thread.Sleep(500); // 取り込みが始まるのを待つ
            Console.WriteLine($"playing {path} at {Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency:0}ms (stopwatch)");
            using var player = new SoundPlayer(path);
            player.PlaySync();
        });
    }

    private static int RunCapture(Options o, CaptureMode mode, int pid, TimeSpan duration, Action whileCapturing)
    {
        var config = o.Config();
        var logLevels = o.Has("levels");
        ToneDetector detector = null;
        var detections = 0;
        var lastLevelLog = Stopwatch.GetTimestamp();
        var captureStart = Stopwatch.GetTimestamp();
        var lockObj = new object();

        void OnAudio(float[] mono, float[] left, float[] right, int frames, int rate, long qpc)
        {
            lock (lockObj)
            {
                detector ??= new ToneDetector(rate, config.Events, config.Detector);
                var startSamples = detector.ProcessedSamples;
                detector.Process(mono, left, right, frames, d =>
                {
                    detections++;
                    var e = config.Events[d.EventIndex];
                    var lag = "?";
                    if (qpc > 0)
                    {
                        var sampleQpc = qpc + (long)((d.SampleIndex - startSamples) * 10_000_000.0 / rate);
                        lag = ((LoopbackCapture.NowQpc100ns() - sampleQpc) / 10_000.0).ToString("0");
                    }

                    var analysis = config.Detector.WindowMs + (config.Detector.MinHits - 1) * config.Detector.HopMs;
                    Console.WriteLine($"  DETECTED at {(Stopwatch.GetTimestamp() - captureStart) * 1000.0 / Stopwatch.Frequency,7:0}ms  #{e.Number} {e.Describe()}  " +
                                      $"level={string.Join("/", d.LevelsDb.Select(l => l.ToString("0.0")))}dBFS guard={d.GuardDb:0.0}dBFS{SideText(d)}  " +
                                      $"captureLag={lag}ms (+{analysis:0}ms analysis)");
                });

                if (logLevels && Stopwatch.GetTimestamp() - lastLevelLog >= Stopwatch.Frequency)
                {
                    lastLevelLog = Stopwatch.GetTimestamp();
                    var (peaks, guard) = detector.TakePeakLevels();
                    Console.WriteLine($"  levels: {string.Join(" ", detector.MarkerFrequencies.Select((f, i) => $"{f:0}Hz={peaks[i]:0.0}"))} guard={guard:0.0}");
                }
            }
        }

        using var capture = new LoopbackCapture(mode, pid, fallbackToDevice: false, OnAudio, null, message => Console.WriteLine("  [capture] " + message));
        Console.WriteLine($"capturing: mode={mode}{(mode == CaptureMode.Process ? $" pid={pid}" : "")}, {duration.TotalSeconds:0.0}s");
        capture.Start();
        capture.Paused = false;
        if (o.Has("pausetest"))
        {
            // 走行→停車と同じ順番(取り込み中 → 一時停止 → 再開)を再現してから鳴らす。
            Thread.Sleep(700);
            capture.Paused = true;
            Console.WriteLine("  (paused)");
            Thread.Sleep(700);
            capture.Paused = false;
            Console.WriteLine("  (resumed)");
            duration += TimeSpan.FromSeconds(1.4);
        }

        var deadline = DateTime.UtcNow + duration;
        if (whileCapturing != null)
        {
            whileCapturing();
        }

        while (DateTime.UtcNow < deadline && !capture.Failed)
        {
            Thread.Sleep(50);
        }

        capture.Dispose();
        Console.WriteLine($"done: {detections} detection(s), active mode={capture.ActiveMode}, rate={capture.SampleRate}Hz{(capture.Failed ? ", CAPTURE FAILED" : "")}");
        return capture.Failed ? 3 : 0;
    }

    /// <summary>コマンドラインの「--名前 値」と、それ以外の位置引数。</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positional = new();

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    var name = args[i].Substring(2);
                    var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                    o._named[name] = hasValue ? args[++i] : "";
                }
                else
                {
                    o._positional.Add(args[i]);
                }
            }

            return o;
        }

        public bool Has(string name) => _named.ContainsKey(name);

        public string Positional(int index, string what) =>
            index < _positional.Count ? _positional[index] : throw new ArgumentException($"missing argument: {what}");

        public int Int(string name, int fallback) => _named.TryGetValue(name, out var v) && v.Length > 0 ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

        /// <summary>--channel L/R/both → 0(左)・1(右)・-1(全部)。</summary>
        public int Channel()
        {
            if (!_named.TryGetValue("channel", out var v) || v.Length == 0)
            {
                return -1;
            }

            switch (v.ToUpperInvariant())
            {
                case "L": case "LEFT": return 0;
                case "R": case "RIGHT": return 1;
                case "BOTH": case "ALL": return -1;
                default: throw new ArgumentException("--channel must be L, R or both");
            }
        }

        public double Double(string name, double fallback) => _named.TryGetValue(name, out var v) && v.Length > 0 ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

        public MarkerConfig Config()
        {
            if (_named.TryGetValue("ini", out var ini) && ini.Length > 0)
            {
                var full = Path.GetFullPath(ini);
                if (!string.Equals(Path.GetFileName(full), MarkerConfig.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"--ini must point to a file named {MarkerConfig.FileName}");
                }

                if (!File.Exists(full))
                {
                    throw new FileNotFoundException(full);
                }

                return MarkerConfig.Load(Path.GetDirectoryName(full));
            }

            return new MarkerConfig();
        }

        public double[] Frequencies()
        {
            if (_named.TryGetValue("freq", out var text) && text.Length > 0)
            {
                return text.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            }

            if (_named.TryGetValue("event", out var number) && number.Length > 0)
            {
                var n = int.Parse(number, CultureInfo.InvariantCulture);
                var e = Config().Events.FirstOrDefault(x => x.Number == n) ?? throw new ArgumentException($"event {n} is not defined");
                return e.Frequencies;
            }

            throw new ArgumentException("specify --event N or --freq F[,F2]");
        }
    }
}
