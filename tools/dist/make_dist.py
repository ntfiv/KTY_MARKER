# -*- coding: utf-8 -*-
r"""KTY_MARKER の配布パッケージ(技術テスト版)を作る。

使い方:
    python make_dist.py --uchibo <dir>   dist\ に配布フォルダとzipを作る
                                         <dir> = 開発用の内房線フォルダ(Uchibo20 と、試験用データ *_MarkerTest があるフォルダ)。
                                         環境変数 KTY_MARKER_UCHIBO でも指定できる
    python make_dist.py --uchibo <dir> --install <dir2>
                                         作った内房線サンプルを <dir2>(Uchibo20 があるフォルダ)にも入れる
                                         (動作確認用。既にあるファイルは上書きせず、止まる)
    --uninstall <dir2>                   前に入れたサンプルを消してから作る

内房線サンプルの元になる試験用データ(Uchibo20\E217r_MarkerTest など。docs\DEVELOPMENT.md の「試験用シナリオ」)は
リポジトリに入っていない(内房線制作チームのデータの改造のため)。

内房線サンプルは、BVE Trainsim 内房線(Uchibo20)に対する差分だけ。Uchibo20 と同じフォルダに入れると使える。
車両は元の E217r を参照し(Notch・Parameters・Panel・走行音・ATSなどのプラグイン)、変えたもの(ドアの音・Sound.cfg・
KTY_MARKER)だけを持つ。路線は、変えたファイル(路線ファイル・停止位置・サウンド一覧・駅リスト・発車音と到着音)のコピーだけ。
元データの説明書: 「配布物を流用・改造しBVE用のアドオンや素材データを制作する際には、作者に問い合わせを行う必要は
ありません」「参照も可能」(Uchibo20\\documents\\Read_me(JA).pdf の16・17ページ)。
"""
import argparse
import os
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))           # KTY_MARKER\
# 開発用の内房線フォルダ(試験用データを作った場所)。--uchibo か環境変数 KTY_MARKER_UCHIBO で指定する。
U20 = ""

VERSION = "2026-10-30"
PACKAGE = "KTY_MARKER_TechTest_20261030"
OUT_ROOT = os.path.join(ROOT, "dist")
OUT = os.path.join(OUT_ROOT, PACKAGE)

DLL86 = os.path.join(ROOT, r"src\KTY_MARKER\bin\x86\Release\net48\x86\KTY_MARKER.dll")
DLL64 = os.path.join(ROOT, r"src\KTY_MARKER\bin\x64\Release\net48\x64\KTY_MARKER64.dll")
TOOL = os.path.join(ROOT, r"tools\MarkerTool\bin\Release\net48")
INI_SAMPLE = os.path.join(ROOT, r"src\KTY_MARKER\KTY_MARKER.ini.sample")
README = os.path.join(HERE, "readme.txt")

VEHICLE = "E217r_KTY_MARKER"       # サンプル車両のフォルダ名(Uchibo20\ の下)
SUFFIX = "_KTY_MARKER"             # 路線側のファイル名に付ける印
CRLF = "\r\n"


def die(msg):
    print("ERROR: " + msg)
    sys.exit(1)


def read_text(path, encoding="utf-8"):
    with open(path, "rb") as f:
        data = f.read()
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    # 改行はLFにそろえて返す(書き出すときにCRLFにする)。
    return data.decode(encoding).replace("\r\n", "\n")


def write_text(path, text, encoding="utf-8", bom=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    text = text.replace("\r\n", "\n").replace("\n", CRLF)
    with open(path, "wb") as f:
        if bom:
            f.write(b"\xef\xbb\xbf")
        f.write(text.encode(encoding))


def copy(src, dst):
    if not os.path.isfile(src):
        die("見つからない: " + src)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)


def replace_once(text, old, new, where):
    if text.count(old) != 1:
        die(f"{where}: 「{old}」が{text.count(old)}個ある(1個のはず)")
    return text.replace(old, new)


# ---- 設定ファイル(技術テスト版) ----

def make_ini(confirm_sound, where):
    """KTY_MARKER.ini.sample から、技術テスト版の設定を作る(ログはオン・DLLと同じフォルダに書き出す)。"""
    text = read_text(INI_SAMPLE)
    text = replace_once(text, "; KTY_MARKER.ini - 設定ファイル(サンプル)\n",
                        f"; KTY_MARKER.ini - 設定ファイル(技術テスト版 {VERSION}、{where})\n"
                        "; 技術テスト版なので、ログ(動作の記録)はオンにして、このファイルと同じフォルダに書き出します。\n"
                        "; ログを送っていただくと、どんな環境で動く・動かないかを調べられます(readme.txt を参照)。\n", "ini")
    text = replace_once(text, "\nEnabled = false\n; 書き出す場所。", "\nEnabled = true\n; 書き出す場所。", "ini [Log] Enabled")
    text = replace_once(text, "Directory = temp\n", "Directory = plugin\n", "ini [Log] Directory")
    text = replace_once(text, "LogLevels = false ", "LogLevels = true  ", "ini LogLevels")
    if confirm_sound:
        text = replace_once(text, "ConfirmSound = false ", "ConfirmSound = true  ", "ini ConfirmSound")
    return text


# ---- 内房線サンプル ----

def build_sample(dst):
    v = os.path.join(dst, "Uchibo20", VEHICLE)
    src_v = os.path.join(U20, "E217r_MarkerTest")   # 開発用のテスト車両(E217rの丸ごとコピー + 変更)

    # 車両ファイル: 変えていないものは元の E217r を参照する。
    write_text(os.path.join(v, "Vehicle_3793F.txt"), CRLF.join([
        "BveTs Vehicle 2.00",
        r"PerformanceCurve = ..\E217r\Notch\Notch.txt",
        r"Parameters = ..\E217r\Parameters.txt",
        r"Panel = ..\E217r\Panel\Panel_3793F.txt",
        "Sound = Sound.cfg",
        r"Ats32 = Ats\x86\DetailManager.dll",
        r"Ats64 = Ats\x64\DetailManager.dll",
        r"MotorNoise = ..\E217r\Sound\Motor.txt",
        "",
    ]), encoding="ascii")

    # Sound.cfg: ドアの音4つだけマーカー入りのWAV(このフォルダ)、ほかは元の E217r の音。
    door = {"Sound\\DoorOpnL_marker.wav", "Sound\\DoorOpnR_marker.wav", "Sound\\DoorClsL_marker.wav", "Sound\\DoorClsR_marker.wav"}
    lines, used_door, rewritten = [], set(), 0
    for line in read_text(os.path.join(src_v, "Sound.cfg")).split("\n"):
        key, sep, value = line.partition("=")
        # 値の後ろに「;コメント」が付いている行もある(コメントはそのまま残す)。
        path, semi, comment = value.partition(";")
        path = path.strip()
        if sep and not line.lstrip().startswith(";") and path.lower().endswith(".wav"):
            if path in door:
                used_door.add(path)
            else:
                line = f"{key}{sep} ..\\E217r\\{path}" + (f" {semi}{comment}" if semi else "")
                rewritten += 1
        elif sep and not line.lstrip().startswith(";") and ".wav" in value.lower():
            die("Sound.cfg の読めない行: " + line)
        lines.append(line)
    if used_door != door:
        die("Sound.cfg のドアの音が想定と違う: " + ", ".join(sorted(used_door)))
    print(f"Sound.cfg: {rewritten} sounds point to ..\\E217r, {len(used_door)} door sounds are local")
    write_text(os.path.join(v, "Sound.cfg"), "\n".join(lines))
    for name in sorted(door):
        copy(os.path.join(src_v, name), os.path.join(v, name))

    # ATS: DetailManager(元の E217r と同じもの)で、元の E217r のプラグインと KTY_MARKER を読み込む。
    # 元のプラグインは自分のDLLの場所から設定(ATS.ini など)を読むので、元の場所のまま参照する。
    for bits, dll, src_dll in (("x86", "KTY_MARKER.dll", DLL86), ("x64", "KTY_MARKER64.dll", DLL64)):
        a = os.path.join(v, "Ats", bits)
        copy(os.path.join(U20, "E217r", "Ats", bits, "DetailManager.dll"), os.path.join(a, "DetailManager.dll"))
        modules = [m.strip() for m in read_text(os.path.join(U20, "E217r", "Ats", bits, "detailmodules.txt")).split("\n") if m.strip()]
        lines = [f"..\\..\\..\\E217r\\Ats\\{bits}\\{m}" for m in modules] + [dll]
        write_text(os.path.join(a, "detailmodules.txt"), CRLF.join(lines) + CRLF, encoding="ascii")
        copy(src_dll, os.path.join(a, dll))
        write_text(os.path.join(a, "KTY_MARKER.ini"), make_ini(True, "内房線サンプル車両。確認音もオン"))

    # 路線: 変えたファイルだけ(名前に _KTY_MARKER を付ける)。
    maps_src = os.path.join(U20, "Maps")
    maps = os.path.join(dst, "Uchibo20", "Maps")
    route = read_text(os.path.join(maps_src, "3793F_P_MarkerTest.txt"))
    header_old = route.split("\n")[1:5]
    if not header_old[0].startswith("//\tKTY_MARKER試験用"):
        die("3793F_P_MarkerTest.txt の先頭のコメントが想定と違う")
    header_new = [
        "//\tKTY_MARKER(技術テスト版)の内房線サンプル: 3793F_P.txt のコピー。違うのは次の3つの読み込み先だけ。",
        "//\t  3793F\\Sounds_KTY_MARKER.csv  : 最初の5駅の発車音と蘇我の到着音を、マーカー入りの Sounds\\Map\\3793F_KTY_MARKER\\ に差し替え",
        "//\t  3793F\\Stations_KTY_MARKER.csv: 再開閉を試しやすいよう、浜野・八幡宿の再開閉の確率を0.9に",
        "//\t  Stop_15_KTY_MARKER.txt       : ドアの左右を試すため、本千葉・浜野・五井で左側のドアが開くように",
    ]
    route = "\n".join(route.split("\n")[:1] + header_new + route.split("\n")[5:])
    route = route.replace("_MarkerTest", SUFFIX)
    write_text(os.path.join(maps, f"3793F_P{SUFFIX}.txt"), route)
    write_text(os.path.join(maps, f"Stop_15{SUFFIX}.txt"), read_text(os.path.join(maps_src, "Stop_15_MarkerTest.txt")).replace("_MarkerTest", SUFFIX))
    with open(os.path.join(maps_src, "3793F", "Sounds_MarkerTest.csv"), "rb") as f:
        sounds = f.read()
    if sounds.count(b"3793F_MarkerTest") != 6:
        die("Sounds_MarkerTest.csv の差し替えが6つではない")
    os.makedirs(os.path.join(maps, "3793F"), exist_ok=True)
    with open(os.path.join(maps, "3793F", f"Sounds{SUFFIX}.csv"), "wb") as f:
        f.write(sounds.replace(b"3793F_MarkerTest", b"3793F" + SUFFIX.encode()))
    copy(os.path.join(maps_src, "3793F", "Stations_MarkerTest.csv"), os.path.join(maps, "3793F", f"Stations{SUFFIX}.csv"))
    snd_src = os.path.join(U20, "Sounds", "Map", "3793F_MarkerTest")
    for name in sorted(os.listdir(snd_src)):
        if name.lower().endswith(".wav"):
            copy(os.path.join(snd_src, name), os.path.join(dst, "Uchibo20", "Sounds", "Map", "3793F" + SUFFIX, name))

    # シナリオ
    write_text(os.path.join(dst, "Uchibo_3793F_KTY_MARKER.txt"), CRLF.join([
        "BveTs Scenario 2.00:UTF-8",
        "",
        f"Route\t\t\t= Uchibo20\\Maps\\3793F_P{SUFFIX}.txt",
        f"Vehicle\t\t\t= Uchibo20\\{VEHICLE}\\Vehicle_3793F.txt",
        "Image\t\t\t= Uchibo20\\Uchibo_3793F.jpg",
        "",
        "Title\t\t\t= [KTY_MARKER 技術テスト] 3793F | 快速 15両 | 君津",
        "Comment\t\t\t= 音声マーカー検知プラグイン KTY_MARKER の技術テスト用です。停車中に放送・ドアの音を検知すると「ピッ」が鳴ります"
        "(1回=発車放送、2回=戸閉、3回=戸開、4回=到着放送。ドアは開く側のスピーカーから)。"
        "本千葉・浜野・五井は左、千葉・蘇我・八幡宿は右のドアが開きます(試験用)。浜野・八幡宿は再開閉しやすくしてあります。"
        f"記録(ログ)は Uchibo20\\{VEHICLE}\\Ats\\x64(BVE5は x86)にできます。",
        "RouteTitle\t\t= 内房線 下り (KTY_MARKER 技術テスト)",
        "VehicleTitle\t= E217系 (機器更新車) 15両 + KTY_MARKER",
        "Author\t\t\t= 内房線制作チーム (路線・車両) / KTY_MARKER (差分)",
        "",
    ]), bom=True)


def build():
    if os.path.exists(OUT):
        shutil.rmtree(OUT)
    os.makedirs(OUT)

    # メモ帳などで文字化けしないよう、BOM付きUTF-8・CRLFにする。
    write_text(os.path.join(OUT, "readme.txt"), read_text(README), bom=True)

    p = os.path.join(OUT, "Plugin")
    copy(DLL86, os.path.join(p, "KTY_MARKER.dll"))
    copy(DLL64, os.path.join(p, "KTY_MARKER64.dll"))
    write_text(os.path.join(p, "KTY_MARKER.ini"), make_ini(False, "自分の車両に入れる用"))

    for name in ("MarkerTool.exe", "MarkerTool.exe.config"):
        copy(os.path.join(TOOL, name), os.path.join(OUT, "MarkerTool", name))

    build_sample(os.path.join(OUT, "Sample_Uchibo"))

    zip_path = OUT + ".zip"
    if os.path.exists(zip_path):
        os.remove(zip_path)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for base, _, files in os.walk(OUT):
            for name in sorted(files):
                full = os.path.join(base, name)
                arc = os.path.relpath(full, OUT_ROOT)
                if not arc.isascii():
                    die("zipの中の名前は半角英数字だけにする(Windowsの展開で文字化けするため): " + arc)
                z.write(full, arc)
    print("package:", OUT)
    print("zip    :", zip_path, f"({os.path.getsize(zip_path):,} bytes)")


def install(target):
    """作った内房線サンプルを、Uchibo20 があるフォルダに入れる(既にあるファイルは上書きしない)。"""
    src = os.path.join(OUT, "Sample_Uchibo")
    if not os.path.isdir(os.path.join(target, "Uchibo20")):
        die("Uchibo20 フォルダが無い: " + target)
    pairs = []
    for base, _, files in os.walk(src):
        for name in files:
            s = os.path.join(base, name)
            d = os.path.join(target, os.path.relpath(s, src))
            if os.path.exists(d):
                die("既にある(上書きしない): " + d)
            pairs.append((s, d))
    for s, d in pairs:
        copy(s, d)
    print(f"installed {len(pairs)} files into {target}")


def uninstall(target):
    """install で入れたファイル(今の Sample_Uchibo と同じ名前のもの)だけを消し、空になったフォルダも消す。"""
    src = os.path.join(OUT, "Sample_Uchibo")
    removed, dirs = 0, set()
    for base, _, files in os.walk(src):
        for name in files:
            d = os.path.join(target, os.path.relpath(os.path.join(base, name), src))
            if os.path.isfile(d):
                os.remove(d)
                removed += 1
            dirs.add(os.path.dirname(d))
    # 深いフォルダから、空なら消す(元からあるフォルダは中身が残っているので消えない)。
    for d in sorted(dirs, key=len, reverse=True):
        while d.startswith(target) and d != target and os.path.isdir(d) and not os.listdir(d):
            os.rmdir(d)
            d = os.path.dirname(d)
    print(f"removed {removed} files from {target}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--uchibo", metavar="DIR", default=os.environ.get("KTY_MARKER_UCHIBO", ""),
                    help="開発用の内房線フォルダ(Uchibo20 と試験用データがあるフォルダ)")
    ap.add_argument("--install", metavar="DIR")
    ap.add_argument("--uninstall", metavar="DIR", help="前に入れたサンプルを消してから作る")
    args = ap.parse_args()
    if not args.uchibo:
        die("開発用の内房線フォルダを --uchibo か環境変数 KTY_MARKER_UCHIBO で指定してください")
    U20 = os.path.join(args.uchibo, "Uchibo20")
    if not os.path.isdir(os.path.join(U20, "E217r_MarkerTest")):
        die("試験用データ(Uchibo20\\E217r_MarkerTest)が見つからない: " + U20)
    if args.uninstall:
        uninstall(args.uninstall)   # 前回作った Sample_Uchibo の一覧で消す(作り直す前に)
    build()
    if args.install:
        install(args.install)
