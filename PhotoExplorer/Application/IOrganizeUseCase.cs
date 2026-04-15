using PhotoExplorer.Domain.Entities;

namespace PhotoExplorer.Application;

public record OrganizeProgress(int Total, int Processed, string CurrentFile, string Message);

/// <summary>写真整理のメインユースケース</summary>
public interface IOrganizeUseCase
{
    Task<OrganizeResult> ExecuteAsync(
        AppSettings settings,
        IProgress<OrganizeProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class OrganizeResult
{
    public int TotalScanned   { get; init; }
    public int TotalProcessed { get; init; }
    public int TotalDuplicates { get; init; }
    public int TotalErrors    { get; init; }
    public IReadOnlyList<string> Log { get; init; } = [];
    public ScanStatistics Statistics { get; init; } = new();
    /// <summary>重複グループ: グループID → ファイルパス一覧</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DuplicateGroups { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
}
