using System;
using System.Collections.Generic;
using System.Linq;

namespace KTY_MARKER;

/// <summary>マーカーの役割。ドアの再開閉の判定(DoorSequenceTracker)に使う。</summary>
public enum MarkerRole
{
    /// <summary>役割なし(検知を知らせるだけ)。</summary>
    None,

    /// <summary>到着時放送(駅リストのarrivalSoundKey)。</summary>
    Arrival,

    /// <summary>発車時放送(駅リストのdepartureSoundKey)。</summary>
    Departure,

    /// <summary>戸閉開始(ドアが閉まり始める音)。</summary>
    DoorClose,

    /// <summary>戸開開始(ドアが開き始める音)。</summary>
    DoorOpen,
}

/// <summary>
/// ドアの側(マーカーが左右どちらのチャンネルで鳴ったか)。左側のドアの音のWAVは左チャンネルだけに、右側は
/// 右チャンネルだけにマーカーを入れておくと、左右のレベルの差から分かる。Panelにもこの数値を書く。
/// </summary>
public enum DoorSide
{
    /// <summary>分からない(左右の差が中途半端、取り込みがモノラル、側を調べない設定など)。</summary>
    Unknown = 0,

    /// <summary>左側(左チャンネルの方がSideDb以上大きい)。</summary>
    Left = 1,

    /// <summary>右側(右チャンネルの方がSideDb以上大きい)。</summary>
    Right = 2,

    /// <summary>両側(左右の差がBothDb以内。両側のドアの音が同時に鳴った)。</summary>
    Both = 3,
}

/// <summary>
/// マーカー1種類の定義。FreqAだけ(FreqB=0)なら1つの周波数、両方なら2つの周波数を同時に鳴らした音を
/// マーカーとする。
/// </summary>
public sealed class MarkerEvent
{
    public int Number;
    public string Name = "";
    public MarkerRole Role;
    public double FreqA;
    public double FreqB;

    /// <summary>検知したことを書き込むPanelのindex(-1なら書き込まない)。</summary>
    public int PanelIndex = -1;

    /// <summary>Panelを1にしておく時間(ミリ秒、シミュレータ内の時刻)。</summary>
    public int HoldMs = 1000;

    /// <summary>検知したときに鳴らす確認音の回数(0で鳴らさない。[General] ConfirmSound = true のときだけ)。</summary>
    public int ConfirmBeeps;

    /// <summary>
    /// 左右どちらのチャンネルで鳴ったか(<see cref="DoorSide"/>)も調べる。戸閉・戸開のマーカーで使う
    /// (iniの[DoorSide] Enabled = true のとき、Role = DoorClose / DoorOpen のマーカーに付く)。
    /// </summary>
    public bool DetectSide;

    public double[] Frequencies => FreqB > 0 ? new[] { FreqA, FreqB } : new[] { FreqA };

    public string Describe() => FreqB > 0 ? $"{Name}({FreqA:0}+{FreqB:0}Hz)" : $"{Name}({FreqA:0}Hz)";
}

/// <summary>検知の感度などの設定。</summary>
public sealed class ToneDetectorSettings
{
    /// <summary>マーカーの周波数がこれ(dBFS、正弦波の振幅基準)より大きければ「鳴っている」候補とする。</summary>
    public double ThresholdDb = -70;

    /// <summary>比較用の周波数(マーカーの外側)よりこのdB以上大きいこと(広い帯域の雑音を除くため)。</summary>
    public double MarginDb = 15;

    /// <summary>
    /// 一番大きいマーカー周波数からこのdB以内のものだけを「鳴っている」とする(大きなマーカー音が
    /// 隣の周波数に漏れて、別のマーカーと取り違えるのを防ぐため)。500Hz離れた周波数への漏れは計算上
    /// 約-67dBなので、40dBなら漏れは拾わず、同時に鳴った音量差のある2つのマーカー(到着放送と戸開の音など)は
    /// 両方拾える。
    /// </summary>
    public double RelativeDb = 40;

    /// <summary>1回の解析に使う長さ(ミリ秒)。長いほど周波数の分解能は上がるが、反応は遅くなる。</summary>
    public double WindowMs = 20;

    /// <summary>解析の間隔(ミリ秒)。WindowMsの半分(50%重ね)が目安。</summary>
    public double HopMs = 10;

    /// <summary>この回数だけ続けて同じマーカーが見つかったら検知とする。</summary>
    public int MinHits = 4;

    /// <summary>検知した後、この回数だけ続けて見つからなくなるまでは同じマーカーを再検知しない。</summary>
    public int ReleaseHits = 10;

    /// <summary>同じマーカーを検知した後、この時間(ミリ秒)は再検知しない(取りこぼし・二重検知の防止)。</summary>
    public int CooldownMs = 1000;

    /// <summary>左右のどちらかがこのdB以上大きければ、その側とする(<see cref="MarkerEvent.DetectSide"/>のマーカーだけ)。</summary>
    public double SideDb = 10;

    /// <summary>左右の差がこのdB以内なら両側とする。SideDbとの間(中途半端な差)は「分からない」。</summary>
    public double BothDb = 6;

    /// <summary>
    /// 鏡像のチェック(<see cref="MirrorDb"/>)をするか。22.05kHz・11.025kHzなどの低いサンプリング周波数のWAVを
    /// BVEが48kHzに変換するとき、元の音(2.5〜8kHzあたり)の鏡像がマーカーの周波数に現れて誤検知することがある
    /// (早送りで車内放送が停車中まで鳴り続けたときに実機で確認)。
    /// </summary>
    public bool MirrorCheck = true;

    /// <summary>
    /// 鏡像の元になる周波数(<see cref="ToneDetector.MirrorSources"/>)のどれかが、マーカーの周波数よりこのdB以上大きければ、
    /// その回は鏡像とみなして「鳴っていない」とする。鏡像は元の音より20dB以上小さいので、10dBなら鏡像は確実に除けて、
    /// 本物のマーカー(元の位置の音はふつうずっと小さい)はまず除かない。音量の比なので、BVEの音量には左右されない。
    /// </summary>
    public double MirrorDb = 10;
}

/// <summary>検知結果。</summary>
public readonly struct ToneDetection
{
    public ToneDetection(int eventIndex, long sampleIndex, double timeSeconds, double[] levelsDb, double guardDb,
                         DoorSide side = DoorSide.Unknown, double? sideDiffDb = null)
    {
        EventIndex = eventIndex;
        SampleIndex = sampleIndex;
        TimeSeconds = timeSeconds;
        LevelsDb = levelsDb;
        GuardDb = guardDb;
        Side = side;
        SideDiffDb = sideDiffDb;
    }

    /// <summary>マーカーの定義(MarkerEventの配列)の何番目か。</summary>
    public int EventIndex { get; }

    /// <summary>Resetしてから流し込んだサンプルのうち、検知した解析窓の終わりの位置。</summary>
    public long SampleIndex { get; }

    /// <summary>Resetしてからの経過時間(秒、取り込んだサンプル数から計算。検知した解析窓の終わり)。</summary>
    public double TimeSeconds { get; }

    /// <summary>そのマーカーの各周波数のレベル(dBFS)。</summary>
    public double[] LevelsDb { get; }

    /// <summary>比較用の周波数のレベル(dBFS、大きい方)。</summary>
    public double GuardDb { get; }

    /// <summary>ドアの側(<see cref="MarkerEvent.DetectSide"/>のマーカーだけ。それ以外はUnknown)。</summary>
    public DoorSide Side { get; }

    /// <summary>左チャンネルが右チャンネルより何dB大きかったか(調べなかったときはnull)。ログ用。</summary>
    public double? SideDiffDb { get; }
}

/// <summary>
/// 決まった数個の周波数だけを監視して、マーカー音を検知する。周波数ごとにGoertzel法(1つの周波数の
/// 強さだけを求める軽い計算。FFTで全周波数を計算するより大幅に軽い)を使う。
///
/// 判定の手順(解析の間隔ごと):
///  1. 直近WindowMsぶんの音に窓関数(Hann)を掛け、各マーカー周波数と比較用の周波数の強さを求める。
///  2. マーカー周波数のうち、(a)ThresholdDb以上、(b)比較用の周波数よりMarginDb以上大きい、
///     (c)一番大きいマーカー周波数からRelativeDb以内、の3つを満たすものを「鳴っている」とする。
///  3. マーカーの周波数が全部「鳴っている」なら、そのマーカーの候補とする。複数のマーカーが同時に
///     鳴っても(到着放送と戸開の音が重なった等)、それぞれ候補になる。ただし、2周波数のマーカーの候補に
///     なったときは、その一部の周波数だけのマーカーは候補にしない(2周波数のマーカーの片割れと取り違えないため)。
///     MinHits回続いたら検知。
///  2'. 鏡像のチェック(<see cref="ToneDetectorSettings.MirrorCheck"/>): 2.を満たした周波数fだけ、鏡像の元になる周波数
///     (<see cref="MirrorSources"/>)の強さも求め、どれかがfよりMirrorDb以上大きければ「鳴っていない」とする。
///     2.を満たすことはめったにないので、ふだんの計算量は変わらない。
///  4. 左右を調べるマーカー(<see cref="MarkerEvent.DetectSide"/>)は、候補になっている間だけ左右のチャンネルでも
///     そのマーカーの周波数の強さを求めて足し合わせ、検知したときの左右の差で側を決める(左右の計算は候補の
///     ときだけなので、ふだんの計算量は変わらない)。検知そのものは今まで通り左右を混ぜた音で行う。
///
/// スレッドセーフではない(取り込みスレッドからだけ使う)。
/// </summary>
public sealed class ToneDetector
{
    private readonly int _sampleRate;
    private readonly MarkerEvent[] _events;
    private readonly ToneDetectorSettings _settings;

    // 監視する周波数(全マーカーの周波数を重複なしで並べたもの)と、各マーカーがそのどれを使うか。
    private readonly double[] _markerFreqs;
    private readonly int[][] _eventFreqIndices;
    private readonly ulong[] _eventMasks;
    private readonly double[] _guardFreqs;

    private readonly double[] _markerCoeffs;
    private readonly double[] _guardCoeffs;

    // 鏡像のチェック用: マーカーの周波数ごとの、鏡像の元になる周波数とその係数(Goertzel法)、計算結果の置き場。
    private readonly double[][] _mirrorFreqs;
    private readonly double[][] _mirrorCoeffs;
    private readonly double[][] _mirrorLevels;
    private long _mirrorRejects;
    private readonly float[] _window;
    private readonly double _amplitudeScale;

    private readonly int _windowLength;
    private readonly int _hopLength;
    private readonly float[] _ring;

    // 解析のたびに、リングバッファの直近の窓を古い順に並べ直し、窓関数を掛けたもの。周波数ごとの計算(Goertzel法)は
    // これに対して行う(周波数ごとにリングバッファの位置を割り算で求め、窓関数を掛け直す無駄を省く。計算の順番と
    // 結果は同じ)。
    private readonly float[] _frame;

    // 左右を調べるマーカーがあるときだけ使う、左右のチャンネルのリングバッファと解析用の並べ直し。
    private readonly bool _anySide;
    private readonly float[] _ringL;
    private readonly float[] _ringR;
    private readonly float[] _frameL;
    private readonly float[] _frameR;
    private readonly double[] _sideCoeff = new double[1];
    private readonly double[] _sideLevels = new double[1];
    private readonly double[] _sidePowerL;
    private readonly double[] _sidePowerR;
    private bool _stereo;       // 直近に流し込んだ音が左右別々だったか
    private bool _sideFramesReady;
    private int _ringPos;
    private int _filled;
    private int _sinceLastAnalysis;
    private long _totalSamples;

    private readonly int[] _hits;
    private readonly int[] _misses;
    private readonly bool[] _fired;
    private readonly long[] _lastFiredSample;

    private readonly double[] _markerLevels;
    private readonly double[] _guardLevels;
    private readonly double[] _peakMarkerLevels;
    // 「最小値」の初期値。double.NegativeInfinityの定数はDllExportのビルド処理(ildasm→ilasm)で
    // 壊れることがある(double.NaNで実際に失敗した)ため、十分小さい普通の値を使う。
    private const double FloorDb = -999;

    private double _peakGuardLevel = FloorDb;

    public ToneDetector(int sampleRate, IList<MarkerEvent> events, ToneDetectorSettings settings)
    {
        _sampleRate = sampleRate;
        _events = events.ToArray();
        _settings = settings;

        var nyquist = sampleRate / 2.0;
        _markerFreqs = _events.SelectMany(e => e.Frequencies).Distinct().OrderBy(f => f).ToArray();
        if (_markerFreqs.Length > 64)
        {
            throw new ArgumentException("too many marker frequencies");
        }

        foreach (var f in _markerFreqs.Where(f => f >= nyquist * 0.98))
        {
            throw new ArgumentException($"marker frequency {f}Hz is too high for the {sampleRate}Hz capture (must be below {nyquist * 0.98:0}Hz)");
        }

        _eventFreqIndices = _events.Select(e => e.Frequencies.Select(f => Array.IndexOf(_markerFreqs, f)).ToArray()).ToArray();
        _eventMasks = _eventFreqIndices.Select(idx => idx.Aggregate(0UL, (m, i) => m | (1UL << i))).ToArray();

        // 比較用の周波数: マーカーの一番下より1kHz下と、一番上より1kHz上(ナイキスト周波数を超えるなら省く)。
        var guards = new List<double>();
        if (_markerFreqs.Length > 0)
        {
            guards.Add(_markerFreqs.First() - 1000);
            var upper = _markerFreqs.Last() + 1000;
            if (upper < nyquist * 0.98)
            {
                guards.Add(upper);
            }
        }

        _guardFreqs = guards.ToArray();

        _windowLength = Math.Max(64, (int)Math.Round(sampleRate * settings.WindowMs / 1000.0));
        _hopLength = Math.Max(1, (int)Math.Round(sampleRate * settings.HopMs / 1000.0));
        _ring = new float[_windowLength];
        _frame = new float[_windowLength];
        _anySide = _events.Any(e => e.DetectSide);
        _ringL = _anySide ? new float[_windowLength] : null;
        _ringR = _anySide ? new float[_windowLength] : null;
        _frameL = _anySide ? new float[_windowLength] : null;
        _frameR = _anySide ? new float[_windowLength] : null;
        _sidePowerL = new double[_events.Length];
        _sidePowerR = new double[_events.Length];

        _window = new float[_windowLength];
        var windowSum = 0.0;
        for (var i = 0; i < _windowLength; i++)
        {
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (_windowLength - 1)));
            windowSum += _window[i];
        }

        // 振幅Aの正弦波のGoertzelの大きさは A × (窓の合計) / 2。逆算して振幅に直すための係数。
        _amplitudeScale = 2.0 / windowSum;

        _markerCoeffs = _markerFreqs.Select(f => 2 * Math.Cos(2 * Math.PI * f / sampleRate)).ToArray();
        _guardCoeffs = _guardFreqs.Select(f => 2 * Math.Cos(2 * Math.PI * f / sampleRate)).ToArray();
        _mirrorFreqs = _markerFreqs.Select(f => MirrorSources(f, sampleRate)).ToArray();
        _mirrorCoeffs = _mirrorFreqs.Select(m => m.Select(f => 2 * Math.Cos(2 * Math.PI * f / sampleRate)).ToArray()).ToArray();
        _mirrorLevels = _mirrorFreqs.Select(m => new double[m.Length]).ToArray();

        _hits = new int[_events.Length];
        _misses = new int[_events.Length];
        _fired = new bool[_events.Length];
        _lastFiredSample = new long[_events.Length];
        _markerLevels = new double[_markerFreqs.Length];
        _guardLevels = new double[_guardFreqs.Length];
        _peakMarkerLevels = new double[_markerFreqs.Length];
        Reset();
    }

    public int SampleRate => _sampleRate;
    public IReadOnlyList<double> MarkerFrequencies => _markerFreqs;
    public IReadOnlyList<double> GuardFrequencies => _guardFreqs;

    /// <summary>Resetしてから流し込んだサンプル数。</summary>
    public long ProcessedSamples => _totalSamples;

    /// <summary>マーカーの周波数fごとの、鏡像の元になる周波数(<see cref="MirrorSources"/>)。ログ用。</summary>
    public IReadOnlyList<double[]> MirrorFrequencies => _mirrorFreqs;

    /// <summary>
    /// マーカーの周波数fに鏡像を作りうる元の周波数。22.05kHz・11.025kHzのWAVを変換したときにできる鏡像
    /// (BVEの車両・路線の古い音はほとんどこの2つ):
    ///  - 22050 − f: 22.05kHzの折り返しの鏡像(11.025kHzのWAVでも同じ位置)。実機の誤検知はこれ。
    ///  - 取り込みの周波数 − 22050 − f(48kHzなら25950 − f): 22050Hzより上にできた鏡像が、取り込みのナイキスト周波数で
    ///    折り返したもの(線形補間の試算で誤検知が出た)。
    /// 0Hz以下・ナイキスト周波数以上になるものは省く。16kHz・24kHz・32kHzのWAVの鏡像は対象外。
    /// </summary>
    public static double[] MirrorSources(double f, int sampleRate)
    {
        var nyquist = sampleRate / 2.0;
        return new[] { 22050 - f, sampleRate - 22050 - f }
            .Where(m => m > 100 && m < nyquist * 0.98 && Math.Abs(m - f) > 1000)
            .Distinct()
            .ToArray();
    }

    /// <summary>前回この関数を呼んでから、鏡像とみなして除いた回数(解析の回数・周波数ごと)を返して0に戻す。</summary>
    public long TakeMirrorRejects()
    {
        var n = _mirrorRejects;
        _mirrorRejects = 0;
        return n;
    }

    /// <summary>状態を初期化する(取り込みを再開したときなど、音が途切れた後に呼ぶ)。</summary>
    public void Reset()
    {
        Array.Clear(_ring, 0, _ring.Length);
        if (_anySide)
        {
            Array.Clear(_ringL, 0, _ringL.Length);
            Array.Clear(_ringR, 0, _ringR.Length);
        }

        _ringPos = 0;
        _filled = 0;
        _sinceLastAnalysis = 0;
        _totalSamples = 0;
        for (var i = 0; i < _events.Length; i++)
        {
            _hits[i] = 0;
            _misses[i] = 0;
            _fired[i] = false;
            _lastFiredSample[i] = long.MinValue / 2;
            _sidePowerL[i] = 0;
            _sidePowerR[i] = 0;
        }

        ResetPeakLevels();
    }

    /// <summary>モノラルの音を流し込む。検知するとonDetectを呼ぶ。ドアの側は分からない(Unknown)。</summary>
    public void Process(float[] samples, int count, Action<ToneDetection> onDetect)
    {
        Process(samples, null, null, count, onDetect);
    }

    /// <summary>
    /// 音を流し込む。samples = 左右を混ぜた音(検知に使う)、left/right = 左右のチャンネル(ドアの側を調べるのに使う。
    /// nullなら側は分からない)。検知するとonDetectを呼ぶ。
    /// </summary>
    public void Process(float[] samples, float[] left, float[] right, int count, Action<ToneDetection> onDetect)
    {
        var stereo = _anySide && left != null && right != null;
        if (stereo != _stereo)
        {
            // 左右別々の音とモノラルが切り替わったら、古い左右の音が残らないように消す。
            _stereo = stereo;
            if (_anySide)
            {
                Array.Clear(_ringL, 0, _ringL.Length);
                Array.Clear(_ringR, 0, _ringR.Length);
            }
        }

        for (var i = 0; i < count; i++)
        {
            _ring[_ringPos] = samples[i];
            if (stereo)
            {
                _ringL[_ringPos] = left[i];
                _ringR[_ringPos] = right[i];
            }

            if (++_ringPos == _windowLength)
            {
                _ringPos = 0;
            }
            _totalSamples++;
            if (_filled < _windowLength)
            {
                _filled++;
            }

            if (++_sinceLastAnalysis >= _hopLength && _filled == _windowLength)
            {
                _sinceLastAnalysis = 0;
                Analyze(onDetect);
            }
        }
    }

    /// <summary>前回この関数を呼んでからの、各マーカー周波数・比較用周波数の最大レベル(dBFS)を返して初期化する。</summary>
    public (double[] markerPeaks, double guardPeak) TakePeakLevels()
    {
        var result = ((double[])_peakMarkerLevels.Clone(), _peakGuardLevel);
        ResetPeakLevels();
        return result;
    }

    private void ResetPeakLevels()
    {
        for (var i = 0; i < _peakMarkerLevels.Length; i++)
        {
            _peakMarkerLevels[i] = FloorDb;
        }

        _peakGuardLevel = FloorDb;
    }

    // リングバッファの一番古いサンプル(_ringPos)から順に、窓関数を掛けて並べる。
    private void Unwrap(float[] ring, float[] frame)
    {
        var head = _windowLength - _ringPos;
        for (var n = 0; n < head; n++)
        {
            frame[n] = ring[_ringPos + n] * _window[n];
        }

        for (var n = head; n < _windowLength; n++)
        {
            frame[n] = ring[n - head] * _window[n];
        }
    }

    private void Analyze(Action<ToneDetection> onDetect)
    {
        Unwrap(_ring, _frame);
        _sideFramesReady = false;

        ComputeLevels(_frame, _markerCoeffs, _markerLevels);
        ComputeLevels(_frame, _guardCoeffs, _guardLevels);

        var maxMarker = FloorDb;
        for (var k = 0; k < _markerFreqs.Length; k++)
        {
            maxMarker = Math.Max(maxMarker, _markerLevels[k]);
            _peakMarkerLevels[k] = Math.Max(_peakMarkerLevels[k], _markerLevels[k]);
        }

        var guard = FloorDb;
        foreach (var level in _guardLevels)
        {
            guard = Math.Max(guard, level);
        }

        _peakGuardLevel = Math.Max(_peakGuardLevel, guard);

        var onMask = 0UL;
        for (var k = 0; k < _markerFreqs.Length; k++)
        {
            var level = _markerLevels[k];
            if (level >= _settings.ThresholdDb && level >= guard + _settings.MarginDb && level >= maxMarker - _settings.RelativeDb)
            {
                if (_settings.MirrorCheck && IsMirrorImage(k, level))
                {
                    _mirrorRejects++;
                    continue;
                }

                onMask |= 1UL << k;
            }
        }

        // 候補: 周波数が全部鳴っているマーカー。ただし、より多くの周波数を含む候補が他にあれば外す。
        var candidates = 0UL;
        for (var e = 0; e < _events.Length; e++)
        {
            if (onMask != 0 && (_eventMasks[e] & onMask) == _eventMasks[e])
            {
                candidates |= 1UL << e;
            }
        }

        for (var e = 0; e < _events.Length; e++)
        {
            if ((candidates & (1UL << e)) == 0)
            {
                continue;
            }

            for (var other = 0; other < _events.Length; other++)
            {
                var m = _eventMasks[e];
                var o = _eventMasks[other];
                if (other != e && (candidates & (1UL << other)) != 0 && (m & o) == m && o != m)
                {
                    candidates &= ~(1UL << e);
                    break;
                }
            }
        }

        var cooldownSamples = (long)_settings.CooldownMs * _sampleRate / 1000;
        for (var e = 0; e < _events.Length; e++)
        {
            if ((candidates & (1UL << e)) != 0)
            {
                _hits[e]++;
                _misses[e] = 0;
                if (!_fired[e] && _events[e].DetectSide && _stereo)
                {
                    AccumulateSide(e);
                }

                if (!_fired[e] && _hits[e] >= _settings.MinHits && _totalSamples - _lastFiredSample[e] >= cooldownSamples)
                {
                    _fired[e] = true;
                    _lastFiredSample[e] = _totalSamples;
                    var levels = _eventFreqIndices[e].Select(i => _markerLevels[i]).ToArray();
                    var side = DoorSide.Unknown;
                    double? sideDiff = null;
                    if (_events[e].DetectSide && _stereo)
                    {
                        sideDiff = 10 * Math.Log10((_sidePowerL[e] + 1e-30) / (_sidePowerR[e] + 1e-30));
                        side = DecideSide(sideDiff.Value, _settings);
                    }

                    onDetect?.Invoke(new ToneDetection(e, _totalSamples, (double)_totalSamples / _sampleRate, levels, guard, side, sideDiff));
                }
            }
            else
            {
                _misses[e]++;

                // 検知前なら、1回の取りこぼしは許す(窓の境目でレベルが下がることがあるため)。
                if (!_fired[e] && _misses[e] >= 2)
                {
                    _hits[e] = 0;
                    _sidePowerL[e] = 0;
                    _sidePowerR[e] = 0;
                }

                if (_fired[e] && _misses[e] >= _settings.ReleaseHits)
                {
                    _fired[e] = false;
                    _hits[e] = 0;
                    _sidePowerL[e] = 0;
                    _sidePowerR[e] = 0;
                }
            }
        }
    }

    /// <summary>左が右より何dB大きいか(diffDb)から側を決める。</summary>
    public static DoorSide DecideSide(double diffDb, ToneDetectorSettings settings)
    {
        if (diffDb >= settings.SideDb)
        {
            return DoorSide.Left;
        }

        if (diffDb <= -settings.SideDb)
        {
            return DoorSide.Right;
        }

        return Math.Abs(diffDb) <= settings.BothDb ? DoorSide.Both : DoorSide.Unknown;
    }

    // マーカーの周波数(k番目、強さlevel dBFS)が、低いサンプリング周波数の音の鏡像らしいか。鏡像の元になる周波数の
    // どれかが、levelよりMirrorDb以上大きければ鏡像とみなす。
    private bool IsMirrorImage(int k, double level)
    {
        var coeffs = _mirrorCoeffs[k];
        if (coeffs.Length == 0)
        {
            return false;
        }

        var levels = _mirrorLevels[k];
        ComputeLevels(_frame, coeffs, levels);
        foreach (var m in levels)
        {
            if (m >= level + _settings.MirrorDb)
            {
                return true;
            }
        }

        return false;
    }

    // 候補になっているマーカーeについて、左右のチャンネルでのその周波数の強さを足し合わせる(パワーで)。
    // 左右の並べ直しは、1回の解析で最初に必要になったときだけ行う。
    private void AccumulateSide(int e)
    {
        if (!_sideFramesReady)
        {
            Unwrap(_ringL, _frameL);
            Unwrap(_ringR, _frameR);
            _sideFramesReady = true;
        }

        foreach (var k in _eventFreqIndices[e])
        {
            _sideCoeff[0] = _markerCoeffs[k];
            ComputeLevels(_frameL, _sideCoeff, _sideLevels);
            _sidePowerL[e] += Math.Pow(10, _sideLevels[0] / 10);
            ComputeLevels(_frameR, _sideCoeff, _sideLevels);
            _sidePowerR[e] += Math.Pow(10, _sideLevels[0] / 10);
        }
    }

    // 直近の窓に対するGoertzel法。周波数がちょうど解析の刻みに乗っていなくても、その周波数の強さ
    // (離散時間フーリエ変換の大きさ)が求まる。
    // 各周波数の強さ(dBFS)を求める。2つの周波数を1回のループでまとめて計算する: Goertzel法は1つ前の結果を
    // 待ってから次を計算する連鎖なので、独立した2本の連鎖を並べるとCPUが同時に進められて速くなる。
    // 周波数ごとの計算の中身と順番は1つずつ計算する場合と同じなので、結果も同じ。
    private void ComputeLevels(float[] frame, double[] coeffs, double[] levels)
    {
        var k = 0;
        for (; k + 1 < coeffs.Length; k += 2)
        {
            double ca = coeffs[k], cb = coeffs[k + 1];
            double a1 = 0, a2 = 0, b1 = 0, b2 = 0;
            for (var n = 0; n < frame.Length; n++)
            {
                double x = frame[n];
                var a0 = x + ca * a1 - a2;
                var b0 = x + cb * b1 - b2;
                a2 = a1;
                a1 = a0;
                b2 = b1;
                b1 = b0;
            }

            levels[k] = ToDb(a1, a2, ca);
            levels[k + 1] = ToDb(b1, b2, cb);
        }

        for (; k < coeffs.Length; k++)
        {
            var c = coeffs[k];
            double s1 = 0, s2 = 0;
            for (var n = 0; n < frame.Length; n++)
            {
                var s0 = frame[n] + c * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            levels[k] = ToDb(s1, s2, c);
        }
    }

    private double ToDb(double s1, double s2, double coeff)
    {
        var power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        var amplitude = Math.Sqrt(Math.Max(power, 0)) * _amplitudeScale;
        return 20 * Math.Log10(amplitude + 1e-12);
    }
}
