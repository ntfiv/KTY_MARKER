using System;
using System.Runtime.InteropServices;

namespace KTY_MARKER;

/// <summary>
/// BVEの「ATSプラグイン」ネイティブインターフェース(atsplugin.h)で定義されている定数。
/// </summary>
public static class AtsConstants
{
    public const int PluginVersion = 0x00020000;

    // ATSキーのindex(KeyDown/KeyUpに渡される)。
    public const int KeyS = 0;
    public const int KeyA1 = 1;
    public const int KeyA2 = 2;
    public const int KeyB1 = 3;
    public const int KeyB2 = 4;
    public const int KeyC1 = 5;
    public const int KeyC2 = 6;
    public const int KeyD = 7;
    public const int KeyE = 8;
    public const int KeyF = 9;
    public const int KeyG = 10;
    public const int KeyH = 11;
    public const int KeyI = 12;
    public const int KeyJ = 13;
    public const int KeyK = 14;
    public const int KeyL = 15;

    // 初期ハンドル位置(Initializeに渡される)。
    public const int InitServiceBrake = 0;
    public const int InitEmergencyBrake = 1;
    public const int InitHandleRemoved = 2;

    // サウンド制御命令(Elapseからsound[]配列に書き込む)。
    public const int SoundStop = -10000;
    public const int SoundPlayOnce = 1;
    public const int SoundPlayLooping = 0;
    public const int SoundContinue = 2;

    // 警笛のindex(HornBlowに渡される)。
    public const int HornPrimary = 0;
    public const int HornSecondary = 1;
    public const int HornMusic = 2;

    // 定速制御命令(Elapseから返すAtsHandlesの一部)。
    public const int ConstantSpeedContinue = 0;
    public const int ConstantSpeedEnable = 1;
    public const int ConstantSpeedDisable = 2;
}

/// <summary>車両仕様。SetVehicleSpec経由で一度だけ渡される。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AtsVehicleSpec
{
    public int BrakeNotches;
    public int PowerNotches;
    public int AtsNotch;
    public int B67Notch;
    public int Cars;
}

/// <summary>車両の現在状態。Elapseに毎フレーム渡される。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AtsVehicleState
{
    public double Location;
    public float Speed;
    public int Time;
    public float BcPressure;
    public float MrPressure;
    public float ErPressure;
    public float BpPressure;
    public float SapPressure;
    public float Current;
}

/// <summary>地上子(ビーコン)データ。地上子を通過した際にSetBeaconData経由で渡される。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AtsBeaconData
{
    public int Type;
    public int Signal;
    public float Distance;
    public int Optional;
}

/// <summary>運転指示。Elapseから毎フレーム返される。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AtsHandles
{
    public int Brake;
    public int Power;
    public int Reverser;
    public int ConstantSpeed;
}

/// <summary>
/// BVE5がElapseにpanel/sound状態として渡すネイティブint[]バッファへの薄いインデクサ。
/// ホスト側がバッファを所有し、プラグインにサイズを一切教えないため、読み取りは
/// 範囲チェックなし - panel.cfg/sound.cfgで実際に宣言したindexだけを読むこと。書き込みは
/// [minIndex, maxIndex]にクランプされる: ATS Plugin Multiplexer経由で複数プラグインが
/// このバッファを共有する場合、このプラグインの宣言範囲外への書き込みは他プラグインの
/// panel/sound状態を silently 壊してしまうため、代わりにログを出して無視する。
/// </summary>
public readonly struct NativeIntArray(IntPtr pointer, int minIndex = 0, int maxIndex = int.MaxValue)
{
    public int this[int index]
    {
        get => Marshal.ReadInt32(pointer, index * sizeof(int));
        set
        {
            if (index < minIndex || index > maxIndex)
            {
                AtsDebug.Log($"[WARN] panel/soundのindex {index} への書き込みを拒否(このプラグインの宣言範囲[{minIndex}, {maxIndex}]外)。");
                return;
            }

            Marshal.WriteInt32(pointer, index * sizeof(int), value);
        }
    }
}
