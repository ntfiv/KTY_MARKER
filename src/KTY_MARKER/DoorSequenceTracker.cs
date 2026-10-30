namespace KTY_MARKER;

/// <summary>ドアの状態。</summary>
public enum DoorState
{
    /// <summary>閉(BVEのDoorCloseを受け取った後。シナリオ開始時もこれ)。</summary>
    Closed,

    /// <summary>開(駅に着いて開いた後、まだ閉め始めていない)。</summary>
    Open,

    /// <summary>閉まりかけ(戸閉開始のマーカーを検知した後、BVEのDoorCloseがまだ来ていない)。</summary>
    Closing,

    /// <summary>再び開いた(閉まりかけの途中で戸開のマーカーを検知した。再開閉)。</summary>
    Reopened,
}

/// <summary>ドアの一連の動きの中で起きたこと。</summary>
public enum DoorSequenceKind
{
    /// <summary>ドアが開いた(駅に着いて最初に開いたとき。再開閉の「再び開く」はReopen)。</summary>
    Opened,

    /// <summary>戸閉開始。Attemptは何回目の戸閉か(1 = 最初、2以降 = 再開閉の後の閉め直し)。</summary>
    CloseStart,

    /// <summary>再開扉(閉まりかけの途中で開いた)。Attemptは何回目の再開扉か。</summary>
    Reopen,

    /// <summary>戸閉完了(BVEのDoorClose)。Attemptはそれまでの戸閉の回数。</summary>
    Closed,

    /// <summary>
    /// ドアの側が分かった。BVEのDoorOpenで先にOpened(側は不明)を出した後に、戸開のマーカーから側が分かったとき
    /// だけ出す(BVEのDoorOpenはマーカーの検知より早く来ることが多いため)。
    /// </summary>
    OpenSide,
}

/// <summary>ドアの一連の動きの中で起きたこと1つ。</summary>
public readonly struct DoorSequenceEvent
{
    public DoorSequenceEvent(DoorSequenceKind kind, int attempt, bool fromMarker, int simTime, DoorSide side = DoorSide.Unknown)
    {
        Kind = kind;
        Attempt = attempt;
        FromMarker = fromMarker;
        SimTime = simTime;
        Side = side;
    }

    public DoorSequenceKind Kind { get; }

    /// <summary>何回目か(CloseStart・Reopen・Closed。Openedは0)。</summary>
    public int Attempt { get; }

    /// <summary>マーカーの検知から分かったならtrue、BVEのDoorOpen/DoorCloseから分かったならfalse。</summary>
    public bool FromMarker { get; }

    /// <summary>シミュレータ内の時刻(ミリ秒)。</summary>
    public int SimTime { get; }

    /// <summary>
    /// ドアの側。マーカーからならそのマーカーの左右、BVEのDoorCloseからなら今回の停車で最後に分かった側、
    /// BVEのDoorOpenからならUnknown。
    /// </summary>
    public DoorSide Side { get; }

    public string Describe() => (Kind switch
    {
        DoorSequenceKind.Opened => "Opened",
        DoorSequenceKind.CloseStart => FromMarker ? $"CloseStart #{Attempt}" : $"CloseStart #{Attempt} (estimated)",
        DoorSequenceKind.Reopen => $"Reopen #{Attempt}",
        DoorSequenceKind.Closed => $"Closed (after {Attempt} close attempt(s))",
        _ => Kind.ToString(),
    }) + $" side={Side}";
}

/// <summary>
/// ドアの再開閉を1回ずつ見分ける。マーカーの検知(戸閉開始・戸開開始)と、BVEのDoorOpen/DoorCloseを
/// 組み合わせて、ドアの状態を追う。
///
/// BVEのDoorCloseは戸閉が「終わったとき」(全部のドアが閉まったとき)にしか来ない(BVE6で確認済み)。
/// 再開閉ではドアが閉まり切らないので、DoorCloseが来る前に「戸閉開始のマーカー → 戸開のマーカー →
/// 戸閉開始のマーカー…」と続く。これを数えて、次のように知らせる:
///
///   開(Open) ──戸閉マーカー──→ CloseStart #1 ──戸開マーカー──→ Reopen #1 ──戸閉マーカー──→ CloseStart #2 …
///   どの状態からでも、BVEのDoorClose → Closed(戸閉完了)
///
/// 戸閉・再開扉は<see cref="MaxAttempts"/>回目(既定3回目)まで知らせ、それを超えた分は数えるだけで
/// 知らせない(<see cref="LastNote"/>に理由を残す)。
///
/// 同じ状態のまま同じマーカーがもう一度来た場合(左右両側のドアの音が少しずれて鳴った等)は、二重に
/// 数えない。BVEのスレッドだけから使う(スレッドセーフではない)。
///
/// ドアの側(<see cref="DoorSide"/>): マーカーの左右をそのまま各イベントに付け、今回の停車で最後に分かった側を
/// <see cref="Side"/>に残す(BVEのDoorCloseからのイベントにはこれを付ける)。BVEのDoorOpenが戸開のマーカーより
/// 先に来た場合は、マーカーで側が分かった時点で<see cref="DoorSequenceKind.OpenSide"/>を出す。
/// </summary>
public sealed class DoorSequenceTracker
{
    public DoorSequenceTracker(int maxAttempts, bool estimateCloseStart = true)
    {
        MaxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        EstimateCloseStart = estimateCloseStart;
    }

    public int MaxAttempts { get; }

    /// <summary>
    /// 戸閉マーカーを取りこぼしたまま(開・再び開いた状態で)BVEのDoorCloseが来たら、戸閉開始をDoorCloseの時点で
    /// 補う(<see cref="DoorSequenceEvent.FromMarker"/> = false)。BVEのウィンドウが前面に無くBVEが音を出していない
    /// 間など、マーカーを検知できなかったときでも、戸閉開始の処理が(遅れてでも)抜けないようにするため。
    /// </summary>
    public bool EstimateCloseStart { get; }

    public DoorState State { get; private set; } = DoorState.Closed;

    /// <summary>今回(ドアが開いてから)の戸閉開始の回数。次にドアが開くまで残す。</summary>
    public int CloseCount { get; private set; }

    /// <summary>今回(ドアが開いてから)の再開扉の回数。次にドアが開くまで残す。</summary>
    public int ReopenCount { get; private set; }

    /// <summary>今回の停車で最後に分かったドアの側(分からなければUnknown)。次にドアが開くまで残す。</summary>
    public DoorSide Side { get; private set; }

    /// <summary>直前の呼び出しの補足(ログ用)。イベントを起こさなかった理由や、気になる点。無ければnull。</summary>
    public string LastNote { get; private set; }

    /// <summary>シナリオの読み込み時などに、最初の状態(閉)に戻す。</summary>
    public void Reset()
    {
        State = DoorState.Closed;
        CloseCount = 0;
        ReopenCount = 0;
        Side = DoorSide.Unknown;
        LastNote = null;
    }

    /// <summary>戸開のマーカーを検知した。side = そのマーカーの左右(分からなければUnknown)。</summary>
    public DoorSequenceEvent? OnOpenMarker(int simTime, DoorSide side = DoorSide.Unknown)
    {
        LastNote = null;
        switch (State)
        {
            case DoorState.Closed:
                StartNewStop();
                State = DoorState.Open;
                RememberSide(side);
                return new DoorSequenceEvent(DoorSequenceKind.Opened, 0, true, simTime, side);

            case DoorState.Closing:
                ReopenCount++;
                State = DoorState.Reopened;
                RememberSide(side);
                if (ReopenCount > MaxAttempts)
                {
                    LastNote = $"reopen #{ReopenCount} is over the limit ({MaxAttempts}) - not reported";
                    return null;
                }

                return new DoorSequenceEvent(DoorSequenceKind.Reopen, ReopenCount, true, simTime, side);

            default:
                // 開・再び開いた状態で、もう一度戸開のマーカー(BVEのDoorOpenが先に来ていた、左右のドアの音が
                // ずれた等)。二重に数えない。ただし、BVEのDoorOpenで開いて側がまだ分かっていなければ、側だけ知らせる。
                if (State == DoorState.Open && Side == DoorSide.Unknown && side != DoorSide.Unknown)
                {
                    Side = side;
                    LastNote = "door-open marker after BVE DoorOpen - door side is now known";
                    return new DoorSequenceEvent(DoorSequenceKind.OpenSide, 0, true, simTime, side);
                }

                LastNote = $"door-open marker while {State} - already counted";
                return null;
        }
    }

    /// <summary>戸閉開始のマーカーを検知した。side = そのマーカーの左右(分からなければUnknown)。</summary>
    public DoorSequenceEvent? OnCloseMarker(int simTime, DoorSide side = DoorSide.Unknown)
    {
        LastNote = null;
        switch (State)
        {
            case DoorState.Closing:
                LastNote = "door-close marker while Closing - already counted";
                return null;

            case DoorState.Closed:
                // ドアが開いたことを見ていない(駅へジャンプした直後など、BVEのDoorOpenもマーカーも
                // 取りこぼした)。ここから新しく数え始める。
                StartNewStop();
                LastNote = "door-close marker while Closed (door opening was not seen) - started a new count";
                break;
        }

        CloseCount++;
        State = DoorState.Closing;
        RememberSide(side);
        if (CloseCount > MaxAttempts)
        {
            LastNote = $"close attempt #{CloseCount} is over the limit ({MaxAttempts}) - not reported";
            return null;
        }

        return new DoorSequenceEvent(DoorSequenceKind.CloseStart, CloseCount, true, simTime, side);
    }

    /// <summary>BVEのDoorOpenを受け取った。</summary>
    public DoorSequenceEvent? OnBveDoorOpen(int simTime)
    {
        LastNote = null;
        switch (State)
        {
            case DoorState.Closed:
                StartNewStop();
                State = DoorState.Open;
                return new DoorSequenceEvent(DoorSequenceKind.Opened, 0, false, simTime);

            case DoorState.Closing:
            case DoorState.Reopened:
                // BVEは再開閉の途中ではDoorOpenを送らない(BVE6で確認済み、2026-10-28 八幡宿: 戸閉→再開扉→戸閉
                // →再開扉→戸閉の間、DoorOpenは来なかった)。閉まりかけの途中にDoorOpenが来るのは、駅へジャンプした
                // とき(ジャンプ先でドアが開いた状態になる)。前の駅の回数を持ち越さないよう、新しい停車として数え直す。
                LastNote = $"BVE DoorOpen while {State} (station jump?) - started a new count";
                StartNewStop();
                State = DoorState.Open;
                return new DoorSequenceEvent(DoorSequenceKind.Opened, 0, false, simTime);

            default:
                // 戸開のマーカーを先に検知していた、または駅へのジャンプで開いたまま次の駅へ移った(まだ数は0)。
                LastNote = "BVE DoorOpen while Open - already open";
                return null;
        }
    }

    /// <summary>BVEのDoorCloseを受け取った(全部のドアが閉まった)。</summary>
    /// <param name="estimatedCloseStart">
    /// 戸閉マーカーを取りこぼしていた場合、補った戸閉開始(<see cref="EstimateCloseStart"/>がtrueのとき)。
    /// 戻り値の戸閉完了より先に処理すること。
    /// </param>
    public DoorSequenceEvent? OnBveDoorClose(int simTime, out DoorSequenceEvent? estimatedCloseStart)
    {
        LastNote = null;
        estimatedCloseStart = null;
        var before = State;
        State = DoorState.Closed;
        if (before == DoorState.Closed)
        {
            LastNote = "BVE DoorClose while already Closed";
            return null;
        }

        // 開・再び開いた状態のまま閉まった = 最後の戸閉マーカーを取りこぼした。
        if (before is DoorState.Open or DoorState.Reopened)
        {
            if (EstimateCloseStart)
            {
                CloseCount++;
                if (CloseCount <= MaxAttempts)
                {
                    estimatedCloseStart = new DoorSequenceEvent(DoorSequenceKind.CloseStart, CloseCount, false, simTime, Side);
                }

                LastNote = $"door-close marker was not detected - close start #{CloseCount} estimated from BVE DoorClose (late)";
            }
            else
            {
                LastNote = "closed without the last door-close marker";
            }
        }

        return new DoorSequenceEvent(DoorSequenceKind.Closed, CloseCount, false, simTime, Side);
    }

    private void StartNewStop()
    {
        CloseCount = 0;
        ReopenCount = 0;
        Side = DoorSide.Unknown;
    }

    // 分かった側だけ覚える(Unknownでは上書きしない)。
    private void RememberSide(DoorSide side)
    {
        if (side != DoorSide.Unknown)
        {
            Side = side;
        }
    }
}
