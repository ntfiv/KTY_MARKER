# KTY_MARKER 開発メモ

仕組み・BVE の動き・決めたことなど、プラグインの中身を知りたい人・手を加えたい人向けのメモです。
使い方は [README](../README.md) を参照してください。

## 目次

- [仕組み](#仕組み)
- [きっかけの受け取り方(`OnStationEvent`)](#きっかけの受け取り方onstationevent)
- [ドアの再開閉](#ドアの再開閉doorsequence)
- [ドアの左右](#ドアの左右doorside)
- [鏡像のチェック](#鏡像のチェックmirrorcheck)
- [BVE が音を出していないとき](#bve-が音を出していないときsilence)
- [読み込み時の自己診断](#読み込み時の自己診断selftest)
- [確認音・ログ](#確認音ログ)
- [分かった BVE の動き](#分かった-bve-の動き)
- [決めたこと](#決めたこと)
- [制限・まだ確かめていないこと](#制限まだ確かめていないこと)
- [確認状況](#確認状況)
- [過去に直した主な問題](#過去に直した主な問題)
- [処理の重さ](#処理の重さ)
- [設定項目](#設定項目)
- [MarkerTool](#markertool)
- [ビルドの注意](#ビルドの注意)
- [配布パッケージの作り方](#配布パッケージの作り方)
- [フォルダ構成](#フォルダ構成)

## 仕組み

1. 放送やドアの音の WAV の先頭に、聞こえにくい高い周波数の短い音(**マーカー**、120ms・-30dBFS)を混ぜておく。
2. BVE が鳴らしている音を、WASAPI のループバックで取り込む(`LoopbackCapture.cs`)。
   - 既定では **BVE のプロセスの音だけ**を取り込む(プロセスループバック。Windows 10 Build 20348 以降、実質 Windows 11)。
   - 使えない Windows では、出力デバイスの音全体の取り込みに自動で切り替える(`FallbackToDevice`)。
3. 決まった周波数だけを Goertzel 法で調べ、マーカーが鳴った瞬間を検知する(`ToneDetector.cs`)。鳴り始めから検知まで約 50ms。
   複数のマーカーが同時に鳴っても(到着放送と戸開の音など)、それぞれ検知する。
4. **停車中(0.1km/h 未満)だけ検知する。** 放送もドアの音も停車中にしか鳴らないので取りこぼしは無く、走行音・ブレーキの鳴きを
   取り違えずに済む。走行中も取り込みは止めず、届いた音を捨てる(プロセスループバックは一度止めると再開できないため)。
5. 検知したマーカーと BVE の `DoorOpen` / `DoorClose` を組み合わせて、駅で起きたことを `AtsLogic.OnStationEvent` に知らせる。

既定のマーカー(周波数は互いに 500Hz 以上離す):

| マーカー | 周波数 | 入れる音 |
|---|---|---|
| 1 `Departure` | 18.5kHz | 発車放送(駅リストの `departureSoundKey`) |
| 2 `DoorCloseStart` | 19kHz | 戸閉の音(`[Door] CloseLeft` は左チャンネルだけ、`CloseRight` は右チャンネルだけ) |
| 3 `DoorOpenStart` | 19.5kHz | 戸開の音(`OpenLeft` / `OpenRight`。同上) |
| 4 `Arrival` | 18kHz | 到着放送(駅リストの `arrivalSoundKey`) |

検知の条件(解析 10ms ごと):マーカーの周波数が `ThresholdDb`(-70dBFS)以上、比較用の周波数(マーカーの外側 1kHz)より `MarginDb`(15dB)
以上大きい、一番大きいマーカーから `RelativeDb`(40dB)以内。これが `MinHits`(4)回続いたら検知。

## きっかけの受け取り方(`OnStationEvent`)

駅で起きたことは、すべて `AtsLogic.OnStationEvent(StationEvent ev)` に集まる(`StationEventRaised` イベントも起こす)。
BVE のスレッドから呼ばれる。プラグインに処理を足すときは、ここにつなぐ。

| `ev.Kind` | きっかけ | 元になるもの |
|---|---|---|
| `ArrivalAnnounce` | 到着放送が鳴り始めた | 到着マーカー |
| `DepartureAnnounce` | 発車放送が鳴り始めた | 発車マーカー |
| `DoorOpened` | 駅に着いてドアが開いた | 戸開マーカーか BVE の `DoorOpen` の早い方 |
| `DoorCloseStart` | 戸閉が始まった(`Attempt` = 1, 2, 3) | 戸閉マーカー(取りこぼしたときは `DoorClose` の時点で補う) |
| `DoorReopen` | 再開扉(`Attempt` = 1, 2, 3) | 閉まりかけの途中に来た戸開マーカー |
| `DoorClosed` | 戸閉が完了した(`Attempt` = それまでの戸閉の回数) | BVE の `DoorClose` |
| `DoorOpenSide` | ドアの側が分かった(`DoorOpened` を側不明で出した後だけ) | `DoorOpen` より後に来た戸開マーカー |
| `AudioSilent` / `AudioResumed` | BVE の音が止まった / 戻った(`Attempt` = 無音だった時間 ms) | 取り込んだ音のレベル |
| `OtherMarker` | 役割の無いマーカー(`ev.MarkerName` で区別) | `Role = None` のマーカー |

- `ev.FromMarker`:マーカーから分かったなら true。`DoorCloseStart` で false なのは、`DoorClose` の時点で補ったもの(遅れている)。
- `ev.SinceStopMs`:停車してからの時間。`ev.Side`:ドアの側(`Left` / `Right` / `Both` / `Unknown`)。
- 到着放送が鳴ったかどうかはマーカーでしか分からない(鳴るのは駅リストに `arrivalSoundKey` がある駅だけ)。

## ドアの再開閉(`[DoorSequence]`)

BVE の `DoorClose` は全部のドアが閉まったときにしか来ず、再開閉の途中では `DoorOpen` も来ない。そこで `DoorClose` までの
「戸閉マーカー → 戸開マーカー → 戸閉マーカー…」を `DoorSequenceTracker.cs` で数え、1回ずつ知らせる。

```
開 ─戸閉→ DoorCloseStart #1 ─戸開→ DoorReopen #1 ─戸閉→ DoorCloseStart #2 ─…→ DoorCloseStart #3
どの状態からでも、BVE の DoorClose → DoorClosed(戸閉の回数つき)
```

- マーカーの役割は `[EventN] Role`(`Arrival` / `Departure` / `DoorClose` / `DoorOpen` / `None`)。省略時は既定の名前から決める。
- `MaxAttempts`(3)回目まで知らせる。超えた分は数えるだけ。
- 同じ状態で同じマーカーがもう一度来た(左右のドアの音のずれなど)ときや、`DoorOpen` と戸開マーカーの両方が来たときは二重に数えない。
- 閉まりかけの途中に `DoorOpen` が来たら、駅ジャンプとみなして数え直す(再開閉の途中では `DoorOpen` は来ないため)。
- Panel(指定したときだけ):`StatePanel`(0=閉、1=開、2=閉まりかけ、3=再び開いた)、`CloseCountPanel`・`ReopenCountPanel`。

## ドアの左右(`[DoorSide]`)

ATSプラグインの API では、`DoorOpen` / `DoorClose` がどちら側のドアかは分からない。そこで、左側のドアの音は左チャンネルだけ、
右側は右チャンネルだけにマーカーを入れ、検知したときの左右の強さの差で側を決める(周波数を増やさずに済む)。

| 左右の差 | 判定 |
|---|---|
| 片方が `SideDb`(10dB)以上大きい | `Left` / `Right` |
| `BothDb`(6dB)以内 | `Both` |
| その間 | `Unknown` |

- 検知そのものは左右を混ぜた音で行い、左右の計算はマーカーが鳴っている間だけ行う。取り込みがモノラルなら `Unknown`。
- `DoorOpen` はふつう戸開マーカーより早く来るので、`DoorOpened` は側不明で先に出て、マーカーで分かった時点で `DoorOpenSide` が出る。
- `DoorClosed` と補った `DoorCloseStart` は、その停車で最後に分かった側。Panel(指定したときだけ):0=不明、1=左、2=右、3=両側。
- 片側だけに入れたマーカーは、左右を混ぜると 6dB 小さくなる(しきい値までの余裕もその分減る)。
- **BVE は1つの駅で片側のドアしか開けない**(`Station[...].Put(door, …)` の `door` が負なら左、それ以外は右)。`Both` になるのは、
  両方のチャンネルにマーカーを入れた WAV のときだけ。

## 鏡像のチェック(`MirrorCheck`)

22.05kHz・11.025kHz の WAV を BVE が 48kHz にして鳴らすと、元の音(声の 2.5〜4kHz など)が「22050Hz − 周波数」の位置(18〜19.5kHz)に
鏡像として現れる(Windows の変換では出ず、BVE が鳴らすときだけ出る)。停車中にこうした音が大きく鳴っていると、マーカーと取り違えることがある。
実際に、早送りで車内放送が停車後まで鳴り続けたときに誤検知が出た。

- マーカーの周波数 f が検知の条件を満たしたときだけ、鏡像の元になる周波数(22050 − f と、取り込みの周波数 − 22050 − f。48kHz なら 25950 − f)も調べる。
  どちらかが f より `MirrorDb`(10dB)以上大きければ、その回は鳴っていないとする。
- 鏡像は元の音より 20dB 以上小さく、本物のマーカーでは元の位置の音はずっと小さいので、音量の比で見分けられる(BVE の音量に左右されない)。
- 弱点:大きな低いサンプリング周波数の音と同時に鳴った、とても小さな本物のマーカーは除かれることがある。16k・24k・32kHz の WAV の鏡像は対象外。
- ほかの対策(`MinHits` を増やす・しきい値を上げる・2周波数のマーカー)は、効かないか WAV の作り直しが要るので採らなかった。

## BVE が音を出していないとき(`[Silence]`)

BVE はウィンドウからフォーカスが外れると音を出さなくなる(シミュレーションは進む)。その間のマーカーは検知できない。

- 停車中に完全な無音(サンプルがすべて 0)が `MinMs`(300ms)続いたら `AudioSilent`、音が戻ったら `AudioResumed` を出す。
  ふつうの停車中でも背景の音で -80〜-100dBFS はあるので、完全な 0 は BVE が音を出していないとき。
- 戸閉マーカーを取りこぼしたまま `DoorClose` が来たら、`DoorCloseStart`(推定・遅れ)を補ってから `DoorClosed` を出す(`EstimateCloseStart`)。
- 放送には代わりになる BVE のイベントが無いので補えない。

## 読み込み時の自己診断(`[SelfTest]`)

シナリオの読み込み時に、マーカーと同じ周波数の試験音(各 120ms、-40dBFS)をプラグイン自身(BVE のプロセス)から鳴らし、
いつもの取り込み・検知で拾えるかを確かめる(`SelfTest.cs`)。本物のマーカーと同じ経路なので、結果がそのまま「この環境で検知できるか」になる。

- 失敗したら警告のウィンドウを出す(別スレッド、同じ BVE の起動中は1回だけ)。結果は `ResultPanel`(0=未実施、1=OK、2=NG、3=判定できず)にも出る。
- 戸閉・戸開の試験音は左だけ・右だけで鳴らし、左右を見分けられるかも確かめる(「モノラル オーディオ」・立体音響などで混ざる環境を見つける)。
- 試験音は本物より 10dB 小さいので、合格すれば少なくとも 10dB の余裕がある。読み込みから約 2.3 秒で終わる。
- 試験中に検知したものはきっかけに流さない。試験の途中で走り出したら「判定できず」。

## 確認音・ログ

**確認音**(`[General] ConfirmSound`):検知したら「ピッ」を鳴らす(`ConfirmBeeper.cs`)。回数でマーカー、高さで再開閉の何回目か、
鳴るチャンネルでドアの側を表す。音はメモリから `PlaySound` で鳴らす(1kHz 前後なので、自分の確認音を取り違えない)。

**ログ**(`AtsDebug.cs`):既定は `%TEMP%\KTY_MARKER.log`(x64 版は `KTY_MARKER64.log`)。4MB を超えたら `.old` に退避する。

- `[Log] Enabled` で止められる。ini が無い・項目が無いときは出力する(ini の置き忘れに気づけるように)。
- `[Log] Directory = plugin` で DLL と同じフォルダに書き出す(書き込めなければ一時フォルダ)。
- 読み込み時に、Windows の版とビルド番号・BVE の版・ログの場所を書く。`[General] LogLevels = true` で、停車中の各周波数のレベルを1秒ごとに書く。
- 主な行:`STATION …`(きっかけ)、`DETECTED #n …`(検知。レベル・左右の差つき)、`BVE DoorOpen/DoorClose event`、`self-test …`、`AUDIO SILENT`。

## 分かった BVE の動き

BVE6 で観察した(早送りと低いサンプリング周波数の WAV の件は BVE5 でも同じ)。

| 場面 | 分かったこと |
|---|---|
| 戸閉 | `DoorClose` は戸閉が**終わったとき**に来る(戸閉の音が鳴り始めてから約 3.4 秒後) |
| 戸開 | `DoorOpen` は戸開が**始まるとき**に来る。戸開の音と到着放送は、その約 50ms 後に鳴り始める |
| 再開閉 | ドアの音を鳴らし直すだけで、途中で `DoorOpen` は来ない |
| 駅ジャンプ | 「`Initialize` → `DoorOpen` → `Elapse`」の順に来る。戸開の音は鳴らない(到着放送のある駅では直後に鳴る) |
| シナリオ開始 | ドアが開いた状態で始まり、戸開の音は鳴らない |
| ハンドル | BVE6 も ATS Plugin Multiplexer も、`Initialize` のときに `SetPower` / `SetBrake` / `SetReverser` を送り直さない |
| フォーカスを外す | BVE は音を出さなくなる(シミュレーションは進む) |
| 早送り | 早送り中に始まった音が停車後も鳴り続ける。続けて鳴るマーカーが `CooldownMs`(1秒)の中に入ることがある |
| 低いサンプリング周波数の WAV | 48kHz にして鳴らすとき、「22050Hz − 周波数」などの位置に鏡像が出る |
| Windows の音量・ミュート | 取り込む地点より後でかかるので、検知に影響しない |
| 音量ミキサーの BVE の音量 | 取り込む音にも効く。約 30dB 下げても検知できたが、約 46dB 以上下げると検知できなかった |
| 出力先の形式 | 取り込む音は出力先のサンプリング周波数に変換された後の音。44.1kHz の出力先では高い周波数ほど少し削られる(19.5kHz で約 5dB)。48kHz では削られない |

## 決めたこと

| 決めたこと | 理由 |
|---|---|
| AtsEX を使わず、音に埋め込んだマーカーで検知する | ATSプラグインの API では、放送の開始・戸閉の開始が分からない |
| WASAPI は自前で呼ぶ(NAudio を使わない) | プロセスループバックは NAudio に無い。依存 DLL が無ければ配布も楽 |
| 停車中だけ検知する | 放送もドアの音も停車中にしか鳴らない。走行中の誤検知の大半が消える |
| マーカーは1つの周波数(`FreqB` は既定で使わない) | 停車中に限れば誤検知はほぼ無い。2つ同時に鳴らすと、スピーカーによっては差の周波数が歪みとして聞こえる |
| ドアの左右は、マーカーを入れるチャンネルで表す | 周波数を増やさずに済む |
| ハンドルの値は受け取ったまま返し、`Initialize` でも戻さない | 0 などに戻すと、ATS Plugin Multiplexer 経由で運転士の操作や前のプラグインのブレーキを上書きしてしまう |
| 音量ミキサーで BVE だけを極端に下げたときの検知失敗には対応しない | 約 30dB 下げても検知でき、ふつうの使い方では問題にならない |
| 配布する ini.sample はログ・確認音をオフにする | 不具合調査のための機能なので、ふだんは止めておく(技術テスト版だけはログをオンにした) |

## 制限・まだ確かめていないこと

**制限**
- BVE のウィンドウを前面にしていない間は検知できない(戸閉開始だけは遅れて補う)。
- 音量ミキサーで BVE だけの音量を極端に下げると検知できない。走行中に下げた場合は自己診断でも分からない。
- しきい値を下げすぎると誤検知が出る(-100dBFS にすると、隣の線路の列車の減速音で誤検知した)。
- マスコンの操作音など、19kHz 付近に成分を持つ音が少しある。誤検知が出たら `MarginDb` を上げるか、マーカーの周波数をずらす。
- 早送りで走ると、再開扉のあとの2回目の戸閉マーカーを取りこぼすことがある(補った `DoorCloseStart` になる)。
- 出力デバイス全体を取り込む方式(Windows 10)では、ほかのアプリの音も混ざる。途中で出力先を変えると、読み込み直すまで止まることがある。
  この方式では、ログの `captureLag` は参考にならない。

**まだ確かめていないこと**
- Windows 10 での動作。
- Bluetoothイヤホンの通話(ハンズフリー)モードで、自己診断が警告を出すか。
- 戸閉開始の補完(`EstimateCloseStart`)と、駅ジャンプ後にハンドルが保たれることの、実機での確認(どちらも試験では確認済み)。
- マーカーが人やスピーカーによって聞こえないか(聞こえるなら `MarkerTool add` の `--db` を下げる)。

## 確認状況

| 機能 | BVE6 | BVE5 |
|---|---|---|
| 到着放送・発車放送・戸閉/戸開の検知 | 確認済み | 確認済み |
| 再開閉(3回目の戸閉まで) | 確認済み | - |
| ドアの左右 | 確認済み | 両側の模擬で確認 |
| 読み込み時の自己診断(左右を含む) | 確認済み | 確認済み |
| 鏡像のチェック | 確認済み(早送り後の誤検知 0 件) | - |
| 無音の検出 | 確認済み | - |
| Bluetoothイヤホン(音楽用のモード)・Windows のミュート | 確認済み | - |

BVE を使わない試験:Uchibo の全 WAV(146 個)で誤検知 0 件。`MarkerTool seqtest`(再開閉・左右の受け渡し 19 通り)と
`sidetest`(左右の判定 9 通り)に合格。DLL を ATS Plugin Multiplexer と同じ呼び方で呼ぶ試験で、駅ジャンプ前後もハンドルが保たれる。

## 過去に直した主な問題

プラグインを作るときの参考になるものだけを残している。

| 問題 | 原因と対策 |
|---|---|
| ビルドでエクスポートの埋め込みが失敗する | `double.NaN` の定数を DllExport のビルド処理(ildasm→ilasm)が扱えない。`double?` を使う(無限大の定数も避ける) |
| 2駅目以降、何も検知しない | プロセスループバックは一度止めると再開できない。止めずに、走行中は音を捨てる |
| 同時に鳴った2つのマーカーを、どちらも検知しない | 鳴っている周波数の組み合わせが1つのマーカーと一致したときだけ検知していた。それぞれ検知するように変えた |
| 駅ジャンプ後、次の駅の戸閉が「#2」になる | 戸閉の回数が持ち越されていた。閉まりかけの途中の `DoorOpen` で数え直す |
| 駅ジャンプ後、レバーサ中立・ブレーキ緩解を返し続ける | `Initialize` でハンドルの値を初期値に戻していた。受け取った値は戻さない |

## 処理の重さ

プラグイン全体で1コアの約 0.5% 以下。解析だけなら音声1秒あたり 0.95ms(64bit)・1.92ms(32bit)。

- 10ms ごとに6つの周波数(マーカー4つ + 比較用2つ)× 960 サンプルを計算する。2つの周波数を1回のループでまとめて計算して速くしている。
- 左右の計算と鏡像のチェックは、マーカーが鳴っている間だけなので、ふだんの計算量は変わらない。
- メモリの確保は検知したときだけ。解析だけの速さは `MarkerTool bench` で測れる。

## 設定項目

DLL と同じフォルダの `KTY_MARKER.ini`(x86 版・x64 版で共有)。無ければすべて既定値。値は半角英数字で書く。説明つきの全項目は
[KTY_MARKER.ini.sample](../src/KTY_MARKER/KTY_MARKER.ini.sample)。

| セクション | 項目(既定値) | 内容 |
|---|---|---|
| `[Log]` | `Enabled`(sample は false) / `Directory`(temp) | ログを出すか / 書き出す場所(`temp` / `plugin`) |
| `[General]` | `Enabled`(true) / `CaptureMode`(Process) / `FallbackToDevice`(true) | 検知を動かすか / 取り込み方 / Process が使えないとき Device にするか |
| | `StopSpeedKmh`(0.1) / `LogLevels`(false) | 停車中とみなす速度 / 1秒ごとのレベル記録 |
| | `ConfirmSound`(false) / `ConfirmVolume`(30) / `ConfirmFrequency`(1000) | 確認音 |
| `[SelfTest]` | `Enabled`(true) / `LevelDb`(-40) / `ShowWarning`(true) / `ResultPanel`(-1) | 読み込み時の自己診断 |
| `[Detect]` | `ThresholdDb`(-70) / `MarginDb`(15) / `RelativeDb`(40) | 検知の条件([仕組み](#仕組み)を参照) |
| | `WindowMs`(20) / `HopMs`(10) / `MinHits`(4) / `ReleaseHits`(10) / `CooldownMs`(1000) | 解析の長さ・間隔 / 何回続けば検知か / 再検知までの条件 |
| | `MirrorCheck`(true) / `MirrorDb`(10) | 鏡像のチェック |
| `[DoorSequence]` | `Enabled`(true) / `MaxAttempts`(3) / `PitchStep`(0.5) / `EstimateCloseStart`(true) | 再開閉の判定 |
| | `StatePanel` / `CloseCountPanel` / `ReopenCountPanel`(-1) | ドアの状態・回数の Panel |
| `[DoorSide]` | `Enabled`(true) / `SideDb`(10) / `BothDb`(6) / `Panel`(-1) | ドアの左右の判定 |
| `[Silence]` | `Enabled`(true) / `MinMs`(300) / `Panel`(-1) | 無音の検出 |
| `[Event1]`〜`[Event8]` | `Name` / `Role` / `FreqA` / `FreqB`(0) / `Panel`(-1) / `HoldMs`(1000) / `ConfirmBeeps` | マーカーの定義 |

- `[EventN]` を1つでも書くと、書いたものだけが有効になる。
- Panel は、ATS Plugin Multiplexer で併用するほかのプラグインと重ならない番号にする(既定はどこにも書き込まない)。Sound には書き込まない。

## MarkerTool

検知・取り込みのコードはプラグインと同じファイルをリンクしている。

| コマンド | 内容 |
|---|---|
| `add <入力.wav> <出力.wav> --event N [--db -30] [--ms 120] [--at 0] [--channel L\|R]` | WAV にマーカーを混ぜる。44.1kHz 未満は 48kHz に変換してから混ぜる |
| `tone <出力.wav> --event N [--pad 300] [--channel L\|R]` | マーカーだけの WAV を作る |
| `detect <ファイル.wav> [--ini KTY_MARKER.ini]` | プラグインと同じ処理で解析し、検知した時刻・左右を表示する |
| `listen [--device \| --pid N] [--seconds 10] [--levels]` | 実際に鳴っている音を取り込んで検知する |
| `selftest <ファイル.wav> [--device]` | ツール自身で WAV を鳴らし、自分のプロセスの音を取り込んで検知する(BVE の中と同じ状況) |
| `beep <出力.wav> [--count 3] [--freq 1000]` | 確認音と同じ WAV を書き出す |
| `seqtest` / `sidetest` | ドアの再開閉・左右の判定を、決まった筋書きで試す |
| `bench [--seconds 60]` | 解析だけの速さを測る |

`--event N` は ini の `[EventN]` の周波数を使う(`--freq 18500` で直接指定も可)。`--ini` に渡すファイル名は `KTY_MARKER.ini` にする。

## ビルドの注意

ビルドの手順は README を参照。

- エクスポートには DllExport を使う。成果物は `bin\<Platform>\Release\net48\<Platform>\` のサブフォルダに出る(1つ上のフォルダの DLL はエクスポートの無いもの)。
- 16個の関数がエクスポートされているかを `dumpbin /exports` で確かめる。
- ソースに `double.NaN` や無限大の定数を書かない(上の表を参照)。

## 配布パッケージの作り方

`python tools\dist\make_dist.py --uchibo <内房線のフォルダ>` で、`dist\` に配布フォルダと zip を作る(DLL は先にビルドしておく)。

- 説明書の元は `tools\dist\readme.txt`(BOM 付き UTF-8・CRLF にして入れる)。説明書を直すときは、ここを直してから作り直す。
- 設定ファイルは `KTY_MARKER.ini.sample` から作る(技術テスト版はログをオン・`Directory = plugin`、内房線サンプルは確認音もオン)。
- 内房線サンプル(`Sample_Uchibo\`)は「BVE Trainsim 内房線」への差分だけ(25 ファイル)。
  - 車両は元の `Uchibo20\E217r` を参照する。持つのは、ドアの音4つ・`Sound.cfg`・`DetailManager.dll` と `detailmodules.txt`(元のプラグインを
    `..\..\..\E217r\Ats\…` で参照)・KTY_MARKER だけ。元のプラグインは自分の DLL の場所から設定を読むので、元の場所のまま参照できる。
  - 路線は、変えたファイル(路線ファイル・停止位置・サウンド一覧・駅リスト)と、マーカー入りの発車音・到着音だけ。
  - 元になる試験用データ(`Uchibo20\E217r_MarkerTest` など)は、内房線のデータの改造なのでリポジトリには入れていない。
    内房線サンプル入りの zip は、GitHub の Releases にだけ置く。
- `--install <フォルダ>` で作ったサンプルを内房線のフォルダにも入れる(既にあるファイルは上書きしない)。`--uninstall` で入れたファイルだけを消す。

## フォルダ構成

```
KTY_MARKER/
  README.md                  利用者向けの説明
  LICENSE                    MIT(内房線サンプルは対象外)
  KTY_MARKER.sln
  DllExport.bat, packages/   DllExport(ビルドに必要)
  src/KTY_MARKER/            プラグイン本体
    AtsExports.cs            BVE から呼ばれる16個の関数
    AtsLogic.cs              停車中の判定、検知結果の処理、OnStationEvent
    StationEvent.cs          きっかけの種類・回数
    DoorSequenceTracker.cs   ドアの再開閉を1回ずつ見分ける
    LoopbackCapture.cs       WASAPI のループバック取り込み(専用スレッド)
    CoreAudioInterop.cs      WASAPI の COM 定義
    ToneDetector.cs          マーカーの検知(Goertzel 法)・ドアの左右・鏡像のチェック
    SelfTest.cs              読み込み時の自己診断と警告のウィンドウ
    ConfirmBeeper.cs         確認音・試験音(PlaySound)
    MarkerConfig.cs          KTY_MARKER.ini の読み込み
    AtsDebug.cs              ログ
    AtsInterop.cs            ATSプラグインの構造体・定数
    DllExportCompileCheckShim.cs  dotnet build で簡易コンパイルするときだけ使う代用品
    KTY_MARKER.ini.sample    設定ファイルのサンプル
  tools/MarkerTool/          マーカー入り WAV の作成・検知の確認ツール
  tools/dist/                配布パッケージを作るスクリプトと説明書の元
  docs/DEVELOPMENT.md        このメモ
```
