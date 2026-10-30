namespace KTY_MARKER;

/// <summary>駅で起きたこと(プラグイン側で処理を起こすきっかけ)の種類。</summary>
public enum StationEventKind
{
    /// <summary>到着時放送が鳴り始めた(Role = Arrival のマーカー)。</summary>
    ArrivalAnnounce,

    /// <summary>発車時放送が鳴り始めた(Role = Departure のマーカー)。</summary>
    DepartureAnnounce,

    /// <summary>駅に着いてドアが開いた(戸開のマーカー、またはBVEのDoorOpen)。</summary>
    DoorOpened,

    /// <summary>戸閉開始(戸閉のマーカー)。Attempt = 何回目か(2以降は再開閉の後の閉め直し)。</summary>
    DoorCloseStart,

    /// <summary>再開扉(閉まりかけの途中で開いた。戸開のマーカー)。Attempt = 何回目か。</summary>
    DoorReopen,

    /// <summary>戸閉完了(BVEのDoorClose)。Attempt = それまでの戸閉の回数。</summary>
    DoorClosed,

    /// <summary>
    /// ドアの側が分かった。BVEのDoorOpenで先にDoorOpened(Side = Unknown)を出した後、戸開のマーカーから側が
    /// 分かったときだけ出る(BVEのDoorOpenはマーカーの検知より早く来ることが多い)。Side = 開いた側。
    /// </summary>
    DoorOpenSide,

    /// <summary>役割の無い(Role = None)マーカーを検知した。MarkerNameで区別する。</summary>
    OtherMarker,

    /// <summary>
    /// 停車中にBVEの音が完全な無音になった(BVEのウィンドウからフォーカスが外れた等)。この間に鳴るはずの放送・ドアの
    /// 音は検知できない。
    /// </summary>
    AudioSilent,

    /// <summary>BVEの音が戻った。Attempt = 無音だった時間(ミリ秒、実時間)。</summary>
    AudioResumed,
}

/// <summary>駅で起きたこと1つ。<see cref="AtsLogic.OnStationEvent"/>に渡される。</summary>
public readonly struct StationEvent
{
    public StationEvent(StationEventKind kind, int attempt, bool fromMarker, int simTime, int sinceStopMs, string markerName,
                        DoorSide side = DoorSide.Unknown)
    {
        Kind = kind;
        Attempt = attempt;
        FromMarker = fromMarker;
        SimTime = simTime;
        SinceStopMs = sinceStopMs;
        MarkerName = markerName ?? "";
        Side = side;
    }

    public StationEventKind Kind { get; }

    /// <summary>何回目か(DoorCloseStart・DoorReopen・DoorClosed)。AudioResumedは無音だった時間(ミリ秒)。それ以外は0。</summary>
    public int Attempt { get; }

    /// <summary>
    /// マーカーの検知から分かったならtrue、BVEのDoorOpen/DoorCloseや音の状態から分かったならfalse。
    /// DoorCloseStartでfalseなのは、戸閉マーカーを取りこぼしたためBVEのDoorCloseの時点で補ったもの(遅れている)。
    /// </summary>
    public bool FromMarker { get; }

    /// <summary>シミュレータ内の時刻(ミリ秒)。</summary>
    public int SimTime { get; }

    /// <summary>停車してから(速度が0付近になってから)の時間(ミリ秒、シミュレータ内の時刻)。</summary>
    public int SinceStopMs { get; }

    /// <summary>元になったマーカーの名前(iniの[EventN] Name)。BVEのイベントからなら空。</summary>
    public string MarkerName { get; }

    /// <summary>
    /// ドアの側(ドアのイベントだけ。[DoorSide])。戸閉・戸開のマーカーからならその左右、DoorClosed・補った
    /// DoorCloseStartなら今回の停車で最後に分かった側、BVEのDoorOpenからのDoorOpenedはUnknown(後から
    /// DoorOpenSideで分かる)。左右が分からない環境・設定ではUnknown。
    /// </summary>
    public DoorSide Side { get; }

    /// <summary>ドアのイベントか(Sideに意味があるか)。</summary>
    public bool IsDoorEvent => Kind is StationEventKind.DoorOpened or StationEventKind.DoorCloseStart or StationEventKind.DoorReopen
        or StationEventKind.DoorClosed or StationEventKind.DoorOpenSide;

    public string Describe() => DescribeKind() + (IsDoorEvent ? $" side={Side}" : "");

    private string DescribeKind() => Kind switch
    {
        StationEventKind.DoorCloseStart => FromMarker ? $"DoorCloseStart #{Attempt}" : $"DoorCloseStart #{Attempt} (estimated, late)",
        StationEventKind.AudioResumed => $"AudioResumed (silent for {Attempt / 1000.0:0.0}s)",
        StationEventKind.DoorReopen => $"DoorReopen #{Attempt}",
        StationEventKind.DoorClosed => $"DoorClosed (after {Attempt} close attempt(s))",
        StationEventKind.OtherMarker => $"OtherMarker {MarkerName}",
        _ => Kind.ToString(),
    };
}
