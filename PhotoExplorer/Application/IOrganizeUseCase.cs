using PhotoExplorer.Domain.Entities;

namespace PhotoExplorer.Application;

public record OrganizeProgress(int Total, int Processed, string CurrentFile, string Message);

/// <summary>写真整理のメインユースケース</summary>
public interface IOrganizeUseCase
{
    /// <summary>
    /// 写真整理処理を実行する
    /// Scan → EXIF → Normalize → Resolve → Duplicate → Execute → Log
    /// </summary>
    Task<OrganizeResult> ExecuteAsync(
        AppSettings settings,
        IProgress<OrganizeProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class OrganizeResult
{
    public int TotalScanned { get; init; }
    public int TotalProcessed { get; init; }
    public int TotalDuplicates { get; init; }
    public int TotalErrors { get; init; }
    public IReadOnlyList<string> Log { get; init; } = [];
}
