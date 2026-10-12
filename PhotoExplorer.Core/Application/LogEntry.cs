namespace PhotoExplorer.Application;

public enum LogLevel { Debug, Info, Success, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public static LogEntry Debug(string m) => new(DateTime.Now, LogLevel.Debug, m);
    public static LogEntry Info(string m)  => new(DateTime.Now, LogLevel.Info, m);
    public static LogEntry Ok(string m)    => new(DateTime.Now, LogLevel.Success, m);
    public static LogEntry Warn(string m)  => new(DateTime.Now, LogLevel.Warning, m);
    public static LogEntry Error(string m) => new(DateTime.Now, LogLevel.Error, m);

    public string LevelTag => Level switch
    {
        LogLevel.Debug   => "詳細",
        LogLevel.Info    => "情報",
        LogLevel.Success => "完了",
        LogLevel.Warning => "注意",
        LogLevel.Error   => "エラー",
        _ => "",
    };

    public override string ToString() => $"[{Time:HH:mm:ss}] [{LevelTag}] {Message}";
}
