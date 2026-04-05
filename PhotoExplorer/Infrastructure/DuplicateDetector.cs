using System.IO;
using System.Security.Cryptography;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.Services;

namespace PhotoExplorer.Infrastructure;

/// <summary>
/// サイズ → 部分ハッシュ → フルハッシュ の3段階で重複検出する
/// RAW と JPEG は別ファイルとして扱う（拡張子が異なる）
/// </summary>
public sealed class DuplicateDetector : IDuplicateDetector
{
    /// <summary>部分ハッシュで読み込む先頭バイト数</summary>
    private const int PartialHashBytes = 64 * 1024; // 64 KB

    public async Task DetectDuplicatesAsync(
        IReadOnlyList<ImageAsset> assets,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Step 1: ファイルサイズでグルーピング（同じ拡張子グループ内で）
        progress?.Report("重複検出: サイズ比較中...");
        var sizeGroups = assets
            .GroupBy(a => (a.FileSize, a.Extension))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in sizeGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = group.ToList();

            // Step 2: 部分ハッシュを計算
            progress?.Report($"重複検出: 部分ハッシュ計算中... ({candidates.Count} ファイル)");
            foreach (var asset in candidates)
            {
                if (asset.PartialHash == null)
                    asset.PartialHash = await ComputePartialHashAsync(asset.SourcePath, cancellationToken);
            }

            var partialGroups = candidates
                .GroupBy(a => a.PartialHash)
                .Where(g => g.Count() > 1);

            // Step 3: フルハッシュで確認
            foreach (var pg in partialGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pCandidates = pg.ToList();
                progress?.Report($"重複検出: フルハッシュ計算中... ({pCandidates.Count} ファイル)");

                foreach (var asset in pCandidates)
                {
                    if (asset.FullHash == null)
                        asset.FullHash = await ComputeFullHashAsync(asset.SourcePath, cancellationToken);
                }

                var fullGroups = pCandidates
                    .GroupBy(a => a.FullHash)
                    .Where(g => g.Count() > 1);

                foreach (var fg in fullGroups)
                {
                    var duplicates = fg.OrderBy(a => a.SourcePath).ToList();
                    var groupId = Guid.NewGuid().ToString("N")[..8];

                    // 先頭1件を保持、残りを重複扱い
                    for (int i = 0; i < duplicates.Count; i++)
                    {
                        duplicates[i].DuplicateGroupId = groupId;
                        duplicates[i].IsKeptInDuplicateGroup = i == 0;
                        if (i > 0)
                            duplicates[i].Status = AssetStatus.Duplicate;
                    }
                }
            }
        }
    }

    private static async Task<string?> ComputePartialHashAsync(
        string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var buffer = new byte[PartialHashBytes];
            int read = await fs.ReadAsync(buffer.AsMemory(0, PartialHashBytes), cancellationToken);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(buffer, 0, read);
            return Convert.ToHexString(hash);
        }
        catch { return null; }
    }

    private static async Task<string?> ComputeFullHashAsync(
        string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            using var sha256 = SHA256.Create();
            var hash = await sha256.ComputeHashAsync(fs, cancellationToken);
            return Convert.ToHexString(hash);
        }
        catch { return null; }
    }
}
