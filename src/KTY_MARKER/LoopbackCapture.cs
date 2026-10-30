using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KTY_MARKER;

/// <summary>音声の取り込み方。</summary>
public enum CaptureMode
{
    /// <summary>
    /// 指定したプロセス(既定はBVE自身)が鳴らしている音だけを取り込む(プロセスループバック)。
    /// 他のアプリ(動画・音楽など)の音が混ざらない。Windows 10 Build 20348以降(Windows 11)で使える。
    /// </summary>
    Process,

    /// <summary>既定の出力デバイスに流れている音を全部取り込む(従来のループバック)。</summary>
    Device,
}

/// <summary>
/// 取り込んだ音声を受け取るコールバック。モノラルに混ぜたサンプル(-1.0～1.0)、左右のチャンネル(1つ目・2つ目の
/// チャンネル。取り込みがモノラルならnull)と、その塊の先頭が取り込まれた時刻(QueryPerformanceCounterを
/// 100ナノ秒単位にしたもの。0なら不明)を渡す。配列は次の呼び出しで上書きされる。取り込みスレッドから呼ばれる。
/// </summary>
public delegate void CapturedAudioHandler(float[] mono, float[] left, float[] right, int frames, int sampleRate, long qpcPosition100ns);

/// <summary>
/// WASAPIのループバックで音声を取り込み続ける。専用のスレッド(MTA)で動き、BVEのスレッドは止めない。
///
/// <see cref="Paused"/>をtrueにしている間は、届いた音を変換せずに捨てる(コールバックを呼ばない)。
/// falseに戻すと<c>onResumed</c>を呼んでから、また音を渡し始める。
///
/// 取り込み(IAudioClient)そのものは、Disposeまで一度も止めない。プロセスループバックは、一度Stopすると
/// Startし直しても音が二度と届かない(BVE6とMarkerToolのselftest --pausetestで実機確認済み、2026-10-28。
/// 出力デバイス全体の取り込みでは再開できた)。音を受け取って捨てるだけなら、CPUの負担はほぼ無い。
///
/// <see cref="Dispose"/>は何回・どのスレッドから呼んでも安全(BVE5はDisposeを2回、しかも重ねて
/// 呼ぶことがある - KTY_HOSOで実機確認済み)。
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private const int AudclntSBufferEmpty = 0x08890001;
    private const long BufferDuration100ns = 1_000_000; // 100ms

    private readonly CaptureMode _mode;
    private readonly int _targetProcessId;
    private readonly bool _fallbackToDevice;
    private readonly CapturedAudioHandler _onAudio;
    private readonly Action _onResumed;
    private readonly Action<string> _log;

    private readonly object _lifecycleLock = new();
    private Thread _thread;
    private bool _disposed;
    private volatile bool _stopRequested;
    private volatile bool _paused;

    public LoopbackCapture(CaptureMode mode, int targetProcessId, bool fallbackToDevice, CapturedAudioHandler onAudio, Action onResumed, Action<string> log)
    {
        _mode = mode;
        _targetProcessId = targetProcessId;
        _fallbackToDevice = fallbackToDevice;
        _onAudio = onAudio;
        _onResumed = onResumed;
        _log = log ?? (_ => { });
    }

    /// <summary>trueの間は届いた音を捨てる(取り込みは続ける)。最初はfalse。</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>実際に使っている取り込み方(プロセスループバックが使えず全体に切り替えた場合はDevice)。</summary>
    public CaptureMode ActiveMode { get; private set; }

    /// <summary>取り込みのサンプリング周波数(取り込み開始前は0)。</summary>
    public int SampleRate { get; private set; }

    /// <summary>取り込みが失敗して止まっている場合はtrue。</summary>
    public bool Failed { get; private set; }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _thread != null)
            {
                return;
            }

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "KTY_MARKER capture",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public void Dispose()
    {
        // ロックの中でスレッドの終了まで待つ: 2回目のDisposeが1回目の途中で呼ばれても、
        // 取り込みが完全に止まるまで2回目も戻らないようにするため。取り込みスレッド側は
        // このロックを使わないので、待っている間に行き詰まる(デッドロックする)ことはない。
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopRequested = true;
            if (_thread != null && !_thread.Join(3000))
            {
                _log("[WARN] capture thread did not stop within 3s");
            }

            _thread = null;
        }
    }

    /// <summary>現在時刻を、GetBufferが返す時刻と同じ単位(100ナノ秒)で返す。</summary>
    public static long NowQpc100ns()
    {
        return (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
    }

    private void Run()
    {
        try
        {
            var stream = OpenStream();
            if (stream == null)
            {
                Failed = true;
                return;
            }

            using (stream)
            {
                Loop(stream);
            }
        }
        catch (Exception ex)
        {
            Failed = true;
            _log("[ERROR] capture thread: " + ex);
        }
    }

    private CaptureStream OpenStream()
    {
        if (_mode == CaptureMode.Process)
        {
            try
            {
                var stream = CaptureStream.OpenProcessLoopback(_targetProcessId, _log);
                ActiveMode = CaptureMode.Process;
                return stream;
            }
            catch (Exception ex)
            {
                _log($"[WARN] process loopback (pid {_targetProcessId}) is not available: {Describe(ex)}");
                if (!_fallbackToDevice)
                {
                    return null;
                }

                _log("falling back to device loopback (the whole default output device)");
            }
        }

        try
        {
            var stream = CaptureStream.OpenDeviceLoopback(_log);
            ActiveMode = CaptureMode.Device;
            return stream;
        }
        catch (Exception ex)
        {
            _log("[ERROR] device loopback is not available: " + Describe(ex));
            return null;
        }
    }

    private void Loop(CaptureStream stream)
    {
        SampleRate = stream.SampleRate;
        _log($"capture ready: mode={ActiveMode}, {stream.Describe()}");

        // 取り込みは開始したらDisposeまで止めない(プロセスループバックはStopすると再開できないため。
        // クラスの説明を参照)。一時停止中も受け取り続けて捨てる。
        stream.Client.Start();
        var wasPaused = _paused;
        try
        {
            while (!_stopRequested)
            {
                stream.WaitForData(20);

                var paused = _paused;
                if (wasPaused && !paused)
                {
                    _onResumed?.Invoke();
                }

                wasPaused = paused;
                Drain(stream, discard: paused);
            }
        }
        finally
        {
            try
            {
                stream.Client.Stop();
            }
            catch
            {
                // 終了処理中の失敗は無視する(どのみち解放する)。
            }
        }
    }

    // 溜まっている音を全部受け取る。discard=trueなら変換もコールバックもせずに捨てる。
    private void Drain(CaptureStream stream, bool discard)
    {
        stream.Capture.GetNextPacketSize(out var packetFrames);
        while (packetFrames > 0 && !_stopRequested)
        {
            var hr = stream.Capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc);
            if (hr < 0)
            {
                throw new COMException("IAudioCaptureClient.GetBuffer failed", hr);
            }

            if (hr == AudclntSBufferEmpty || frames <= 0)
            {
                break;
            }

            float[] mono = null;
            try
            {
                if (!discard)
                {
                    mono = stream.ToMono(data, frames, (flags & CoreAudio.BufferFlagsSilent) != 0);
                }
            }
            finally
            {
                stream.Capture.ReleaseBuffer(frames);
            }

            if (mono != null)
            {
                _onAudio?.Invoke(mono, stream.Left, stream.Right, frames, stream.SampleRate, qpc);
            }
            stream.Capture.GetNextPacketSize(out packetFrames);
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";

    /// <summary>初期化済みのIAudioClient/IAudioCaptureClientと、その音声形式。</summary>
    private sealed class CaptureStream : IDisposable
    {
        private enum SampleKind
        {
            Float32,
            Int16,
            Int24,
            Int32,
        }

        private readonly SampleKind _kind;
        private readonly int _channels;
        private readonly int _blockAlign;
        private readonly AutoResetEvent _dataReady;

        private float[] _mono = new float[4800];
        private float[] _left = new float[4800];
        private float[] _right = new float[4800];
        private float[] _floatBuffer = new float[0];
        private short[] _shortBuffer = new short[0];
        private int[] _intBuffer = new int[0];
        private byte[] _byteBuffer = new byte[0];

        private CaptureStream(IAudioClient client, IntPtr format, bool useEvent)
        {
            Client = client;
            var wf = Marshal.PtrToStructure<WaveFormatEx>(format);
            _channels = wf.Channels;
            _blockAlign = wf.BlockAlign;
            SampleRate = wf.SamplesPerSec;
            BitsPerSample = wf.BitsPerSample;
            _kind = DetectKind(format, wf);

            if (useEvent)
            {
                _dataReady = new AutoResetEvent(false);
                client.SetEventHandle(_dataReady.SafeWaitHandle.DangerousGetHandle());
            }

            var iid = CoreAudio.IidAudioCaptureClient;
            client.GetService(ref iid, out var service);
            Capture = (IAudioCaptureClient)service;
        }

        public IAudioClient Client { get; }
        public IAudioCaptureClient Capture { get; }
        public int SampleRate { get; }
        public int BitsPerSample { get; }

        /// <summary>直前のToMonoの左チャンネル(1つ目のチャンネル)。モノラルの取り込みならnull。</summary>
        public float[] Left => _channels >= 2 ? _left : null;

        /// <summary>直前のToMonoの右チャンネル(2つ目のチャンネル)。モノラルの取り込みならnull。</summary>
        public float[] Right => _channels >= 2 ? _right : null;

        public string Describe() => $"{SampleRate}Hz, {_channels}ch, {BitsPerSample}bit {_kind}, wait={(_dataReady != null ? "event" : "polling")}";

        public void WaitForData(int timeoutMs)
        {
            if (_dataReady != null)
            {
                _dataReady.WaitOne(timeoutMs);
            }
            else
            {
                Thread.Sleep(Math.Min(timeoutMs, 10));
            }
        }

        /// <summary>プロセスループバック。形式はこちらで指定する(GetMixFormatは使えない)。</summary>
        public static CaptureStream OpenProcessLoopback(int processId, Action<string> log)
        {
            var client = ActivateProcessLoopbackClient(processId);

            // まず32bit浮動小数点・48kHzで試し、だめなら16bit整数。マーカーの18～20kHzを表せるよう、
            // サンプリング周波数は44.1kHz以上にする。
            var candidates = new[]
            {
                (tag: CoreAudio.WaveFormatIeeeFloat, bits: (ushort)32, rate: 48000),
                (tag: CoreAudio.WaveFormatPcm, bits: (ushort)16, rate: 48000),
                (tag: CoreAudio.WaveFormatPcm, bits: (ushort)16, rate: 44100),
            };

            Exception last = null;
            foreach (var c in candidates)
            {
                var format = AllocFormat(c.tag, 2, c.rate, c.bits);
                try
                {
                    // プロセスループバックの音を取り込む側の形式に変換してもらう(AUTOCONVERTPCM)。
                    var flags = CoreAudio.StreamFlagsLoopback | CoreAudio.StreamFlagsEventCallback |
                                CoreAudio.StreamFlagsAutoConvertPcm | CoreAudio.StreamFlagsSrcDefaultQuality;
                    client.Initialize(CoreAudio.ShareModeShared, flags, BufferDuration100ns, 0, format, IntPtr.Zero);
                    return new CaptureStream(client, format, useEvent: true);
                }
                catch (Exception ex)
                {
                    last = ex;
                    log($"process loopback format {c.rate}Hz/{c.bits}bit was rejected: 0x{ex.HResult:X8}");

                    // Initializeに一度失敗したIAudioClientは使い回せないので、作り直す。
                    Marshal.ReleaseComObject(client);
                    client = ActivateProcessLoopbackClient(processId);
                }
                finally
                {
                    Marshal.FreeHGlobal(format);
                }
            }

            Marshal.ReleaseComObject(client);
            throw last ?? new InvalidOperationException("no usable format");
        }

        /// <summary>既定の出力デバイスのループバック。形式はデバイスのミックス形式そのまま。</summary>
        public static CaptureStream OpenDeviceLoopback(Action<string> log)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice device = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(CoreAudio.DataFlowRender, CoreAudio.RoleMultimedia, out device);

                foreach (var useEvent in new[] { true, false })
                {
                    var iid = CoreAudio.IidAudioClient;
                    device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var instance);
                    var client = (IAudioClient)instance;
                    client.GetMixFormat(out var format);
                    try
                    {
                        var flags = CoreAudio.StreamFlagsLoopback | (useEvent ? CoreAudio.StreamFlagsEventCallback : 0);
                        client.Initialize(CoreAudio.ShareModeShared, flags, BufferDuration100ns, 0, format, IntPtr.Zero);
                        return new CaptureStream(client, format, useEvent);
                    }
                    catch (Exception ex) when (useEvent)
                    {
                        // 古いWindowsではループバックで通知(イベント)が使えないので、定期的に見に行く方式で作り直す。
                        log($"device loopback with event callback was rejected (0x{ex.HResult:X8}); retrying with polling");
                        Marshal.ReleaseComObject(client);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(format);
                    }
                }

                throw new InvalidOperationException("unreachable");
            }
            finally
            {
                if (device != null)
                {
                    Marshal.ReleaseComObject(device);
                }

                Marshal.ReleaseComObject(enumerator);
            }
        }

        private static IAudioClient ActivateProcessLoopbackClient(int processId)
        {
            var activation = new AudioClientActivationParams
            {
                ActivationType = CoreAudio.ActivationTypeProcessLoopback,
                TargetProcessId = processId,
                ProcessLoopbackMode = CoreAudio.ProcessLoopbackModeIncludeTargetProcessTree,
            };

            var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
            var paramsPtr = Marshal.AllocHGlobal(paramsSize);
            var propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            var completed = false;
            try
            {
                Marshal.StructureToPtr(activation, paramsPtr, false);
                Marshal.StructureToPtr(new PropVariantBlob { VarType = CoreAudio.VtBlob, BlobSize = paramsSize, BlobData = paramsPtr }, propPtr, false);

                var handler = new ActivationCompletionHandler();
                var iid = CoreAudio.IidAudioClient;
                var hr = CoreAudio.ActivateAudioInterfaceAsync(CoreAudio.ProcessLoopbackDevice, ref iid, propPtr, handler, out var operation);
                if (hr < 0)
                {
                    completed = true;
                    throw new COMException("ActivateAudioInterfaceAsync failed", hr);
                }

                if (!handler.Wait(5000))
                {
                    // 完了していない非同期処理がまだ引数を読むかもしれないので、メモリは解放せずに残す。
                    throw new TimeoutException("process loopback activation did not complete within 5s");
                }

                completed = true;
                GC.KeepAlive(operation);
                if (handler.ResultCode < 0 || handler.ActivatedInterface == null)
                {
                    throw new COMException("process loopback activation failed", handler.ResultCode);
                }

                return (IAudioClient)handler.ActivatedInterface;
            }
            finally
            {
                if (completed)
                {
                    Marshal.FreeHGlobal(propPtr);
                    Marshal.FreeHGlobal(paramsPtr);
                }
            }
        }

        private static IntPtr AllocFormat(ushort tag, ushort channels, int rate, ushort bits)
        {
            var blockAlign = (ushort)(channels * bits / 8);
            var wf = new WaveFormatEx
            {
                FormatTag = tag,
                Channels = channels,
                SamplesPerSec = rate,
                AvgBytesPerSec = rate * blockAlign,
                BlockAlign = blockAlign,
                BitsPerSample = bits,
                ExtraSize = 0,
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            Marshal.StructureToPtr(wf, ptr, false);
            return ptr;
        }

        private static SampleKind DetectKind(IntPtr format, WaveFormatEx wf)
        {
            var isFloat = wf.FormatTag == CoreAudio.WaveFormatIeeeFloat;
            if (wf.FormatTag == CoreAudio.WaveFormatExtensible && wf.ExtraSize >= 22)
            {
                // WAVEFORMATEXTENSIBLEのSubFormat(GUID)は先頭から24バイト目。先頭2バイトが形式の番号。
                isFloat = (ushort)Marshal.ReadInt16(format, 24) == CoreAudio.WaveFormatIeeeFloat;
            }

            if (isFloat && wf.BitsPerSample == 32)
            {
                return SampleKind.Float32;
            }

            switch (wf.BitsPerSample)
            {
                case 16: return SampleKind.Int16;
                case 24: return SampleKind.Int24;
                case 32: return SampleKind.Int32;
                default: throw new NotSupportedException($"unsupported capture format: tag=0x{wf.FormatTag:X4}, {wf.BitsPerSample}bit");
            }
        }

        /// <summary>
        /// 取り込んだ1塊をモノラル(全チャンネルの平均)に変換する。あわせて1つ目・2つ目のチャンネルを
        /// <see cref="Left"/>・<see cref="Right"/>に取り出す(ドアの左右を見分けるため)。返す配列は次の呼び出しで上書きされる。
        /// </summary>
        public float[] ToMono(IntPtr data, int frames, bool silent)
        {
            if (_mono.Length < frames)
            {
                _mono = new float[frames * 2];
                _left = new float[frames * 2];
                _right = new float[frames * 2];
            }

            if (silent || data == IntPtr.Zero)
            {
                Array.Clear(_mono, 0, frames);
                Array.Clear(_left, 0, frames);
                Array.Clear(_right, 0, frames);
                return _mono;
            }

            var stereo = _channels >= 2;

            var samples = frames * _channels;
            var scale = 1f / _channels;
            switch (_kind)
            {
                case SampleKind.Float32:
                    Ensure(ref _floatBuffer, samples);
                    Marshal.Copy(data, _floatBuffer, 0, samples);
                    for (var f = 0; f < frames; f++)
                    {
                        var sum = 0f;
                        for (var c = 0; c < _channels; c++)
                        {
                            sum += _floatBuffer[f * _channels + c];
                        }

                        _mono[f] = sum * scale;
                        if (stereo)
                        {
                            _left[f] = _floatBuffer[f * _channels];
                            _right[f] = _floatBuffer[f * _channels + 1];
                        }
                    }

                    break;

                case SampleKind.Int16:
                    Ensure(ref _shortBuffer, samples);
                    Marshal.Copy(data, _shortBuffer, 0, samples);
                    for (var f = 0; f < frames; f++)
                    {
                        var sum = 0f;
                        for (var c = 0; c < _channels; c++)
                        {
                            sum += _shortBuffer[f * _channels + c];
                        }

                        _mono[f] = sum * scale / 32768f;
                        if (stereo)
                        {
                            _left[f] = _shortBuffer[f * _channels] / 32768f;
                            _right[f] = _shortBuffer[f * _channels + 1] / 32768f;
                        }
                    }

                    break;

                case SampleKind.Int32:
                    Ensure(ref _intBuffer, samples);
                    Marshal.Copy(data, _intBuffer, 0, samples);
                    for (var f = 0; f < frames; f++)
                    {
                        var sum = 0.0;
                        for (var c = 0; c < _channels; c++)
                        {
                            sum += _intBuffer[f * _channels + c];
                        }

                        _mono[f] = (float)(sum * scale / 2147483648.0);
                        if (stereo)
                        {
                            _left[f] = (float)(_intBuffer[f * _channels] / 2147483648.0);
                            _right[f] = (float)(_intBuffer[f * _channels + 1] / 2147483648.0);
                        }
                    }

                    break;

                case SampleKind.Int24:
                    var bytes = frames * _blockAlign;
                    Ensure(ref _byteBuffer, bytes);
                    Marshal.Copy(data, _byteBuffer, 0, bytes);
                    for (var f = 0; f < frames; f++)
                    {
                        var sum = 0f;
                        for (var c = 0; c < _channels; c++)
                        {
                            var o = f * _blockAlign + c * 3;
                            var v = (_byteBuffer[o] | (_byteBuffer[o + 1] << 8) | (_byteBuffer[o + 2] << 16)) << 8 >> 8;
                            sum += v;
                            if (stereo && c < 2)
                            {
                                (c == 0 ? _left : _right)[f] = v / 8388608f;
                            }
                        }

                        _mono[f] = sum * scale / 8388608f;
                    }

                    break;
            }

            return _mono;
        }

        private static void Ensure<T>(ref T[] buffer, int length)
        {
            if (buffer.Length < length)
            {
                buffer = new T[length];
            }
        }

        public void Dispose()
        {
            try
            {
                Marshal.ReleaseComObject(Capture);
                Marshal.ReleaseComObject(Client);
            }
            catch
            {
                // 終了処理中の失敗は無視する。
            }

            _dataReady?.Dispose();
        }
    }
}
