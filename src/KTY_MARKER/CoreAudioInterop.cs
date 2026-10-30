using System;
using System.Runtime.InteropServices;

namespace KTY_MARKER;

// WASAPI(Windows Core Audio)のCOM定義。NAudioに頼らず、取り込みに必要な分だけを宣言している
// (依存DLLを無くし、プロセスループバックというNAudioに無い機能も使うため)。
// メソッドの並び順はCOMの関数テーブル(vtable)の順番そのものなので、途中を省略したり並べ替えたり
// してはいけない(末尾の使わないメソッドは省略してよい)。
//
// COMから呼び戻される型(完了ハンドラ)とそのインターフェースはpublicにしておく必要がある
// (.NETはpublicでない型をCOMに見せないため、QueryInterfaceが失敗する)。

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient
{
    void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    void GetBufferSize(out int bufferFrames);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out int paddingFrames);
    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    void GetMixFormat(out IntPtr format);
    void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr eventHandle);
    void GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioCaptureClient
{
    // データが無いときはAUDCLNT_S_BUFFER_EMPTY(成功コード)を返すので、戻り値で受け取る。
    [PreserveSig]
    int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
    void ReleaseBuffer(int frames);
    void GetNextPacketSize(out int frames);
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
public class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

/// <summary>
/// 「どのスレッドから呼んでもよい」ことを示す目印のインターフェース。ActivateAudioInterfaceAsyncの
/// 完了ハンドラはこれを実装していないと受け付けてもらえない。
/// </summary>
[ComImport]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject
{
}

internal static class CoreAudio
{
    public const int ShareModeShared = 0;

    public const int StreamFlagsLoopback = 0x00020000;
    public const int StreamFlagsEventCallback = 0x00040000;
    public const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
    public const int StreamFlagsSrcDefaultQuality = 0x08000000;

    public const int BufferFlagsDataDiscontinuity = 0x1;
    public const int BufferFlagsSilent = 0x2;

    public const int DataFlowRender = 0;
    public const int RoleMultimedia = 1;
    public const int ClsCtxAll = 0x17;

    public const ushort WaveFormatPcm = 1;
    public const ushort WaveFormatIeeeFloat = 3;
    public const ushort WaveFormatExtensible = 0xFFFE;

    public static readonly Guid IidAudioClient = typeof(IAudioClient).GUID;
    public static readonly Guid IidAudioCaptureClient = typeof(IAudioCaptureClient).GUID;

    /// <summary>プロセスループバック用の仮想デバイス名(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK)。</summary>
    public const string ProcessLoopbackDevice = "VAD\\Process_Loopback";

    public const int ActivationTypeProcessLoopback = 1;
    public const int ProcessLoopbackModeIncludeTargetProcessTree = 0;

    // PROPVARIANTのVT_BLOB。
    public const ushort VtBlob = 65;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

/// <summary>AUDIOCLIENT_ACTIVATION_PARAMS(プロセスループバックの場合)。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    public int TargetProcessId;
    public int ProcessLoopbackMode;
}

/// <summary>
/// PROPVARIANTのうちVT_BLOBの形だけ。ポインタの位置は32bit/64bitで変わるが、Sequentialの並びで
/// 自然に正しい位置(x86: 12バイト目、x64: 16バイト目)になる。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public ushort VarType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public int BlobSize;
    public IntPtr BlobData;
}

/// <summary>WAVEFORMATEX(拡張部分なし)。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public int SamplesPerSec;
    public int AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

/// <summary>ActivateAudioInterfaceAsyncの完了を受け取るハンドラ。結果はこの中で取り出しておく。</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    private readonly System.Threading.ManualResetEvent _completed = new(false);

    internal int ResultCode { get; private set; } = unchecked((int)0x80004005);
    internal object ActivatedInterface { get; private set; }

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            operation.GetActivateResult(out var hr, out var instance);
            ResultCode = hr;
            ActivatedInterface = instance;
        }
        catch (Exception ex)
        {
            ResultCode = ex.HResult;
        }
        finally
        {
            _completed.Set();
        }
    }

    internal bool Wait(int timeoutMs) => _completed.WaitOne(timeoutMs);
}
