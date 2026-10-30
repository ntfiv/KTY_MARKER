using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace KTY_MARKER;

/// <summary>
/// ベストエフォートのファイルロガー。BVE5はプラグインにコンソールもエラー報告手段も
/// 与えないため、クラッシュやサイレントな不具合の後に何が起きたか確認する唯一の手段。
/// 例外は投げない。BveTs.exeと同じフォルダではなく、現在のユーザーの一時フォルダに
/// 書き出す: BVE5/6はProgram Files配下にインストールされることが多く、標準ユーザーには
/// 書き込み権限がない - 実機で確認済み(BveTs5/6がProgram Files配下にあり、書き込みが
/// 拒否され、UACの仮想化によるフォールバックも発生しなかった)。そのため作業ディレクトリ
/// 相対のパスではログが何も出力されず、「プラグインが一度も動いていない」状態と
/// 見分けがつかなくなる。一時フォルダはインストール場所に関わらず、常に現在のユーザーが
/// 書き込み可能。ログファイル名は固定文字列ではなくアセンブリ名から生成する
/// (KTY_MARKER.log / KTY_MARKER64.log) - 複数プラグインをATS Plugin Multiplexer経由で
/// 併用する場合、固定文字列だと他プラグインとログが衝突してしまうため。
///
/// ログの出力は`KTY_MARKER.ini`の`[Log] Enabled`で止められる(オフのときはファイルを作らず、例外の
/// ログも含めて何も書かない)。配布するサンプルiniはオフ。項目が無い・iniが無いときはオン(iniの置き忘れや
/// 名前の間違いなど、設定の不備をログで見つけられるように)。KTY_TUNNELと同じ仕組み。
/// `[Log] Directory = plugin`にすると、一時フォルダではなくDLLと同じフォルダに書き出す(書き込めなければ一時フォルダ)。
/// 読み込みは最初のログ出力時と、`Load`の時(`Reload`)に行う。MarkerConfigより前に出るログにも
/// 効かせるため、このクラス自身がiniの`[Log]`だけを読む。
/// </summary>
internal static class AtsDebug
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPrivateProfileString(string section, string key, string defaultValue, StringBuilder result, int size, string filePath);

    private static readonly string LogFileName = $"{Assembly.GetExecutingAssembly().GetName().Name}.log";
    private static readonly string TempLogPath = Path.Combine(Path.GetTempPath(), LogFileName);

    private static readonly object EnabledLock = new();
    private static bool _enabledLoaded;
    private static bool _enabled = true;

    // 書き出し先。既定は一時フォルダ。iniの`[Log] Directory = plugin`なら、DLLと同じフォルダ(書き込めなければ一時フォルダ)。
    private static string _logPath = TempLogPath;

    /// <summary>ログを出力するか。初回に`[Log] Enabled`を読む。</summary>
    public static bool Enabled
    {
        get
        {
            lock (EnabledLock)
            {
                if (!_enabledLoaded)
                {
                    LoadSettings();
                }

                return _enabled;
            }
        }
    }

    /// <summary>今のログの書き出し先(ログ用の表示に使う)。</summary>
    public static string LogPath
    {
        get
        {
            lock (EnabledLock)
            {
                return _logPath;
            }
        }
    }

    /// <summary>iniの`[Log]`を読み直す(`Load`の時に呼ぶ。iniを編集すれば次の読み込みから反映される)。</summary>
    public static void Reload()
    {
        lock (EnabledLock)
        {
            LoadSettings();
        }
    }

    // [Log] Enabled: iniが無い・項目が無い・読めないときはtrue。false/0/no/offのときだけfalse。
    // [Log] Directory: plugin ならDLLと同じフォルダ、それ以外(temp・省略)は一時フォルダ。技術テスト版の配布物では、
    // 利用者がログを見つけて送りやすいよう plugin にしている(2026-10-29、ユーザー指示。ふだんの既定は一時フォルダのまま)。
    private static void LoadSettings()
    {
        _enabledLoaded = true;
        _enabled = true;
        _logPath = TempLogPath;
        try
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            var path = Path.Combine(dir, MarkerConfig.FileName);
            if (!File.Exists(path))
            {
                return;
            }

            switch (ReadLogValue(path, "Enabled"))
            {
                case "false": case "0": case "no": case "off":
                    _enabled = false;
                    break;
            }

            if (ReadLogValue(path, "Directory") == "plugin" && dir.Length > 0)
            {
                _logPath = Path.Combine(dir, LogFileName);
            }
        }
        catch
        {
            // 読めないときは既定(出力する・一時フォルダ)のまま。
        }
    }

    private static string ReadLogValue(string iniPath, string key)
    {
        var buffer = new StringBuilder(256);
        GetPrivateProfileString("Log", key, "", buffer, buffer.Capacity, iniPath);
        return MarkerConfig.StripInlineComment(buffer.ToString()).ToLowerInvariant();
    }

    // ログが無制限に大きくならないよう、一定の回数ごとにファイルサイズを確認し、上限を超えて
    // いたら古いログを「.old」に退避して新しく書き始める(退避は1世代だけ)。配布後に長時間
    // 使われても、一時フォルダを圧迫しないため。
    private const long MaxLogBytes = 4 * 1024 * 1024;
    private const int SizeCheckInterval = 100;
    private static int _writesSinceSizeCheck;

    // KTY_MARKERは音声取り込みスレッドとBVEのスレッドの両方からログを書くため、同時に
    // ファイルへ追記して片方が失われないようロックする。
    private static readonly object WriteLock = new();

    public static void Log(string message)
    {
        try
        {
            if (!Enabled)
            {
                return;
            }

            lock (WriteLock)
            {
                var logPath = LogPath;
                if (++_writesSinceSizeCheck >= SizeCheckInterval)
                {
                    _writesSinceSizeCheck = 0;
                    RotateIfTooLarge(logPath);
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                try
                {
                    File.AppendAllText(logPath, line);
                }
                catch when (logPath != TempLogPath)
                {
                    // DLLのフォルダに書き込めない(Program Files配下・読み取り専用など)。一時フォルダに切り替えて書き直す。
                    lock (EnabledLock)
                    {
                        _logPath = TempLogPath;
                    }

                    File.AppendAllText(TempLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] could not write the log to {logPath} - writing to {TempLogPath} instead{Environment.NewLine}");
                    File.AppendAllText(TempLogPath, line);
                }
            }
        }
        catch
        {
            // ログ出力自体がホストプロセスのクラッシュ原因になってはならない。
        }
    }

    private static void RotateIfTooLarge(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length <= MaxLogBytes)
            {
                return;
            }

            var backup = logPath + ".old";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(logPath, backup);
        }
        catch
        {
            // ログの整理に失敗しても、ホストプロセスには影響させない。
        }
    }

    public static void LogException(string context, Exception ex) => Log($"[ERROR] {context}: {ex}");
}
