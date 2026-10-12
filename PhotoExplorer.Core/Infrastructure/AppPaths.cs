namespace PhotoExplorer.Infrastructure;

/// <summary>
/// 設定・データベースの保存先。既定は %LocalAppData%\PhotoExplorer。
/// ・環境変数 PHOTOEXPLORER_DATA_DIR があればそこを使う
/// ・実行ファイルの隣に portable.txt があれば、隣の Data フォルダを使う（USB メモリなどで持ち歩く用）
/// </summary>
public static class AppPaths
{
    public const string DataDirEnvironmentVariable = "PHOTOEXPLORER_DATA_DIR";

    private static string? _dataDirectory;

    public static string DataDirectory => _dataDirectory ??= ResolveDataDirectory();

    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    private static string ResolveDataDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv);
        if (IsPortable) return Path.Combine(AppContext.BaseDirectory, "Data");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoExplorer");
    }

    public static string AssetDatabase   => Path.Combine(DataDirectory, "photo_explorer.db");
    public static string HistoryDatabase => Path.Combine(DataDirectory, "stats_history.db");
    public static string CrashLog        => Path.Combine(DataDirectory, "crash.log");

    public static void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}
