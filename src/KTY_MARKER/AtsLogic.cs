using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace KTY_MARKER;

/// <summary>
/// 音声マーカーの検知(実現可能性を確かめるための試作)。
///
/// ATSプラグインでは、発車放送が鳴り始めた時刻や戸閉が始まった時刻を受け取れない。そこで、発車放送や
/// ドアの音のWAVの先頭に聞こえにくい高い周波数の短い音(マーカー)を混ぜておき、BVEが鳴らしている音を
/// WASAPIのループバックで取り込んで、マーカーが鳴った瞬間を検知する。
///
/// 検知は停車中(速度が<see cref="MarkerConfig.StopSpeedKmh"/>未満)だけ行う。発車放送もドアの音も
/// 停車中にしか鳴らないので取りこぼしは無く、走行音・ブレーキの鳴きによる誤検知を避けられる。
/// 走行中も取り込み自体は続け、届いた音を解析せずに捨てる(プロセスループバックは一度止めると再開
/// できないため。LoopbackCaptureを参照)。シナリオ開始時に鳴る戸開の音も拾えるよう、最初のElapseより前
/// (読み込み直後)から停車中とみなして検知する。
///
/// 検知した結果は、ログ(%TEMP%\KTY_MARKER.log)に必ず出すほか、iniで指定すればPanelに一定時間1を
/// 書き込む(ATS Plugin Multiplexer経由で併用する他のプラグインからも読める)。比較用に、BVEが
/// 通知するDoorOpen/DoorCloseの時刻も同じログに出す。
///
/// 駅で起きたこと(到着時放送開始・発車時放送開始・戸開・戸閉開始n回目・再開扉n回目・戸閉完了)は、
/// すべて<see cref="OnStationEvent"/>に集まる(<see cref="StationEventRaised"/>イベントも起こす)。
/// プラグイン側で処理を起こすときは、ここにつなぐ。ドアの再開閉は、戸閉・戸開のマーカーとBVEの
/// DoorOpen/DoorCloseを組み合わせて(DoorSequenceTracker)1回ずつ見分ける。ドアの左右は、戸閉・戸開のマーカーが
/// 左右どちらのチャンネルで鳴ったかで見分ける([DoorSide]。StationEvent.Side)。
///
/// ATS Plugin Multiplexer経由で他のプラグインと併用される前提: Power/Brake/Reverserは触らず、
/// SetPower/SetBrake/SetReverserで受け取った値をElapseの戻り値にそのまま返す。Panelは iniで指定した
/// indexにだけ書き込む(既定はどこにも書き込まない)。Soundには書き込まない。
/// </summary>
public sealed class AtsLogic
{
    // BVEのpanel配列は256個。実際に書き込むのはiniの[EventN]Panelで指定したindexだけ。
    public const int PanelIndexMin = 0;
    public const int PanelIndexMax = 255;
    public const int SoundIndexMin = 0;
    public const int SoundIndexMax = -1;

    private AtsHandles _lastHandles;
    private int _brakeNotches;

    // SetPower/SetBrake/SetReverserを、Loadしてから一度でも受け取ったか(項目ごと)。受け取った項目は、
    // Initialize(シナリオ開始・駅へのジャンプ)でも初期値に戻さない(OnInitializeを参照)。
    private bool _powerReceived;
    private bool _brakeReceived;
    private bool _reverserReceived;

    private MarkerConfig _config = new();

    // 確認音([General] ConfirmSound = true のときだけ作る)。
    private ConfirmBeeper _beeper;

    // ドアの再開閉の判定([DoorSequence])。BVEのスレッドだけが触る。
    private DoorSequenceTracker _doors = new(3);

    // [DoorSequence] Enabled = false のときに、最後に分かったドアの側を覚えておく(Panel用)。
    private DoorSide _lastMarkerSide;

    /// <summary>
    /// 駅で起きたこと(<see cref="StationEventKind"/>)が起きたときに起こすイベント。BVEのスレッドから呼ばれる。
    /// 他の処理をつなぐときに使う(<see cref="OnStationEvent"/>も参照)。
    /// </summary>
    public event Action<StationEvent> StationEventRaised;

    // 停車した時刻(シミュレータ内、ミリ秒)。StationEvent.SinceStopMsに使う(到着時放送とDoorOpenの前後関係を
    // ログで比べられるように)。駅へジャンプした後(Initialize)は、次のElapseで停車中なら取り直す。
    private int _stopStartSimTime;
    private bool _stopStartPending = true;

    // BVEが音を出していない状態([Silence])。無音の長さは取り込みスレッドで数え、状態が変わったことだけを
    // _silenceChangesでElapseに渡す(ログ・きっかけ・Panelはすべてシミュレータの時刻と一緒にElapseで出す)。
    private long _silentSamples;        // 取り込みスレッドだけが触る
    private bool _silentOnCapture;      // 取り込みスレッドだけが触る
    private long _silentSinceTicks;     // 取り込みスレッドだけが触る
    private readonly ConcurrentQueue<(bool silent, long durationMs)> _silenceChanges = new();
    private bool _audioSilent;          // BVEのスレッド: 今、無音とみなしているか
    private volatile bool _elapseStarted; // 最初のElapseより前(シナリオの読み込み中)の無音は数えない
    private bool? _bveWindowActive;     // BVEのスレッド: 直前のElapseでBVEのウィンドウが前面にあったか

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

    private static readonly int OwnProcessId = Process.GetCurrentProcess().Id;

    private readonly object _captureLock = new();
    private LoopbackCapture _capture;

    // 読み込み時の自己診断([SelfTest])。取り込みスレッドからも見るのでvolatile。
    private volatile SelfTest _selfTest;

    // ここから下の3つは取り込みスレッドだけが触る。
    private ToneDetector _detector;
    private bool _detectorFailed;
    private long _lastLevelLogTicks;

    // 取り込みスレッド → Elapse への検知結果の受け渡し。
    private readonly ConcurrentQueue<PendingDetection> _detections = new();

    // Elapse → 取り込みスレッド への指示。
    private volatile bool _detectEnabled;
    private volatile bool _resetDetectorRequested;

    private bool _wasStopped;
    private bool _firstElapse = true;
    private int _simTime;
    private int[] _holdUntil = new int[0];
    private int[] _detectCounts = new int[0];

    private readonly struct PendingDetection
    {
        public PendingDetection(ToneDetection detection, double? lagMs)
        {
            Detection = detection;
            LagMs = lagMs;
        }

        public ToneDetection Detection { get; }

        /// <summary>検知した音が出力されてから、検知の処理が終わるまでの時間(ミリ秒、不明ならnull)。</summary>
        public double? LagMs { get; }
    }

    public AtsHandles LastHandles => _lastHandles;

    public void OnLoad()
    {
        // BVEはシナリオのやり直しのたびに、同じインスタンスに対してDispose→Loadを繰り返す。
        CancelSelfTest();
        StopCapture();
        ResetState();

        // 読み込み直したら、ハンドルの値は受け取り直す(Multiplexerも読み込み直すと最初のElapseの前に全部送り直す)。
        _powerReceived = false;
        _brakeReceived = false;
        _reverserReceived = false;

        // ログのオン/オフ([Log] Enabled)を読み直す。iniを編集すれば、シナリオを読み込み直したときから反映される。
        AtsDebug.Reload();

        var pluginDir = Path.GetDirectoryName(typeof(AtsLogic).Assembly.Location) ?? "";
        _config = MarkerConfig.Load(pluginDir);
        _holdUntil = new int[_config.Events.Count];
        _detectCounts = new int[_config.Events.Count];
        _beeper = _config.ConfirmSound ? new ConfirmBeeper(_config.ConfirmFrequency, _config.ConfirmVolume) : null;
        _doors = new DoorSequenceTracker(_config.DoorMaxAttempts, _config.DoorEstimateCloseStart);
        AtsDebug.Log($"KTY_MARKER loaded ({(IntPtr.Size == 8 ? "x64" : "x86")}, pid {Process.GetCurrentProcess().Id}, build {BuildDate()}): {_config.Describe()}");
        AtsDebug.Log($"environment: {DescribeEnvironment()}, log={AtsDebug.LogPath}");

        foreach (var e in _config.Events.Where(e => e.PanelIndex > PanelIndexMax))
        {
            AtsDebug.Log($"[WARN] Event{e.Number} Panel = {e.PanelIndex} is out of range (0-{PanelIndexMax}) - ignored");
            e.PanelIndex = -1;
        }

        _config.DoorStatePanel = ValidatePanel("DoorSequence StatePanel", _config.DoorStatePanel);
        _config.DoorCloseCountPanel = ValidatePanel("DoorSequence CloseCountPanel", _config.DoorCloseCountPanel);
        _config.DoorReopenCountPanel = ValidatePanel("DoorSequence ReopenCountPanel", _config.DoorReopenCountPanel);
        _config.DoorSidePanel = ValidatePanel("DoorSide Panel", _config.DoorSidePanel);
        if (_config.DoorSequenceEnabled &&
            (!_config.Events.Any(e => e.Role == MarkerRole.DoorClose) || !_config.Events.Any(e => e.Role == MarkerRole.DoorOpen)))
        {
            AtsDebug.Log("[WARN] DoorSequence needs markers with Role = DoorClose and Role = DoorOpen - only BVE's DoorOpen/DoorClose will be tracked");
        }

        _config.SelfTestResultPanel = ValidatePanel("SelfTest ResultPanel", _config.SelfTestResultPanel);
        _config.SilencePanel = ValidatePanel("Silence Panel", _config.SilencePanel);

        if (_config.Enabled && _config.Events.Count > 0)
        {
            StartCapture();

            // 自己診断: マーカーと同じ周波数の試験音を鳴らし、この環境で検知できるかを確かめる(別スレッド)。
            // 終わったら検知器をリセットする(試験音の検知の名残で、直後の本物のマーカーを取りこぼさないように)。
            if (_config.SelfTestEnabled)
            {
                _selfTest = new SelfTest(_config, () => _capture, () => _resetDetectorRequested = true);
                _selfTest.Start();
            }
        }
    }

    // ログ用: このDLLを作った日時(どの版のログかを見分けるため)。
    private static string BuildDate()
    {
        try
        {
            return File.GetLastWriteTime(typeof(AtsLogic).Assembly.Location).ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return "?";
        }
    }

    // ログ用: Windowsの版とビルド番号、BVEの版。技術テストで、どの環境のログかを見分けるため
    // (Environment.OSVersionは.NET Frameworkでは本当の版を返さないので、レジストリから読む)。
    private static string DescribeEnvironment()
    {
        var windows = "?";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                // Windows 11でもProductNameは「Windows 10 …」のままなので、ビルド番号(22000以上がWindows 11)で直す。
                var product = key.GetValue("ProductName") as string ?? "?";
                if (int.TryParse(key.GetValue("CurrentBuild") as string, out var build) && build >= 22000)
                {
                    product = product.Replace("Windows 10", "Windows 11");
                }

                windows = $"{product} {key.GetValue("DisplayVersion")} build {key.GetValue("CurrentBuild")}.{key.GetValue("UBR")}";
            }
        }
        catch
        {
            // 読めなくてもよい(ログ用)。
        }

        var host = "?";
        try
        {
            var module = Process.GetCurrentProcess().MainModule;
            host = $"{Path.GetFileName(module.FileName)} {module.FileVersionInfo.FileVersion}";
        }
        catch
        {
            // 読めなくてもよい(ログ用)。
        }

        return $"Windows={windows}, host={host}, {(Environment.Is64BitOperatingSystem ? "64bit" : "32bit")} OS";
    }

    private void CancelSelfTest()
    {
        var test = _selfTest;
        _selfTest = null;
        test?.Cancel();
    }

    public void OnDispose()
    {
        CancelSelfTest();
        StopCapture();
        ResetState();
        if (_beeper != null)
        {
            ConfirmBeeper.StopAll();
        }
    }

    private void ResetState()
    {
        while (_detections.TryDequeue(out _))
        {
        }

        // 最初のElapseより前から検知する(シナリオ開始と同時に鳴る戸開の音を拾うため。BVE6の実機では、
        // 最初のElapseより約80ms早くDoorOpenが来た)。走行中に始まるシナリオなら最初のElapseで止まる。
        _detectEnabled = true;
        _resetDetectorRequested = true;
        _wasStopped = false;
        _firstElapse = true;
        Array.Clear(_holdUntil, 0, _holdUntil.Length);
        Array.Clear(_detectCounts, 0, _detectCounts.Length);
        _doors.Reset();
        _lastMarkerSide = DoorSide.Unknown;
        _stopStartSimTime = 0;
        _stopStartPending = true;
        while (_silenceChanges.TryDequeue(out _))
        {
        }

        _audioSilent = false;
        _bveWindowActive = null;
        _elapseStarted = false;
    }

    private static int ValidatePanel(string name, int index)
    {
        if (index > PanelIndexMax)
        {
            AtsDebug.Log($"[WARN] {name} = {index} is out of range (0-{PanelIndexMax}) - ignored");
            return -1;
        }

        return index;
    }

    private void StartCapture()
    {
        lock (_captureLock)
        {
            if (_capture != null)
            {
                return;
            }

            _detector = null;
            _detectorFailed = false;
            _silentSamples = 0;
            _silentOnCapture = false;
            _capture = new LoopbackCapture(
                _config.CaptureMode,
                Process.GetCurrentProcess().Id,
                _config.FallbackToDevice,
                OnCapturedAudio,
                () => _resetDetectorRequested = true,
                AtsDebug.Log);
            _capture.Start();
        }
    }

    private void StopCapture()
    {
        // BVE5はDisposeを2回、重ねて呼ぶことがある。ロックで1回ずつにし、取り込みスレッドが
        // 完全に止まるまで待つ(LoopbackCapture.Disposeが待つ)。
        lock (_captureLock)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    // ---- 取り込みスレッドから呼ばれる ----

    private void OnCapturedAudio(float[] mono, float[] left, float[] right, int frames, int sampleRate, long qpcPosition)
    {
        if (_detectorFailed)
        {
            return;
        }

        if (_detector == null || _detector.SampleRate != sampleRate)
        {
            try
            {
                _detector = new ToneDetector(sampleRate, _config.Events, _config.Detector);
                AtsDebug.Log($"detector ready: {sampleRate}Hz, {(left != null ? "stereo" : "mono (door side cannot be detected)")}, " +
                             $"markers=[{string.Join(", ", _detector.MarkerFrequencies)}]Hz, guards=[{string.Join(", ", _detector.GuardFrequencies)}]Hz, " +
                             (_config.Detector.MirrorCheck
                                 ? $"mirrorSources=[{string.Join(", ", _detector.MarkerFrequencies.Select((f, i) => $"{f:0}<-{string.Join("/", _detector.MirrorFrequencies[i].Select(m => m.ToString("0")))}"))}]Hz"
                                 : "mirrorCheck=off"));
            }
            catch (Exception ex)
            {
                _detectorFailed = true;
                AtsDebug.LogException("ToneDetector", ex);
                return;
            }
        }

        if (_resetDetectorRequested)
        {
            _resetDetectorRequested = false;
            _detector.Reset();
        }

        // 念のため、走行中に届いた音は判定しない(通常はPausedで捨てられて、ここまで来ない)。
        if (!_detectEnabled)
        {
            _selfTest?.MarkInterrupted();
            return;
        }

        if (_config.SilenceEnabled)
        {
            TrackSilence(mono, frames, sampleRate);
        }

        var processStart = _detector.ProcessedSamples;
        _detector.Process(mono, left, right, frames, d =>
        {
            // NaNを「不明」の印に使わないこと: DllExportのビルド処理(ildasm→ilasm)がdouble.NaNの
            // 定数を正しく書き戻せず、ビルドが失敗する(実機で確認済み)。無限大の定数も同様に避ける。
            double? lagMs = null;
            if (qpcPosition > 0)
            {
                // 検知した解析窓の終わりのサンプルが出力された時刻から、今までの時間。
                var offsetSamples = d.SampleIndex - processStart;
                var sampleQpc = qpcPosition + (long)(offsetSamples * 10_000_000.0 / sampleRate);
                lagMs = (LoopbackCapture.NowQpc100ns() - sampleQpc) / 10_000.0;
            }

            // 自己診断の最中に検知したもの(試験音)は、試験の結果として受け取り、きっかけには流さない。
            if (_selfTest is { } test && test.TryConsume(d))
            {
                return;
            }

            _detections.Enqueue(new PendingDetection(d, lagMs));
        });

        // ログを止めているときは、レベルの集計もしない([Log] Enabled = false)。
        if (_config.LogLevels && AtsDebug.Enabled)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - _lastLevelLogTicks >= Stopwatch.Frequency)
            {
                _lastLevelLogTicks = now;
                var (peaks, guard) = _detector.TakePeakLevels();
                var parts = _detector.MarkerFrequencies.Select((f, i) => $"{f:0}Hz={peaks[i]:0.0}");
                // mirrorRejects: この1秒に、鏡像とみなして除いた回数(解析の回数・周波数ごと)。0なら書かない。
                var rejects = _detector.TakeMirrorRejects();
                AtsDebug.Log($"levels(dBFS, last 1s max): {string.Join(" ", parts)} guard={guard:0.0}" +
                             (rejects > 0 ? $" mirrorRejects={rejects}" : ""));
            }
        }
    }

    // 完全な無音(サンプルがすべて0)の長さを数え、MinMsを超えたら「無音」、音が戻ったら「戻った」をElapseに知らせる。
    // ふつうの停車中でも背景の音で-80〜-100dBFSはあるので、完全な0はBVEが音を出していないとき(BVE6で確認済み)。
    private void TrackSilence(float[] mono, int frames, int sampleRate)
    {
        if (!_elapseStarted)
        {
            _silentSamples = 0;
            return;
        }

        var allZero = true;
        for (var i = 0; i < frames; i++)
        {
            if (mono[i] != 0f)
            {
                allZero = false;
                break;
            }
        }

        if (allZero)
        {
            if (_silentSamples == 0)
            {
                _silentSinceTicks = Stopwatch.GetTimestamp();
            }

            _silentSamples += frames;
            if (!_silentOnCapture && _silentSamples >= (long)_config.SilenceMinMs * sampleRate / 1000)
            {
                _silentOnCapture = true;
                _silenceChanges.Enqueue((true, 0));
            }

            return;
        }

        if (_silentOnCapture)
        {
            _silentOnCapture = false;
            var ms = (Stopwatch.GetTimestamp() - _silentSinceTicks) * 1000 / Stopwatch.Frequency;
            _silenceChanges.Enqueue((false, ms));
        }

        _silentSamples = 0;
    }

    // BVE(このプロセス)のウィンドウが前面にあるか。BVEは前面に無いと音を出さない(BVE6で確認済み)。
    private static bool IsBveWindowActive()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out var pid) != 0 && pid == OwnProcessId;
    }

    // ---- BVEのスレッドから呼ばれる ----

    public void OnVehicleSpec(AtsVehicleSpec spec)
    {
        _brakeNotches = spec.BrakeNotches;
    }

    public void OnInitialize(int initialHandlePosition)
    {
        // シナリオ開始・駅へのジャンプ。停車した時刻は、次のElapseで取り直す。
        _stopStartPending = true;
        AtsDebug.Log($"BVE Initialize (handle {initialHandlePosition}, last sim {FormatSimTime(_simTime)})");

        // ハンドルの値は、受け取った値をそのまま返し続ける(このプラグインはハンドルに介入しない)。Initializeで
        // 0などに戻してはいけない: ATS Plugin MultiplexerはSetPower/SetBrake/SetReverserを値が変わったときしか
        // 送り直さず、BVE6もInitializeのときには送らない(どちらもソース・本体の中身で確認、2026-10-29)。戻すと、
        // 運転士がハンドルを動かすまで、レバーサ中立・ブレーキ緩解などを返し続け、運転士の操作や前のプラグイン
        // (ATCのブレーキなど)を上書きしてしまう。まだ一度も受け取っていない項目だけ、初期値を入れる。
        if (!_brakeReceived)
        {
            _lastHandles.Brake = initialHandlePosition == AtsConstants.InitEmergencyBrake ? _brakeNotches + 1 : 0;
        }

        if (!_powerReceived)
        {
            _lastHandles.Power = 0;
        }

        if (!_reverserReceived)
        {
            _lastHandles.Reverser = 0;
        }

        _lastHandles.ConstantSpeed = AtsConstants.ConstantSpeedContinue;
    }

    public AtsHandles OnElapse(AtsVehicleState state, NativeIntArray panel, NativeIntArray sound)
    {
        _simTime = state.Time;
        _elapseStarted = true;

        // 速度は小数なので、ちょうど0かどうかではなく、しきい値未満かどうかで停車中と判断する。
        var stopped = Math.Abs(state.Speed) < _config.StopSpeedKmh;
        if (stopped != _wasStopped || _firstElapse)
        {
            // 走行→停車のときは、走行中の古い状態を捨てる。最初のElapseでは捨てない(読み込み直後から
            // 検知しているので、シナリオ開始時の戸開の音を検知している途中かもしれないため)。
            if (stopped && !_firstElapse)
            {
                _resetDetectorRequested = true;
            }

            AtsDebug.Log($"{(stopped ? "stopped - detection on" : "moving - detection off")} (sim {FormatSimTime(state.Time)}, {state.Speed:0.00}km/h)");
            _wasStopped = stopped;
            _firstElapse = false;
            if (stopped)
            {
                _stopStartSimTime = state.Time;
                _stopStartPending = false;
            }
        }

        if (stopped && _stopStartPending)
        {
            _stopStartSimTime = state.Time;
            _stopStartPending = false;
        }

        _detectEnabled = stopped;
        if (!stopped)
        {
            _selfTest?.MarkInterrupted();
        }

        WritePanel(panel, _config.SelfTestResultPanel, (int)(_selfTest?.Result ?? SelfTestResult.NotRun));

        var active = IsBveWindowActive();
        if (_bveWindowActive != active)
        {
            AtsDebug.Log($"BVE window {(active ? "active" : "inactive (BVE outputs no sound while inactive)")} (sim {FormatSimTime(state.Time)})");
            _bveWindowActive = active;
        }

        while (_silenceChanges.TryDequeue(out var change))
        {
            _audioSilent = change.silent;
            var where = $"window {(active ? "active" : "inactive")}, door state={_doors.State}";
            if (change.silent)
            {
                AtsDebug.Log($"AUDIO SILENT: BVE is outputting no sound - markers cannot be detected until it returns ({where})");
                RaiseStationEvent(new StationEvent(StationEventKind.AudioSilent, 0, false, state.Time, SinceStop(state.Time), null));
            }
            else
            {
                var ms = (int)Math.Min(int.MaxValue, change.durationMs);
                RaiseStationEvent(new StationEvent(StationEventKind.AudioResumed, ms, false, state.Time, SinceStop(state.Time), null));
            }
        }

        WritePanel(panel, _config.SilencePanel, _audioSilent ? 1 : 0);

        var capture = _capture;
        if (capture != null)
        {
            capture.Paused = !stopped;
        }

        while (_detections.TryDequeue(out var pending))
        {
            var index = pending.Detection.EventIndex;
            var e = _config.Events[index];
            _detectCounts[index]++;
            _holdUntil[index] = state.Time + e.HoldMs;
            var levels = string.Join("/", pending.Detection.LevelsDb.Select(l => l.ToString("0.0")));
            var lag = pending.LagMs is { } l ? l.ToString("0") : "?";
            var onsetDelay = pending.LagMs is { } l2 ? (l2 + EstimatedAnalysisDelayMs()).ToString("0") : "?";
            var side = pending.Detection.Side;
            var sideText = pending.Detection.SideDiffDb is { } diff ? $" side={side}(L-R={diff:+0.0;-0.0}dB)" : "";
            AtsDebug.Log($"DETECTED #{e.Number} {e.Describe()} sim {FormatSimTime(state.Time)} level={levels}dBFS guard={pending.Detection.GuardDb:0.0}dBFS{sideText} " +
                         $"captureLag={lag}ms estimatedDelayFromOnset={onsetDelay}ms count={_detectCounts[index]}");
            if (e.DetectSide && side != DoorSide.Unknown)
            {
                _lastMarkerSide = side;
            }

            // 戸閉・戸開のマーカーは、ドアの再開閉の判定に回す(確認音もそちらで、何回目かに応じた高さで鳴らす)。
            if (_config.DoorSequenceEnabled && e.Role == MarkerRole.DoorClose)
            {
                HandleDoorEvent(_doors.OnCloseMarker(state.Time, side), e);
                continue;
            }

            if (_config.DoorSequenceEnabled && e.Role == MarkerRole.DoorOpen)
            {
                HandleDoorEvent(_doors.OnOpenMarker(state.Time, side), e);
                continue;
            }

            // 到着時放送・発車時放送・役割の無いマーカー。
            var kind = e.Role switch
            {
                MarkerRole.Arrival => StationEventKind.ArrivalAnnounce,
                MarkerRole.Departure => StationEventKind.DepartureAnnounce,
                MarkerRole.DoorClose => StationEventKind.OtherMarker, // DoorSequenceが無効のとき
                MarkerRole.DoorOpen => StationEventKind.OtherMarker,
                _ => StationEventKind.OtherMarker,
            };
            PlayConfirm(e.ConfirmBeeps, 0, side);
            RaiseStationEvent(new StationEvent(kind, 0, true, state.Time, SinceStop(state.Time), e.Name, side));
        }

        if (_config.DoorSequenceEnabled)
        {
            WritePanel(panel, _config.DoorStatePanel, (int)_doors.State);
            WritePanel(panel, _config.DoorCloseCountPanel, _doors.CloseCount);
            WritePanel(panel, _config.DoorReopenCountPanel, _doors.ReopenCount);
        }

        WritePanel(panel, _config.DoorSidePanel, (int)(_config.DoorSequenceEnabled ? _doors.Side : _lastMarkerSide));

        for (var i = 0; i < _config.Events.Count; i++)
        {
            var e = _config.Events[i];

            // シミュレータ内の時刻が巻き戻った(駅へジャンプした等)場合は、保持をやめる。
            if (_holdUntil[i] - state.Time > e.HoldMs)
            {
                _holdUntil[i] = 0;
            }

            if (e.PanelIndex >= 0)
            {
                panel[e.PanelIndex] = state.Time < _holdUntil[i] ? 1 : 0;
            }
        }

        return _lastHandles;
    }

    private static void WritePanel(NativeIntArray panel, int index, int value)
    {
        if (index >= 0)
        {
            panel[index] = value;
        }
    }

    // 確認音。1kHz前後の音なので、取り込んだ自分の確認音をマーカーと取り違えることはない。
    private void PlayConfirm(int beeps, double frequency, DoorSide side = DoorSide.Unknown)
    {
        if (_beeper != null && beeps > 0 && !_beeper.Play(beeps, frequency, side))
        {
            AtsDebug.Log($"[WARN] confirm sound could not be played (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        }
    }

    // ドアの再開閉の判定結果を処理する。marker = 元になったマーカー(BVEのDoorOpen/DoorCloseからならnull)。
    private void HandleDoorEvent(DoorSequenceEvent? result, MarkerEvent marker, bool logNote = true)
    {
        if (logNote && _doors.LastNote != null)
        {
            AtsDebug.Log($"door: {_doors.LastNote} (sim {FormatSimTime(_simTime)})");
        }

        if (result is not { } ev)
        {
            return;
        }

        // 確認音: ピッの回数でマーカーの種類(戸閉=2回、戸開=3回)、高さで何回目か(1回目が基準の高さ)、
        // 鳴る側(左・右のチャンネル)でドアの側を表す。BVEのDoorOpen/DoorCloseから分かったもの(マーカー無し)は鳴らさない。
        if (marker != null)
        {
            var attempt = Math.Max(1, ev.Attempt);
            PlayConfirm(marker.ConfirmBeeps, _config.ConfirmFrequency * (1 + _config.DoorPitchStep * (attempt - 1)), ev.Side);
        }

        var kind = ev.Kind switch
        {
            DoorSequenceKind.Opened => StationEventKind.DoorOpened,
            DoorSequenceKind.CloseStart => StationEventKind.DoorCloseStart,
            DoorSequenceKind.Reopen => StationEventKind.DoorReopen,
            DoorSequenceKind.OpenSide => StationEventKind.DoorOpenSide,
            _ => StationEventKind.DoorClosed,
        };
        RaiseStationEvent(new StationEvent(kind, ev.Attempt, ev.FromMarker, ev.SimTime, SinceStop(ev.SimTime), marker?.Name, ev.Side));
    }

    private int SinceStop(int simTime) => _stopStartPending ? 0 : simTime - _stopStartSimTime;

    private void RaiseStationEvent(StationEvent ev)
    {
        var door = ev.IsDoorEvent
            ? $", door state={_doors.State} closes={_doors.CloseCount} reopens={_doors.ReopenCount}"
            : "";
        AtsDebug.Log($"STATION {ev.Describe()} (sim {FormatSimTime(ev.SimTime)}, sinceStop={ev.SinceStopMs / 1000.0:+0.000;-0.000}s, " +
                     $"from {(ev.FromMarker ? "marker " + ev.MarkerName : ev.Kind is StationEventKind.AudioSilent or StationEventKind.AudioResumed ? "audio level" : "BVE event")}{door})");
        OnStationEvent(ev);
    }

    /// <summary>
    /// 駅で起きたことが起きたときに呼ばれる(BVEのスレッド)。プラグイン側で処理を起こすときは、ここに足す
    /// (または<see cref="StationEventRaised"/>につなぐ)。ev.Kindで種類、ev.Attemptで何回目かが分かる:
    ///   ArrivalAnnounce    到着時放送が鳴り始めた(Role = Arrival のマーカー)
    ///   DepartureAnnounce  発車時放送が鳴り始めた(Role = Departure のマーカー)
    ///   DoorOpened         駅に着いてドアが開いた(戸開のマーカー、またはBVEのDoorOpenの早い方)
    ///   DoorCloseStart     戸閉開始(Attempt = 1, 2, 3。2以降は再開閉の後の閉め直し)
    ///   DoorReopen         再開扉(閉まりかけの途中で開いた。Attempt = 1, 2, 3)
    ///   DoorClosed         戸閉完了(BVEのDoorClose。Attempt = それまでの戸閉の回数)
    ///   DoorOpenSide       BVEのDoorOpenで先にDoorOpened(側は不明)を出した後、戸開のマーカーでドアの側が分かった
    ///   OtherMarker        役割の無いマーカー(ev.MarkerNameで区別)
    /// 戸閉開始・再開扉は[DoorSequence] MaxAttempts回目(既定3回目)まで。
    /// ドアのイベントは、ev.Sideでドアの側(Left・Right・Both・Unknown)も分かる([DoorSide])。
    /// </summary>
    private void OnStationEvent(StationEvent ev)
    {
        StationEventRaised?.Invoke(ev);
    }

    // マーカーが鳴り始めてから検知するまでの解析による遅れの目安: 解析窓の長さ + (必要な連続回数-1)×間隔。
    private double EstimatedAnalysisDelayMs()
    {
        var d = _config.Detector;
        return d.WindowMs + (d.MinHits - 1) * d.HopMs;
    }

    private static string FormatSimTime(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    public void OnPowerChanged(int notch)
    {
        _lastHandles.Power = notch;
        _powerReceived = true;
    }

    public void OnBrakeChanged(int notch)
    {
        _lastHandles.Brake = notch;
        _brakeReceived = true;
    }

    public void OnReverserChanged(int position)
    {
        _lastHandles.Reverser = position;
        _reverserReceived = true;
    }

    public void OnKeyDown(int keyIndex)
    {
    }

    public void OnKeyUp(int keyIndex)
    {
    }

    public void OnHornBlow(int hornIndex)
    {
    }

    // 比較用: BVEが通知するドアのイベントの時刻もログに出す(マーカーの検知時刻と並べて、
    // DoorClose/DoorOpenが戸閉・戸開の始まりと終わりのどちらで来るかを確かめるため)。
    public void OnDoorOpen()
    {
        AtsDebug.Log($"BVE DoorOpen event (sim {FormatSimTime(_simTime)})");
        if (_config.DoorSequenceEnabled)
        {
            HandleDoorEvent(_doors.OnBveDoorOpen(_simTime), null);
        }
    }

    public void OnDoorClose()
    {
        AtsDebug.Log($"BVE DoorClose event (sim {FormatSimTime(_simTime)})");
        if (_config.DoorSequenceEnabled)
        {
            // 戸閉マーカーを取りこぼしていたら、補った戸閉開始(遅れ)を先に出してから戸閉完了を出す。
            var closed = _doors.OnBveDoorClose(_simTime, out var estimated);
            if (_doors.LastNote != null)
            {
                AtsDebug.Log($"door: {_doors.LastNote}{(_audioSilent || _bveWindowActive == false ? " - BVE was not outputting sound" : "")} (sim {FormatSimTime(_simTime)})");
            }

            HandleDoorEvent(estimated, null, logNote: false);
            HandleDoorEvent(closed, null, logNote: false);
        }
    }

    public void OnSignalChanged(int signalIndex)
    {
    }

    public void OnBeaconData(AtsBeaconData beacon)
    {
    }
}
