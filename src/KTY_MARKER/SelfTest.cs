using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace KTY_MARKER;

/// <summary>自己診断の結果(Panelにもこの数値を書く)。</summary>
public enum SelfTestResult
{
    /// <summary>まだ実施していない・実施中。</summary>
    NotRun = 0,

    /// <summary>全部のマーカーの試験音を検知できた。</summary>
    Passed = 1,

    /// <summary>検知できない試験音があった、または音声を取り込めなかった。</summary>
    Failed = 2,

    /// <summary>判定できなかった(試験の途中で走り出した、試験音を鳴らせなかった等)。</summary>
    Inconclusive = 3,
}

/// <summary>
/// シナリオの読み込み時の自己診断。マーカーと同じ周波数の試験音をプラグイン自身(BVEのプロセス)から鳴らし、
/// いつもの取り込み・検知で拾えるかを確かめる。本物のマーカーと同じ経路を通るので、結果はそのまま
/// 「この環境で検知できるか」を表す: Bluetoothの通話(ハンズフリー)モードのように高い音が通らない出力先では
/// 拾えず、失敗になる。失敗したら警告のウィンドウを出す(同じBVEの起動中は1回だけ)。
///
/// 左右を調べるマーカー(戸閉・戸開)があれば、その試験音は1つ目を左チャンネルだけ、2つ目を右チャンネルだけで鳴らし、
/// 左右を正しく見分けられるかも確かめる(Windowsの「モノラル オーディオ」や立体音響で左右が混ざる環境を見つけるため)。
/// 検知はできて左右だけ見分けられないときは、結果はPassedのまま、別の警告を出す(ドアの側がUnknownになるだけなので)。
///
/// 試験中に検知したものは試験の結果として受け取り(<see cref="TryConsume"/>)、発車放送などのきっかけには流さない。
/// BVEのスレッドを止めないよう、専用のスレッドで動く。
/// </summary>
internal sealed class SelfTest
{
    private const int ToneMs = 120;
    private const int GapMs = 150;
    private const int WaitForCaptureMs = 5000;
    private const int SettleMs = 300;
    private const int TailMs = 700;

    private readonly IList<MarkerEvent> _events;
    private readonly MarkerConfig _config;
    private readonly Func<LoopbackCapture> _getCapture;
    private readonly Action _onFinished;

    private readonly object _lock = new();
    private readonly bool[] _found;
    private readonly double[] _levels;
    private readonly DoorSide[] _detectedSides;
    private readonly double?[] _sideDiffs;
    private readonly DoorSide[] _expectedSides;
    private bool _active;
    private bool _interrupted;

    private Thread _thread;
    private volatile bool _cancel;
    private volatile SelfTestResult _result = SelfTestResult.NotRun;

    public SelfTest(MarkerConfig config, Func<LoopbackCapture> getCapture, Action onFinished)
    {
        _config = config;
        _events = config.Events;
        _getCapture = getCapture;
        _onFinished = onFinished;
        _found = new bool[_events.Count];
        _levels = new double[_events.Count];
        _detectedSides = new DoorSide[_events.Count];
        _sideDiffs = new double?[_events.Count];

        // 左右を調べるマーカーの試験音を、1つ目は左だけ、2つ目は右だけで鳴らす(3つ目以降・他のマーカーは左右両方)。
        _expectedSides = new DoorSide[_events.Count];
        var next = DoorSide.Left;
        for (var i = 0; i < _events.Count && next != DoorSide.Unknown; i++)
        {
            if (_events[i].DetectSide)
            {
                _expectedSides[i] = next;
                next = next == DoorSide.Left ? DoorSide.Right : DoorSide.Unknown;
            }
        }
    }

    public SelfTestResult Result => _result;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "KTY_MARKER self-test" };
        _thread.Start();
    }

    /// <summary>止める(Disposeの時)。試験の途中なら判定せずに終わる。</summary>
    public void Cancel()
    {
        _cancel = true;
        _thread?.Join(2000);
    }

    /// <summary>取り込みスレッドから: 試験中なら、検知したものを試験の結果として受け取ってtrueを返す。</summary>
    public bool TryConsume(ToneDetection detection)
    {
        lock (_lock)
        {
            if (!_active)
            {
                return false;
            }

            _found[detection.EventIndex] = true;
            _levels[detection.EventIndex] = detection.LevelsDb.Length > 0 ? detection.LevelsDb.Min() : 0;
            _detectedSides[detection.EventIndex] = detection.Side;
            _sideDiffs[detection.EventIndex] = detection.SideDiffDb;
            return true;
        }
    }

    /// <summary>試験の途中で検知を止めた(走り出した)ことを知らせる。その回は判定しない。</summary>
    public void MarkInterrupted()
    {
        lock (_lock)
        {
            if (_active)
            {
                _interrupted = true;
            }
        }
    }

    private void Run()
    {
        try
        {
            // 取り込みの準備ができるのを待つ。
            var waited = 0;
            LoopbackCapture capture;
            while ((capture = _getCapture()) != null && capture.SampleRate == 0 && !capture.Failed && waited < WaitForCaptureMs && !_cancel)
            {
                Thread.Sleep(50);
                waited += 50;
            }

            if (_cancel)
            {
                return;
            }

            if (capture == null || capture.Failed)
            {
                Finish(SelfTestResult.Failed, "audio capture is not working");
                Warn("KTY_MARKER が音声を取り込めませんでした。\n\n" +
                     "このままでは、発車放送・到着放送・ドアの音のタイミングを検知できません。\n" +
                     "Windowsのサウンドの出力先が使える状態か確かめてください。\n\n" + Footer());
                return;
            }

            if (capture.SampleRate == 0)
            {
                Finish(SelfTestResult.Inconclusive, "audio capture did not start in time");
                return;
            }

            Thread.Sleep(SettleMs);
            lock (_lock)
            {
                _active = true;
                _interrupted = false;
                Array.Clear(_found, 0, _found.Length);
            }

            var toneSets = _events.Select(e => e.Frequencies).ToList();
            if (!ConfirmBeeper.PlayTestTones(toneSets, _expectedSides, _config.SelfTestLevelDb, ToneMs, GapMs))
            {
                lock (_lock)
                {
                    _active = false;
                }

                Finish(SelfTestResult.Inconclusive, $"test tones could not be played (error {Marshal.GetLastWin32Error()})");
                return;
            }

            var wait = ConfirmBeeper.TestTonesDurationMs(toneSets.Count, ToneMs, GapMs) + TailMs;
            for (var t = 0; t < wait && !_cancel; t += 50)
            {
                Thread.Sleep(50);
            }

            bool interrupted;
            var missing = new List<MarkerEvent>();
            var sideMismatch = new List<string>();
            var detail = new List<string>();
            lock (_lock)
            {
                _active = false;
                interrupted = _interrupted;
                for (var i = 0; i < _events.Count; i++)
                {
                    var sideText = "";
                    if (_expectedSides[i] != DoorSide.Unknown && _found[i])
                    {
                        var diff = _sideDiffs[i] is { } d ? $"{d:+0.0;-0.0}dB" : "n/a";
                        sideText = $" side={_detectedSides[i]}(played {_expectedSides[i]}, L-R={diff})";
                        if (_detectedSides[i] != _expectedSides[i])
                        {
                            sideMismatch.Add($"{_events[i].Describe()}: {_expectedSides[i]} → {_detectedSides[i]}");
                        }
                    }

                    detail.Add(_found[i] ? $"{_events[i].Describe()}={_levels[i]:0.0}dBFS{sideText}" : $"{_events[i].Describe()}=missed");
                    if (!_found[i])
                    {
                        missing.Add(_events[i]);
                    }
                }
            }

            if (_cancel)
            {
                return;
            }

            var summary = $"mode={capture.ActiveMode}, {capture.SampleRate}Hz, level={_config.SelfTestLevelDb:0.#}dBFS: {string.Join(", ", detail)}";
            if (interrupted)
            {
                Finish(SelfTestResult.Inconclusive, "the train started moving during the test - " + summary);
            }
            else if (missing.Count == 0)
            {
                Finish(SelfTestResult.Passed, summary);
                if (sideMismatch.Count > 0)
                {
                    AtsDebug.Log("self-test: door side could NOT be told apart (left/right are mixed on this output) - " + string.Join(", ", sideMismatch));
                    Warn("KTY_MARKER の動作確認で、試験音の左右を見分けられませんでした。\n\n" +
                         "放送・ドアの音のタイミングは検知できますが、ドアが左右どちら側かは分かりません(「不明」になります)。\n\n" +
                         "考えられる原因:\n" +
                         "・Windowsの設定で「モノラル オーディオ」がオンになっている\n" +
                         "・立体音響(Windows Sonic、Dolby Atmosなど)やサウンドの拡張機能が左右を混ぜている\n" +
                         "・出力先がモノラル(Bluetoothの通話モードなど)\n\n" +
                         "ドアの左右を使わない場合は、KTY_MARKER.ini の [DoorSide] で Enabled = false にしてください\n" +
                         "(この確認もしなくなります)。\n\n" + Footer());
                }
            }
            else
            {
                Finish(SelfTestResult.Failed, summary);
                Warn("KTY_MARKER の動作確認で、試験音を検知できませんでした。\n\n" +
                     "今の音声の出力先では高い音(18〜20kHz)が届かず、発車放送・到着放送・ドアの音のタイミングを\n" +
                     "検知できない可能性があります。\n\n" +
                     "考えられる原因:\n" +
                     "・Bluetoothイヤホン/ヘッドセットが通話(ハンズフリー)モードになっている\n" +
                     "・出力先のサンプリング周波数が低い(16kHzなど)\n" +
                     "・Windowsのサウンドの拡張機能(イコライザーなど)が高い音を削っている\n\n" +
                     "検知できなかった試験音: " + string.Join("、", missing.Select(e => e.Describe())) + "\n\n" + Footer());
            }
        }
        catch (Exception ex)
        {
            Finish(SelfTestResult.Inconclusive, "self-test error: " + ex.Message);
            AtsDebug.LogException("SelfTest", ex);
        }
    }

    private void Finish(SelfTestResult result, string message)
    {
        _result = result;
        AtsDebug.Log($"self-test {result.ToString().ToUpperInvariant()}: {message}");
        _onFinished?.Invoke();
    }

    private void Warn(string text)
    {
        if (_config.SelfTestShowWarning)
        {
            WarningWindow.ShowOnce("KTY_MARKER", text);
        }
    }

    private static string Footer() =>
        "この警告は、KTY_MARKER.ini の [SelfTest] で ShowWarning = false にすると出なくなります。";
}

/// <summary>
/// 警告のウィンドウ。BVEのスレッドを止めないよう別のスレッドで出し、同じBVEの起動中(プロセス)は1回だけ出す。
/// BVEの前に出るよう最前面にする(フォーカスは奪わない)。
/// </summary>
internal static class WarningWindow
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    private const uint MbOk = 0x0;
    private const uint MbIconWarning = 0x30;
    private const uint MbTopmost = 0x40000;

    private static int _shown;

    public static void ShowOnce(string caption, string text)
    {
        if (Interlocked.Exchange(ref _shown, 1) != 0)
        {
            AtsDebug.Log("warning window was already shown in this session - not shown again");
            return;
        }

        AtsDebug.Log("showing warning window");
        var thread = new Thread(() =>
        {
            try
            {
                MessageBoxW(IntPtr.Zero, text, caption, MbOk | MbIconWarning | MbTopmost);
            }
            catch (Exception ex)
            {
                AtsDebug.LogException("WarningWindow", ex);
            }
        })
        {
            IsBackground = true,
            Name = "KTY_MARKER warning",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
