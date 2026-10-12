using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Application;

public enum RunPhase
{
    Preparing,
    Scanning,
    ReadingMetadata,
    Analyzing,
    Hashing,
    Transferring,
    Saving,
    Completed,
}

/// <summary>進捗。Total が 0 のときは件数不明（不確定表示）</summary>
public sealed record OrganizeProgress(RunPhase Phase, int Total, int Done, string CurrentFile, string Message);

public sealed record DuplicateGroup(string GroupId, IReadOnlyList<ImageAsset> Files);

public interface IOrganizeUseCase
{
    Task<OrganizeResult> ExecuteAsync(
        AppSettings settings,
        IProgress<OrganizeProgress>? progress = null,
        IProgress<LogEntry>? log = null,
        CancellationToken cancellationToken = default);
}

public sealed class OrganizeResult
{
    public string RunId { get; init; } = string.Empty;
    public OperationMode Mode { get; init; }
    public bool Cancelled { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? ExifToolVersion { get; init; }

    public int TotalScanned    { get; init; }
    /// <summary>コピー/移動した件数（DRY_RUN では処理予定の件数）</summary>
    public int TotalProcessed  { get; init; }
    /// <summary>出力先に同一ファイルがあってスキップした件数</summary>
    public int TotalSkipped    { get; init; }
    public int TotalDuplicates { get; init; }
    public int TotalFiltered   { get; init; }
    public int TotalErrors     { get; init; }

    public IReadOnlyList<ImageAsset> Assets { get; init; } = [];
    public IReadOnlyList<LogEntry> Log { get; init; } = [];
    public ScanStatistics Statistics { get; init; } = new();
    public IReadOnlyList<DuplicateGroup> DuplicateGroups { get; init; } = [];
}
