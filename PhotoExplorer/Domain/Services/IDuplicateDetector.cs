using PhotoExplorer.Domain.Entities;

namespace PhotoExplorer.Domain.Services;

/// <summary>重複ファイルを検出するサービス</summary>
public interface IDuplicateDetector
{
    /// <summary>
    /// アセット一覧から重複を検出し、グループIDを設定する
    /// </summary>
    /// <param name="assets">対象アセット一覧</param>
    /// <param name="progress">進捗レポーター</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    Task DetectDuplicatesAsync(
        IReadOnlyList<ImageAsset> assets,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
