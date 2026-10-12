using PhotoExplorer.Domain.Entities;

namespace PhotoExplorer.Domain.Services;

/// <summary>重複ファイルを検出するサービス</summary>
public interface IDuplicateDetector
{
    /// <summary>
    /// 重複を検出し、DuplicateGroupId / IsKeptInDuplicateGroup / Status を設定する。
    /// </summary>
    Task DetectDuplicatesAsync(
        IReadOnlyList<ImageAsset> assets,
        int maxDegreeOfParallelism = 4,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default);
}
