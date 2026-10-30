using System;
using System.Runtime.InteropServices;

namespace KTY_MARKER;

/// <summary>
/// BVE5のATSプラグインABI(atsplugin.h)に対応するネイティブエントリポイント。素の
/// __stdcallエクスポートで、GetProcAddressによりこれらの名前(装飾なし)で検索される
/// (DllExportのビルドステップ - KTY_MARKER.csprojの`DllExportNamespace`を参照 - が各メソッドを
/// そのままの名前でエクスポートする)。各エクスポートはすべての例外をキャッチする - 例外が
/// BVE5のネイティブ呼び出しスタックに漏れると、プラグインだけでなくシミュレータ本体が
/// クラッシュする。実際の挙動はすべてAtsLogicにあり、このクラスは相互運用の配線役に過ぎない。
///
/// KTY_HOSO・KTY_TUNNELと違い、NAudioなどの依存DLLを使わないので、依存DLLを探す
/// AssemblyResolveハンドラは不要。
/// </summary>
public static class AtsExports
{
    private static readonly bool UnhandledExceptionLoggerRegistered = RegisterUnhandledExceptionLogger();
    private static readonly AtsLogic Logic = new();

    // 音声の取り込みは専用スレッドで動く。上のtry/catchはBVEから直接呼ばれるエクスポート関数しか
    // カバーしないので、それ以外のスレッドで起きた未処理例外もログに残す。
    private static bool RegisterUnhandledExceptionLogger()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            AtsDebug.Log($"[UNHANDLED] {e.ExceptionObject}");
        return true;
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void Load()
    {
        try
        {
            Logic.OnLoad();
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(Load), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void Dispose()
    {
        try
        {
            Logic.OnDispose();
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(Dispose), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static int GetPluginVersion() => AtsConstants.PluginVersion;

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void SetVehicleSpec(AtsVehicleSpec spec)
    {
        try
        {
            Logic.OnVehicleSpec(spec);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(SetVehicleSpec), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void Initialize(int initialHandlePosition)
    {
        try
        {
            Logic.OnInitialize(initialHandlePosition);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(Initialize), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static AtsHandles Elapse(AtsVehicleState state, IntPtr panel, IntPtr sound)
    {
        try
        {
            return Logic.OnElapse(
                state,
                new NativeIntArray(panel, AtsLogic.PanelIndexMin, AtsLogic.PanelIndexMax),
                new NativeIntArray(sound, AtsLogic.SoundIndexMin, AtsLogic.SoundIndexMax));
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(Elapse), ex);

            // ゼロ(default)を返すと、ブレーキ緩解・レバーサ中立として運転士の操作や他プラグインの
            // 判断を上書きしてしまう(ATS Plugin Multiplexer経由では素通しされる)。最後に把握して
            // いたハンドル位置をそのまま返す。
            return Logic.LastHandles;
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void SetPower(int notch)
    {
        try
        {
            Logic.OnPowerChanged(notch);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(SetPower), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void SetBrake(int notch)
    {
        try
        {
            Logic.OnBrakeChanged(notch);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(SetBrake), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void SetReverser(int position)
    {
        try
        {
            Logic.OnReverserChanged(position);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(SetReverser), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void KeyDown(int keyIndex)
    {
        try
        {
            Logic.OnKeyDown(keyIndex);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(KeyDown), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void KeyUp(int keyIndex)
    {
        try
        {
            Logic.OnKeyUp(keyIndex);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(KeyUp), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void HornBlow(int hornIndex)
    {
        try
        {
            Logic.OnHornBlow(hornIndex);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(HornBlow), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void DoorOpen()
    {
        try
        {
            Logic.OnDoorOpen();
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(DoorOpen), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void DoorClose()
    {
        try
        {
            Logic.OnDoorClose();
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(DoorClose), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void SetSignal(int signalIndex)
    {
        try
        {
            Logic.OnSignalChanged(signalIndex);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(SetSignal), ex);
        }
    }

    [DllExport(CallingConvention = CallingConvention.StdCall)]
    public static void SetBeaconData(AtsBeaconData beacon)
    {
        try
        {
            Logic.OnBeaconData(beacon);
        }
        catch (Exception ex)
        {
            AtsDebug.LogException(nameof(SetBeaconData), ex);
        }
    }
}
