using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KTY_MARKER;

/// <summary>
/// KTY_MARKER.ini(DLLと同じフォルダ)の読み込み。x86版とx64版で同じ1つのファイルを共有する。
/// ファイルが無い・値が不正な項目は既定値のままにするので、設定の不備でプラグインが止まることはない。
/// 値はASCIIで書くこと(GetPrivateProfileStringはファイルをShift-JISとして読むため、UTF-8の日本語の
/// 値は文字化けする。コメントは日本語でよい)。
/// </summary>
public sealed class MarkerConfig
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPrivateProfileString(string section, string key, string defaultValue, StringBuilder result, int size, string filePath);

    public const string FileName = "KTY_MARKER.ini";
    public const int MaxEvents = 8;

    // ---- [General] ----
    public bool Enabled = true;
    public CaptureMode CaptureMode = CaptureMode.Process;

    /// <summary>プロセスループバックが使えないとき、出力デバイス全体の取り込みに切り替えるか。</summary>
    public bool FallbackToDevice = true;

    /// <summary>この速度(km/h、絶対値)未満を「停車中」とみなし、その間だけ検知する。</summary>
    public double StopSpeedKmh = 0.1;

    /// <summary>停車中、各周波数のレベルを1秒ごとにログへ出す(しきい値の調整用)。</summary>
    public bool LogLevels;

    /// <summary>マーカーを検知したら確認音(「ピッ」を[EventN] ConfirmBeeps回)を鳴らす。配布時はfalse。</summary>
    public bool ConfirmSound;

    /// <summary>確認音の音量(%、0～100)。</summary>
    public int ConfirmVolume = 30;

    /// <summary>確認音の高さ(Hz)。マーカー(18～20kHz)から十分離すこと。</summary>
    public double ConfirmFrequency = 1000;

    // ---- [DoorSequence] ドアの再開閉 ----
    /// <summary>ドアの再開閉を1回ずつ見分ける(DoorSequenceTracker)。</summary>
    public bool DoorSequenceEnabled = true;

    /// <summary>戸閉開始・再開扉を何回目まで知らせるか。</summary>
    public int DoorMaxAttempts = 3;

    /// <summary>
    /// 確認音の高さを、何回目かに応じて上げる割合。1回目はConfirmFrequency、2回目は×(1+この値)、
    /// 3回目は×(1+この値×2)…(既定0.5: 1000Hz → 1500Hz → 2000Hz)。回数(ピッの数)は種類を表す。
    /// </summary>
    public double DoorPitchStep = 0.5;

    /// <summary>ドアの状態を書き込むPanelのindex(0=閉、1=開、2=閉まりかけ、3=再び開いた)。-1で書き込まない。</summary>
    public int DoorStatePanel = -1;

    /// <summary>今回の戸閉開始の回数を書き込むPanelのindex(次にドアが開くまで残す)。-1で書き込まない。</summary>
    public int DoorCloseCountPanel = -1;

    /// <summary>今回の再開扉の回数を書き込むPanelのindex(次にドアが開くまで残す)。-1で書き込まない。</summary>
    public int DoorReopenCountPanel = -1;

    /// <summary>
    /// 戸閉マーカーを取りこぼしたままBVEのDoorCloseが来たら、戸閉開始をその時点で補う(遅れる。FromMarker = false)。
    /// </summary>
    public bool DoorEstimateCloseStart = true;

    // ---- [DoorSide] ドアの側(左右) ----
    /// <summary>
    /// 戸閉・戸開のマーカー(Role = DoorClose / DoorOpen)が左右どちらのチャンネルで鳴ったかで、ドアの側を見分ける。
    /// 左側のドアの音のWAVは左チャンネルだけ、右側は右チャンネルだけにマーカーを入れておく(MarkerTool add --channel)。
    /// 判定の基準(SideDb・BothDb)は<see cref="Detector"/>に入れる。
    /// </summary>
    public bool DoorSideEnabled = true;

    /// <summary>ドアの側を書き込むPanelのindex(0=不明、1=左、2=右、3=両側。次にドアが開くまで残す)。-1で書き込まない。</summary>
    public int DoorSidePanel = -1;

    // ---- [Silence] BVEが音を出していない状態の検出 ----
    /// <summary>
    /// 停車中にBVEの音が完全な無音(サンプルがすべて0)になったら知らせる(AudioSilent/AudioResumed)。BVEはウィンドウから
    /// フォーカスが外れると音を出さなくなり(BVE6で確認済み)、その間はマーカーを検知できない。
    /// </summary>
    public bool SilenceEnabled = true;

    /// <summary>この時間(ミリ秒)続けて完全な無音なら「BVEが音を出していない」とみなす。</summary>
    public int SilenceMinMs = 300;

    /// <summary>無音の間1を書き込むPanelのindex。-1で書き込まない。</summary>
    public int SilencePanel = -1;

    // ---- [SelfTest] 読み込み時の自己診断 ----
    /// <summary>
    /// シナリオの読み込み時に、マーカーと同じ周波数の試験音をプラグイン自身が鳴らし、検知できるかを確かめる。
    /// Bluetoothの通話モードのように高い音が通らない出力先では検知できないので、それを知らせる。
    /// </summary>
    public bool SelfTestEnabled = true;

    /// <summary>試験音の大きさ(dBFS)。本物のマーカー(-30dBFS)より小さくして、余裕があることを確かめる。</summary>
    public double SelfTestLevelDb = -40;

    /// <summary>試験に失敗したら警告のウィンドウを出す(同じBVEの起動中は1回だけ)。</summary>
    public bool SelfTestShowWarning = true;

    /// <summary>試験の結果を書き込むPanelのindex(0=未実施・実施中、1=OK、2=NG、3=判定できず)。-1で書き込まない。</summary>
    public int SelfTestResultPanel = -1;

    // ---- [Detect] ----
    public ToneDetectorSettings Detector = new();

    // ---- [Event1]～[Event8] ----
    public List<MarkerEvent> Events = DefaultEvents();

    /// <summary>
    /// 既定のマーカー。1つの周波数だけ(2つの周波数を同時に鳴らすと、スピーカーによっては差の周波数
    /// (500Hzなど)が歪みとして聞こえることがあるため)。周波数は500Hz以上離す。
    /// </summary>
    public static List<MarkerEvent> DefaultEvents() => new()
    {
        new MarkerEvent { Number = 1, Name = "Departure", Role = MarkerRole.Departure, FreqA = 18500, ConfirmBeeps = 1 },
        new MarkerEvent { Number = 2, Name = "DoorCloseStart", Role = MarkerRole.DoorClose, FreqA = 19000, ConfirmBeeps = 2, DetectSide = true },
        new MarkerEvent { Number = 3, Name = "DoorOpenStart", Role = MarkerRole.DoorOpen, FreqA = 19500, ConfirmBeeps = 3, DetectSide = true },
        new MarkerEvent { Number = 4, Name = "Arrival", Role = MarkerRole.Arrival, FreqA = 18000, ConfirmBeeps = 4 },
    };

    public static MarkerConfig Load(string directory)
    {
        var config = new MarkerConfig();
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            AtsDebug.Log($"{FileName} was not found in {directory} - using defaults");
            config.ApplyDoorSide();
            return config;
        }

        config.Enabled = ReadBool(path, "General", "Enabled", config.Enabled);
        var mode = ReadString(path, "General", "CaptureMode", null);
        if (mode != null)
        {
            if (Enum.TryParse<CaptureMode>(mode, true, out var parsed))
            {
                config.CaptureMode = parsed;
            }
            else
            {
                AtsDebug.Log($"[WARN] [General] CaptureMode = \"{mode}\" is unknown (use Process or Device)");
            }
        }

        config.FallbackToDevice = ReadBool(path, "General", "FallbackToDevice", config.FallbackToDevice);
        config.StopSpeedKmh = Math.Max(0, ReadDouble(path, "General", "StopSpeedKmh", config.StopSpeedKmh));
        config.LogLevels = ReadBool(path, "General", "LogLevels", config.LogLevels);
        config.ConfirmSound = ReadBool(path, "General", "ConfirmSound", config.ConfirmSound);
        config.ConfirmVolume = Math.Max(0, Math.Min(100, ReadInt(path, "General", "ConfirmVolume", config.ConfirmVolume)));
        config.ConfirmFrequency = Clamp(ReadDouble(path, "General", "ConfirmFrequency", config.ConfirmFrequency), 100, 8000);

        config.SelfTestEnabled = ReadBool(path, "SelfTest", "Enabled", config.SelfTestEnabled);
        config.SelfTestLevelDb = Clamp(ReadDouble(path, "SelfTest", "LevelDb", config.SelfTestLevelDb), -200, -6);
        config.SelfTestShowWarning = ReadBool(path, "SelfTest", "ShowWarning", config.SelfTestShowWarning);
        config.SelfTestResultPanel = ReadInt(path, "SelfTest", "ResultPanel", config.SelfTestResultPanel);

        config.DoorSequenceEnabled = ReadBool(path, "DoorSequence", "Enabled", config.DoorSequenceEnabled);
        config.DoorMaxAttempts = Math.Max(1, Math.Min(9, ReadInt(path, "DoorSequence", "MaxAttempts", config.DoorMaxAttempts)));
        config.DoorPitchStep = Clamp(ReadDouble(path, "DoorSequence", "PitchStep", config.DoorPitchStep), 0, 2);
        config.DoorStatePanel = ReadInt(path, "DoorSequence", "StatePanel", config.DoorStatePanel);
        config.DoorCloseCountPanel = ReadInt(path, "DoorSequence", "CloseCountPanel", config.DoorCloseCountPanel);
        config.DoorReopenCountPanel = ReadInt(path, "DoorSequence", "ReopenCountPanel", config.DoorReopenCountPanel);
        config.DoorEstimateCloseStart = ReadBool(path, "DoorSequence", "EstimateCloseStart", config.DoorEstimateCloseStart);

        config.DoorSideEnabled = ReadBool(path, "DoorSide", "Enabled", config.DoorSideEnabled);
        config.Detector.SideDb = Clamp(ReadDouble(path, "DoorSide", "SideDb", config.Detector.SideDb), 1, 60);
        config.Detector.BothDb = Clamp(ReadDouble(path, "DoorSide", "BothDb", config.Detector.BothDb), 0, config.Detector.SideDb);
        config.DoorSidePanel = ReadInt(path, "DoorSide", "Panel", config.DoorSidePanel);

        config.SilenceEnabled = ReadBool(path, "Silence", "Enabled", config.SilenceEnabled);
        config.SilenceMinMs = Math.Max(20, Math.Min(10000, ReadInt(path, "Silence", "MinMs", config.SilenceMinMs)));
        config.SilencePanel = ReadInt(path, "Silence", "Panel", config.SilencePanel);

        var d = config.Detector;
        d.ThresholdDb = ReadDouble(path, "Detect", "ThresholdDb", d.ThresholdDb);
        d.MarginDb = ReadDouble(path, "Detect", "MarginDb", d.MarginDb);
        d.RelativeDb = ReadDouble(path, "Detect", "RelativeDb", d.RelativeDb);
        d.WindowMs = Clamp(ReadDouble(path, "Detect", "WindowMs", d.WindowMs), 5, 200);
        d.HopMs = Clamp(ReadDouble(path, "Detect", "HopMs", d.HopMs), 1, d.WindowMs);
        d.MinHits = Math.Max(1, ReadInt(path, "Detect", "MinHits", d.MinHits));
        d.ReleaseHits = Math.Max(1, ReadInt(path, "Detect", "ReleaseHits", d.ReleaseHits));
        d.CooldownMs = Math.Max(0, ReadInt(path, "Detect", "CooldownMs", d.CooldownMs));
        d.MirrorCheck = ReadBool(path, "Detect", "MirrorCheck", d.MirrorCheck);
        d.MirrorDb = Clamp(ReadDouble(path, "Detect", "MirrorDb", d.MirrorDb), 0, 60);

        // [EventN]が1つでも書かれていれば、既定のマーカーは使わずiniの内容だけにする。
        var events = new List<MarkerEvent>();
        var anySection = false;
        for (var n = 1; n <= MaxEvents; n++)
        {
            var section = "Event" + n.ToString(CultureInfo.InvariantCulture);
            // double.NaNを「未設定」の印に使わない(DllExportのビルドが失敗するため。AtsLogic.csを参照)。
            if (ReadString(path, section, "FreqA", null) == null)
            {
                continue;
            }

            var freqA = ReadDouble(path, section, "FreqA", 0);

            anySection = true;
            if (freqA <= 0)
            {
                continue; // FreqA = 0 で無効
            }

            var name = ReadString(path, section, "Name", section);
            events.Add(new MarkerEvent
            {
                Number = n,
                Name = name,
                Role = ReadRole(path, section, name),
                FreqA = freqA,
                FreqB = Math.Max(0, ReadDouble(path, section, "FreqB", 0)),
                PanelIndex = ReadInt(path, section, "Panel", -1),
                HoldMs = Math.Max(0, ReadInt(path, section, "HoldMs", 1000)),

                // 既定はイベントの番号と同じ回数(Event1=1回、Event2=2回…)。
                ConfirmBeeps = Math.Max(0, Math.Min(8, ReadInt(path, section, "ConfirmBeeps", Math.Min(n, 8)))),
            });
        }

        if (anySection)
        {
            config.Events = events;
        }

        config.ApplyDoorSide();
        return config;
    }

    /// <summary>[DoorSide] Enabled に合わせて、戸閉・戸開のマーカーに「左右も調べる」印を付ける(外す)。</summary>
    public void ApplyDoorSide()
    {
        foreach (var e in Events)
        {
            e.DetectSide = DoorSideEnabled && e.Role is MarkerRole.DoorClose or MarkerRole.DoorOpen;
        }
    }

    public string Describe()
    {
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var d = Detector;
        var events = string.Join(", ", Events.Select(e => $"#{e.Number} {e.Describe()} role={e.Role}{(e.DetectSide ? " side" : "")} panel={e.PanelIndex} hold={e.HoldMs}ms beeps={e.ConfirmBeeps}"));
        return $"enabled={Enabled}, mode={CaptureMode}, fallback={FallbackToDevice}, stop<{F(StopSpeedKmh)}km/h, " +
               $"logLevels={LogLevels}, confirmSound={ConfirmSound}(volume={ConfirmVolume}%, {F(ConfirmFrequency)}Hz), " +
               $"detect(threshold={F(d.ThresholdDb)}dB, margin={F(d.MarginDb)}dB, relative={F(d.RelativeDb)}dB, " +
               $"window={F(d.WindowMs)}ms, hop={F(d.HopMs)}ms, minHits={d.MinHits}, releaseHits={d.ReleaseHits}, cooldown={d.CooldownMs}ms, " +
               $"mirror={(d.MirrorCheck ? $"{F(d.MirrorDb)}dB" : "off")}), " +
               $"selfTest(enabled={SelfTestEnabled}, level={F(SelfTestLevelDb)}dBFS, warning={SelfTestShowWarning}, panel={SelfTestResultPanel}), " +
               $"doorSequence(enabled={DoorSequenceEnabled}, maxAttempts={DoorMaxAttempts}, pitchStep={F(DoorPitchStep)}, " +
               $"panels: state={DoorStatePanel} closeCount={DoorCloseCountPanel} reopenCount={DoorReopenCountPanel}, estimateCloseStart={DoorEstimateCloseStart}), " +
               $"doorSide(enabled={DoorSideEnabled}, side>={F(d.SideDb)}dB, both<={F(d.BothDb)}dB, panel={DoorSidePanel}), " +
               $"silence(enabled={SilenceEnabled}, min={SilenceMinMs}ms, panel={SilencePanel}), " +
               $"events=[{events}]";
    }

    /// <summary>行の途中の「 ;」以降をコメントとして取り除く(KTY_HOSO・KTY_TUNNELのiniと同じ書き方)。</summary>
    public static string StripInlineComment(string value)
    {
        if (value.TrimStart().StartsWith(";", StringComparison.Ordinal))
        {
            return "";
        }

        var comment = value.IndexOf(" ;", StringComparison.Ordinal);
        if (comment >= 0)
        {
            value = value.Substring(0, comment);
        }

        return value.Trim();
    }

    // [EventN] Role。省略したときは、既定のマーカーの名前(Arrival/Departure/DoorCloseStart/DoorOpenStart)なら
    // その役割、それ以外は役割なし(古いiniでもドアの再開閉の判定が動くように)。
    private static MarkerRole ReadRole(string path, string section, string name)
    {
        var text = ReadString(path, section, "Role", null);
        if (text != null)
        {
            if (Enum.TryParse<MarkerRole>(text, true, out var role))
            {
                return role;
            }

            AtsDebug.Log($"[WARN] [{section}] Role = \"{text}\" is unknown (use Arrival, Departure, DoorClose, DoorOpen or None)");
            return MarkerRole.None;
        }

        switch (name.ToLowerInvariant())
        {
            case "arrival": return MarkerRole.Arrival;
            case "departure": return MarkerRole.Departure;
            case "doorclosestart": return MarkerRole.DoorClose;
            case "dooropenstart": return MarkerRole.DoorOpen;
            default: return MarkerRole.None;
        }
    }

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    private static string ReadString(string path, string section, string key, string fallback)
    {
        var buffer = new StringBuilder(1024);
        GetPrivateProfileString(section, key, "\u0001", buffer, buffer.Capacity, path);
        var text = buffer.ToString();
        return text == "\u0001" ? fallback : StripInlineComment(text);
    }

    private static int ReadInt(string path, string section, string key, int fallback)
    {
        var text = ReadString(path, section, key, null);
        return text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static double ReadDouble(string path, string section, string key, double fallback)
    {
        var text = ReadString(path, section, key, null);
        return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static bool ReadBool(string path, string section, string key, bool fallback)
    {
        var text = ReadString(path, section, key, null);
        if (text == null)
        {
            return fallback;
        }

        switch (text.ToLowerInvariant())
        {
            case "true": case "1": case "yes": case "on": return true;
            case "false": case "0": case "no": case "off": return false;
            default: return fallback;
        }
    }
}
