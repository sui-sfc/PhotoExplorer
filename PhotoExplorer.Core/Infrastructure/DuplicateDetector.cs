using System.Text.RegularExpressions;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.Services;

namespace PhotoExplorer.Infrastructure;

/// <summary>
/// サイズ → 先頭64KBのハッシュ → 全体のSHA-256 の3段階で重複を検出する。
/// ハッシュが計算できなかったファイルは重複とみなさない。
/// </summary>
public sealed partial class DuplicateDetector : IDuplicateDetector
{
    // "IMG_0001 (1).jpg" "IMG_0001 - コピー.jpg" "IMG_0001_copy.jpg" など、コピーらしい名前
    [GeneratedRegex(@"(\(\d+\)|コピー|copy|複製|_\d{3}$)", RegexOptions.IgnoreCase)]
    private static partial Regex CopyLikeNameRegex();

    public async Task DetectDuplicatesAsync(
        IReadOnlyList<ImageAsset> assets,
        int maxDegreeOfParallelism = 4,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism),
            CancellationToken = cancellationToken,
        };

        // 1. 同じサイズのファイルだけが候補
        var sizeCandidates = assets
            .Where(a => a.FileSize > 0)
            .GroupBy(a => a.FileSize)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        // 2. 先頭64KBのハッシュ
        int done = 0;
        int total = sizeCandidates.Count;
        progress?.Report((0, total));
        await Parallel.ForEachAsync(sizeCandidates, parallel, async (asset, ct) =>
        {
            if (asset.PartialHash == null)
            {
                try { asset.PartialHash = await FileHasher.ComputePartialAsync(asset.SourcePath, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { asset.PartialHash = null; }
            }
            progress?.Report((Interlocked.Increment(ref done), total));
        }).ConfigureAwait(false);

        var partialCandidates = sizeCandidates
            .Where(a => a.PartialHash != null)
            .GroupBy(a => (a.FileSize, a.PartialHash))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        // 3. 全体のハッシュ（64KB 以下のファイルは先頭ハッシュ = 全体ハッシュ）
        done = 0;
        total = partialCandidates.Count;
        progress?.Report((0, total));
        await Parallel.ForEachAsync(partialCandidates, parallel, async (asset, ct) =>
        {
            if (asset.FullHash == null)
            {
                if (asset.FileSize <= FileHasher.PartialHashBytes)
                    asset.FullHash = asset.PartialHash;
                else
                {
                    try { asset.FullHash = await FileHasher.ComputeFullAsync(asset.SourcePath, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { asset.FullHash = null; }
                }
            }
            progress?.Report((Interlocked.Increment(ref done), total));
        }).ConfigureAwait(false);

        var groups = partialCandidates
            .Where(a => a.FullHash != null)
            .GroupBy(a => (a.FileSize, a.FullHash))
            .Where(g => g.Count() > 1);

        foreach (var g in groups)
        {
            var ordered = g.OrderBy(KeepPriority)
                           .ThenBy(a => a.SourcePath.Length)
                           .ThenBy(a => a.SourcePath, StringComparer.OrdinalIgnoreCase)
                           .ToList();
            var groupId = ordered[0].FullHash![..12].ToLowerInvariant();

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].DuplicateGroupId = groupId;
                ordered[i].IsKeptInDuplicateGroup = i == 0;
                if (i > 0) ordered[i].Status = AssetStatus.Duplicate;
            }
        }
    }

    /// <summary>残すファイルの優先度（小さいほど優先）</summary>
    private static int KeepPriority(ImageAsset a)
    {
        int p = 0;
        if (CopyLikeNameRegex().IsMatch(Path.GetFileNameWithoutExtension(a.SourcePath))) p += 2;
        if (!a.HasExifDate) p += 1;
        return p;
    }
}
