==============================================================================
  KTY_MARKERプラグイン 技術テスト版 (2026-10-30)
  BVE Trainsim 5 / 6 用 ATSプラグイン ― 音声マーカー検知
==============================================================================

■ はじめに

  KTY_MARKERプラグイン は、BVE の「駅放送が鳴り始めた瞬間」「ドアが閉まり始めた・開き始めた瞬間」を
  プラグインで知るための仕組みです。ATSプラグインの仕組みでは、これらのタイミングは本来分かりません。
  放送やドアの音のWAVの先頭に、ほとんど聞こえない高い音(18〜19.5kHz、0.12秒)を
  「マーカー」として入れておきます。プラグインは BVE が鳴らしている音を聞いてマーカーを見つけます。

  この配布物は【技術テスト版】です。今後この技術を使った実用的なプラグインを作るにあたって、
  いちばんの目的は「いろいろなパソコン・音の出力先で、ちゃんと動くか」を確かめることです。
　不具合があった場合にはご報告いただけると助かります。

  ※ 運転を楽しむためのものではなく、プラグイン作者向けの技術デモです。


■ 中身

  readme.txt          この説明書
  Sample_Uchibo\      内房線サンプル(「BVE Trainsim 内房線」に追加して使う差分だけ)
  Plugin\             プラグイン本体(自分の車両に入れる人向け)
      KTY_MARKER.dll      BVE5用(32bit)
      KTY_MARKER64.dll    BVE6用(64bit)
      KTY_MARKER.ini      設定ファイル
  MarkerTool\         マーカー入りWAVを作る・調べるツール(自分の車両に入れる人向け)


■ 動作環境

  ・BVE Trainsim 5.8 または 6
  ・Windows 11 をおすすめします。
      Windows 11 では、BVE の音だけを聞き取ります。ほかのアプリ(動画・音楽など)の音は混ざりません。
      Windows 10 では、パソコン全体の音を聞き取る方式に自動で切り替わります。
      このとき、BVE と一緒に動画や音楽を流すと、うまく動かないことがあります。
      ※Windows 10 では、まだ一度も試していません。
  ・高い音(18〜20kHz)がそのまま出る音の出力先
      ふつうのスピーカー・ヘッドホン・Bluetoothイヤホン(音楽用のモード)では動きます。
      Bluetoothイヤホンの「通話(ハンズフリー)モード」では高い音が出ないので動きません
      (読み込みの時に警告が出ます)。


■ 内房線サンプルの入れ方

  先に「BVE Trainsim 内房線」(内房線制作チーム、Uchibo20)を入れておいてください。
  このサンプルは、内房線のデータを読み込んで使います。
　https://bvets.net/uchibo/

  1. 「BVE Trainsim 内房線」を入れたフォルダを開きます。
     「Uchibo20」フォルダと「Uchibo_3793F_ATS-P.txt」が並んでいるフォルダです。

  2. このzipの「Sample_Uchibo」フォルダの【中身】(「Uchibo_3793F_KTY_MARKER.txt」と「Uchibo20」フォルダ)を、
     そのフォルダにコピーします。
     「Uchibo20」は、元からあるフォルダに上書きしてください。
     元のファイルを書き換えることはありません。新しいファイルが増えるだけです。
     (「同じ名前のファイルがあります」と出た場合は、前の版のサンプルを入れてあるときだけです。上書きしてかまいません)

  3. BVE を起動し、シナリオの一覧から次を選びます。
         [KTY_MARKER 技術テスト] 3793F | 快速 15両 | 君津

  増えるファイル(すべて Uchibo20 の中か、その隣):
      Uchibo_3793F_KTY_MARKER.txt                     シナリオ
      Uchibo20\E217r_KTY_MARKER\                      E217系(元の E217r を参照し、ドアの音とプラグインだけ違う)
      Uchibo20\Maps\3793F_P_KTY_MARKER.txt            路線ファイル(読み込み先を変えたコピー)
      Uchibo20\Maps\Stop_15_KTY_MARKER.txt            停止位置(開くドアの側を変えたコピー)
      Uchibo20\Maps\3793F\Sounds_KTY_MARKER.csv       サウンド一覧(マーカー入りの発車音・到着音を使う)
      Uchibo20\Maps\3793F\Stations_KTY_MARKER.csv     駅リスト(浜野・八幡宿を再開閉しやすくしたコピー)
      Uchibo20\Sounds\Map\3793F_KTY_MARKER\           マーカー入りの発車音・到着音

  消すときは、上のファイルとフォルダを消してください。


■ 試し方

  ふつうに千葉から五井まで運転し、各駅に止まってください。
  停車中にマーカーを見つけると、確認音「ピッ」が鳴ります(この音はプラグインが鳴らしています)。

      ピッの回数   意味
      ----------   ------------------------------------------
        1回        発車放送が鳴り始めた(千葉・本千葉・蘇我・浜野・八幡宿)
        2回        ドアが閉まり始めた
        3回        ドアが開き始めた(再開閉の「開く」も)
        4回        到着放送が鳴り始めた(蘇我だけ)

  ・ドアのピッは、開く側のスピーカー(左右)からだけ鳴ります。
      本千葉・浜野・五井 … 左側      千葉・蘇我・八幡宿 … 右側
      ※ 試験のため、わざとホームと反対側のドアが開く駅があります。
  ・再開閉(閉まりかけて、また開く)では、2回目・3回目の音が高くなります。
    浜野・八幡宿では、9割の確率で再開閉します。
  ・ピッが大きすぎる・小さすぎるときは、Uchibo20\E217r_KTY_MARKER\Ats\x64(BVE5は x86)の
    KTY_MARKER.ini で ConfirmVolume を変えてください(下の「設定」を参照)。


■ うまく動かないとき

  ・シナリオの読み込み時に「KTY_MARKER の動作確認で…」という警告が出た
      → 今の音の出力先では、高い音が届いていません。警告に書かれている原因を確かめてください。
        (Bluetoothの通話モード・Windows の「モノラル オーディオ」・立体音響 など)
  ・ピッが鳴らない
      → BVE のウィンドウを前面にしていますか? BVE は、ほかのウィンドウを操作している間は音を出さないので、
        その間はマーカーを見つけられません。
  ・早送りで走ると、ピッが余分に鳴る・足りないことがあります(既知の制限。下を参照)。

  〇記録ファイル(ログ)
      内房線サンプルの場合:
          BVE6 … Uchibo20\E217r_KTY_MARKER\Ats\x64\KTY_MARKER64.log
          BVE5 … Uchibo20\E217r_KTY_MARKER\Ats\x86\KTY_MARKER.log
      自分の車両に入れた場合は、KTY_MARKER.dll / KTY_MARKER64.dll と同じフォルダにできます。
      そこに無いときは、一時フォルダにあります(エクスプローラーのアドレス欄に %TEMP% と入れて開く)。
      ※ 4MB を超えると、古い分は「.log.old」に移ります。
      ※ ログには、Windows の版、BVE の版、音の取り込みの状態、マーカーを見つけた時刻と大きさが入ります。


■ 自分の車両に入れる(作者向け)

  1. マーカー入りWAVを作る(元のファイルは上書きせず、別名で作ってください)
       MarkerTool\MarkerTool.exe add DoorClsL.wav DoorClsL_marker.wav --event 2 --channel L
       MarkerTool\MarkerTool.exe add DoorClsR.wav DoorClsR_marker.wav --event 2 --channel R
       MarkerTool\MarkerTool.exe add DoorOpnL.wav DoorOpnL_marker.wav --event 3 --channel L
       MarkerTool\MarkerTool.exe add DoorOpnR.wav DoorOpnR_marker.wav --event 3 --channel R
       MarkerTool\MarkerTool.exe add 発車放送.wav 発車放送_marker.wav --event 1
       MarkerTool\MarkerTool.exe add 到着放送.wav 到着放送_marker.wav --event 4
     ・ドアの音は、左側(OpenLeft・CloseLeft)に --channel L、右側(OpenRight・CloseRight)に --channel R。
     ・44.1kHz より低いWAV(22.05kHz・11.025kHz など)は、自動で 48kHz にしてから混ぜます。
     ・作ったWAVにマーカーが入っているかは、MarkerTool\MarkerTool.exe detect <ファイル.wav> で確かめられます。

  2. 車両・路線の参照先を、作ったWAVに差し替える
       ドアの音       … Sound.cfg の [Door] OpenLeft / OpenRight / CloseLeft / CloseRight
       発車・到着放送 … 駅リストの departureSoundKey / arrivalSoundKey が指すサウンド一覧のファイル

  3. Plugin フォルダの3つのファイルを、車両の ATS プラグインと同じフォルダに置き、
     Vehicle.txt の Ats32 / Ats64 か、DetailManager・ATS Plugin Multiplexer の
     detailmodules.txt に KTY_MARKER.dll(BVE5) / KTY_MARKER64.dll(BVE6) を追加します。
     ・KTY_MARKER.ini は、DLL と同じフォルダに置いてください(BVE5用・BVE6用が別のフォルダなら両方に)。
     ・ハンドル・ブレーキには手を出さないので、ほかのプラグインと併用しても運転には影響しません。
     ・Panel・Sound の番号は、初期設定ではどこにも書き込みません。

  検知した結果をほかのプラグインから使う方法(Panel に書き込む設定)は、KTY_MARKER.ini の説明を見てください。


■ 設定(KTY_MARKER.ini)

  DLL と同じフォルダの KTY_MARKER.ini で設定します。変えたら、シナリオを読み込み直すと反映されます。
  値は半角英数字で書いてください。行の途中の「 ;」から後ろは説明(コメント)です。
  よく使う項目:

    [Log] Enabled          true = 記録(ログ)を書き出す(技術テスト版はオン)
    [Log] Directory        plugin = DLL と同じフォルダに書き出す / temp = 一時フォルダ
    [General] ConfirmSound true = 見つけたときに確認音「ピッ」を鳴らす
                           (内房線サンプルはオン、Plugin フォルダの設定はオフ)
    [General] ConfirmVolume 確認音の大きさ(%、0〜100)
    [SelfTest] ShowWarning false = 読み込み時の動作確認で失敗しても、警告を出さない

  ほかの項目は、KTY_MARKER.ini の中の説明を見てください。


■ 分かっている制限

  ・BVE のウィンドウを前面にしていない間は、マーカーを見つけられません(BVE がその間は音を出さないため)。
  ・早送りで走ると、取りこぼしたり、余分に見つけたりすることがあります。
  ・音量ミキサーで BVE だけの音量を極端に下げる(およそ -45dB 以下)と、正常に動きません。
    Windows 全体の音量やミュートは影響しません。
  ・Bluetoothイヤホンの通話(ハンズフリー)モードでは動きません。
  ・Windows 10 では、ほかのアプリの音も一緒に聞き取ります。また、使っている途中で音の出力先を変えると、
    シナリオを読み込み直すまで動かなくなることがあります。
  ・確認音は Windows 標準の方法(PlaySound)で鳴らします。同じ方法で音を鳴らすほかのプラグインと一緒に使うと、
    互いに音を止め合うことがあります。
  ・マーカーの音(18〜19.5kHz)は、人によっては、またスピーカーによっては聞こえることがあります。


■ 規約・免責

  ・このプラグインを使ったことで生じた損害について、作者は責任を負いません。
　・この技術は流用フリーですが、様々な環境で充分な精査が出来ていないため、環境によっては正常に動かないこともあります。
　　その点は充分にご留意してください。
  ・内房線サンプルは「BVE Trainsim 内房線」(内房線制作チーム)のデータをもとにしています。
    元データの説明書にしたがい、BVE Trainsim 用の改造データとして作りました。
    サンプルについて、内房線制作チームに問い合わせないでください。


■ 更新履歴

  2026-10-30  技術テスト版 初版公開
