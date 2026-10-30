# KTY_MARKER(音声マーカー検知プラグイン)

BVE Trainsim 5 / 6 の ATSプラグインで、「駅の放送が鳴り始めた瞬間」「ドアが閉まり始めた・開き始めた瞬間(再開閉を含む)」を
知るための仕組みです。ATSプラグインの仕組みでは、これらのタイミングは本来分かりません。

放送やドアの音の WAV の先頭に、ほとんど聞こえない高い音(18〜19.5kHz、0.12秒)を「マーカー」として入れておきます。
プラグインは BVE が鳴らしている音を聞き取り、マーカーを見つけると、その瞬間をきっかけとして知らせます。AtsEX は使いません。

> **技術テスト版です。** 今後この技術を使った実用的なプラグインを作るための技術デモで、運転を楽しむためのものではありません。
> いろいろなパソコン・音の出力先で動くかを確かめている段階です。

## 見つけられるもの

| きっかけ | マーカーを入れる音 |
|---|---|
| 発車放送が鳴り始めた | 駅リストの `departureSoundKey` の音 |
| 到着放送が鳴り始めた | 駅リストの `arrivalSoundKey` の音 |
| ドアが閉まり始めた(1回目・再開閉後の2回目・3回目) | `Sound.cfg` の `[Door]` `CloseLeft` / `CloseRight` |
| ドアが開き始めた・再開扉 | `Sound.cfg` の `[Door]` `OpenLeft` / `OpenRight` |
| 開いたドアの側(左・右) | ドアの音の左右のチャンネルのどちらにマーカーを入れたかで見分ける |

- 停車中だけ聞き取ります。走行音やブレーキの音を取り違えないためです。
- 運転(ハンドル・ブレーキ)には手を出しません。ほかのプラグインと一緒に使っても、運転には影響しません。
- シナリオの読み込み時に、この環境で検知できるかを自分で確かめます(自己診断)。高い音が届かない出力先では警告を出します。

## 動作環境

- BVE Trainsim 5.8 または 6(BVE5 は `KTY_MARKER.dll`、BVE6 は `KTY_MARKER64.dll`)
- Windows 11 をおすすめします。
  - Windows 11 では、BVE の音だけを聞き取ります(ほかのアプリの音は混ざりません)。
  - Windows 10 では、パソコン全体の音を聞き取る方式に自動で切り替わります。Windows 10 では、まだ試していません。
- 高い音(18〜20kHz)がそのまま出る音の出力先
  - ふつうのスピーカー・ヘッドホン・Bluetoothイヤホン(音楽用のモード)で動きます。
  - Bluetoothイヤホンの通話(ハンズフリー)モードでは動きません。

## ダウンロード

[Releases](../../releases) から最新の zip をダウンロードしてください。

| zipの中 | 中身 |
|---|---|
| `readme.txt` | 説明書(入れ方・試し方・確認音の意味・ログの場所など) |
| `Plugin\` | プラグイン本体(`KTY_MARKER.dll`・`KTY_MARKER64.dll`)と設定ファイル `KTY_MARKER.ini` |
| `MarkerTool\` | マーカー入りの WAV を作る・調べるツール |
| `Sample_Uchibo\` | 内房線サンプル(下を参照) |

技術テスト版の設定ファイルは、記録(ログ)をオンにして、DLL と同じフォルダに書き出します(`KTY_MARKER.log` / `KTY_MARKER64.log`)。
不具合を報告するときは、このログを添えてください。

## 内房線サンプルで試す

「[BVE Trainsim 内房線](https://bvets.net/uchibo/)」(内房線制作チーム)を入れてある場合は、すぐに試せます。

1. zip の `Sample_Uchibo` フォルダの中身(`Uchibo_3793F_KTY_MARKER.txt` と `Uchibo20` フォルダ)を、内房線を入れたフォルダ
   (`Uchibo20` フォルダと `Uchibo_3793F_ATS-P.txt` があるフォルダ)にコピーします。元のファイルは書き換えません。
2. BVE で「[KTY_MARKER 技術テスト] 3793F | 快速 15両 | 君津」を選んで、千葉から五井まで運転します。
3. 停車中にマーカーを見つけると、確認音「ピッ」が鳴ります。

   | ピッの回数 | 意味 |
   |---|---|
   | 1回 | 発車放送が鳴り始めた |
   | 2回 | ドアが閉まり始めた(再開閉の2回目・3回目は音が高くなる) |
   | 3回 | ドアが開き始めた |
   | 4回 | 到着放送が鳴り始めた(蘇我) |

   ドアのピッは、開く側のスピーカーからだけ鳴ります。試験のため、本千葉・浜野・五井では左側(ホームと反対側)のドアが開きます。
   浜野・八幡宿では、9割の確率で再開閉します。

サンプルは内房線のデータを参照する差分だけです。車両は元の E217系(`Uchibo20\E217r`)を使い、ドアの音とプラグインだけを差し替えています。

## 自分の車両で使う

1. マーカー入りの WAV を作ります(元のファイルは上書きせず、別名で作ってください)。
   ```
   MarkerTool.exe add DoorClsL.wav DoorClsL_marker.wav --event 2 --channel L
   MarkerTool.exe add DoorClsR.wav DoorClsR_marker.wav --event 2 --channel R
   MarkerTool.exe add DoorOpnL.wav DoorOpnL_marker.wav --event 3 --channel L
   MarkerTool.exe add DoorOpnR.wav DoorOpnR_marker.wav --event 3 --channel R
   MarkerTool.exe add 発車放送.wav 発車放送_marker.wav --event 1
   MarkerTool.exe add 到着放送.wav 到着放送_marker.wav --event 4
   ```
   - ドアの音は、左側に `--channel L`、右側に `--channel R` を付けます(開いた側を見分けるため)。
   - 44.1kHz より低い WAV(22.05kHz・11.025kHz など)は、自動で 48kHz にしてから混ぜます。
   - `MarkerTool.exe detect <ファイル.wav>` で、マーカーが入っているかを確かめられます。
2. `Sound.cfg` の `[Door]` と、駅リストの `departureSoundKey` / `arrivalSoundKey` が指すサウンド一覧を、作った WAV に差し替えます。
3. `Plugin` フォルダの DLL と `KTY_MARKER.ini` を車両の ATSプラグインと同じフォルダに置きます。
   `Vehicle.txt` の `Ats32` / `Ats64`、または DetailManager・[ATS Plugin Multiplexer](https://github.com/magicant/bve-plugin-multiplexer)
   の `detailmodules.txt` に追加します。

検知した結果は、`KTY_MARKER.ini` で指定した Panel の番号に書き込めます(初期設定ではどこにも書き込みません)。
ほかのプラグインから使うときは、ATS Plugin Multiplexer で併用するプラグインと番号が重ならないようにしてください。

プラグインのソースに処理を足す場合は、`AtsLogic.OnStationEvent` にすべてのきっかけが集まります
(種類・何回目か・ドアの側・停車してからの時間)。詳しくは[開発メモ](docs/DEVELOPMENT.md)を参照してください。

## 設定

DLL と同じフォルダの `KTY_MARKER.ini` で設定します。全項目の説明は
[KTY_MARKER.ini.sample](src/KTY_MARKER/KTY_MARKER.ini.sample) にあります。よく使う項目:

| 項目 | 内容 |
|---|---|
| `[Log] Enabled` / `Directory` | 記録(ログ)を書き出すか / 書き出す場所(`plugin` = DLL と同じフォルダ、`temp` = 一時フォルダ) |
| `[General] ConfirmSound` / `ConfirmVolume` | 見つけたときに確認音を鳴らすか / その大きさ |
| `[SelfTest] ShowWarning` | 読み込み時の自己診断で失敗したとき、警告を出すか |
| `[EventN] FreqA` / `Panel` | マーカーの周波数 / 見つけたときに1を書き込む Panel の番号 |

## 分かっている制限

- BVE のウィンドウを前面にしていない間は、マーカーを見つけられません(BVE がその間は音を出さないため)。
- 早送りで走ると、取りこぼしたり、余分に見つけたりすることがあります。
- 音量ミキサーで BVE だけの音量を極端に下げる(およそ -45dB 以下)と、正常に動きません。Windows 全体の音量・ミュートは影響しません。
- Bluetoothイヤホンの通話(ハンズフリー)モードでは動きません。
- マーカーの音(18〜19.5kHz)は、人やスピーカーによっては聞こえることがあります。

## ビルド

Visual Studio 2022(.NET Framework 4.8)で `KTY_MARKER.sln` を開き、`Release|x86` と `Release|x64` の両方をビルドします。

```
MSBuild.exe KTY_MARKER.sln /t:Restore
MSBuild.exe KTY_MARKER.sln /p:Configuration=Release /p:Platform=x86
MSBuild.exe KTY_MARKER.sln /p:Configuration=Release /p:Platform=x64
```

| 成果物 | 場所 |
|---|---|
| `KTY_MARKER.dll`(BVE5用) | `src\KTY_MARKER\bin\x86\Release\net48\x86\` |
| `KTY_MARKER64.dll`(BVE6用) | `src\KTY_MARKER\bin\x64\Release\net48\x64\` |
| `MarkerTool.exe` | `tools\MarkerTool\bin\Release\net48\` |

- BVE から呼べる関数のエクスポートには [DllExport](https://github.com/3F/DllExport) を使っています。ビルドに必要なので、`packages\` に含めてあります。
- 依存する DLL はありません(WASAPI は自前で呼んでいます)。
- 16個の関数がエクスポートされているかは `dumpbin /exports` などで確かめられます。

## ライセンス

- プラグイン本体・MarkerTool・このリポジトリのソースは [MIT ライセンス](LICENSE)です。
- Releases の zip に入っている内房線サンプル(`Sample_Uchibo`)は対象外です。
  - サンプルは「BVE Trainsim 内房線」(内房線制作チーム)のデータをもとにした改造データです。
    元データの説明書の条件(BVE Trainsim 用のアドオン・素材の制作には流用・改造してよい)にしたがって配布しています。
  - 元データと同じく、**事故の再現には使わないでください。**
  - サンプルについて、内房線制作チームに問い合わせないでください。
- `packages\DllExport.1.8.1` は DllExport のライセンス(MIT)にしたがいます。
- 使ったことで生じた損害について、作者は責任を負いません。
