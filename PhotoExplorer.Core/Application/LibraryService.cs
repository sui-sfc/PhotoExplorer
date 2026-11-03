using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Application;

/// <summary>ライブラリに並べる 1 枚</summary>
public sealed record LibraryPhoto(
    ImageAsset Asset,
    string CurrentPath,
    bool Exists,
    string Camera,
    string Lens,
    DateTime Date,
    bool HasExifDate,
    MediaKind Kind,
    int Rating,
    string? Location)
{
    public string FileName => Path.GetFileName(CurrentPath);
}

public enum LibrarySort { NewestFirst, OldestFirst, FileName }

/// <summary>ライブラリの絞り込み条件（null / 空は「すべて」）</summary>
public sealed class LibraryCriteria
{
    public string? Camera { get; init; }
    public string? Lens { get; init; }
    public MediaKind? Kind { get; init; }
    public int MinRating { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? Location { get; init; }
    public string? Search { get; init; }
    public bool IncludeMissing { get; init; }
    public bool IncludeDuplicates { get; init; }
    public LibrarySort Sort { get; init; } = LibrarySort.NewestFirst;
}

/// <summary>
/// 整理の記録（photo_explorer.db）から写真の一覧を作る。
/// 移動・コピー後は出力先を、まだ整理していない写真は元の場所を「今ある場所」とする。
/// </summary>
public sealed class LibraryService
{
    private readonly IAssetRepository _repository;
    private readonly INormalizer _normalizer;

    public LibraryService(IAssetRepository repository, INormalizer normalizer)
    {
        _repository = repository;
        _normalizer = normalizer;
    }

    public Task<IReadOnlyList<LibraryPhoto>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var assets = await _repository.LoadAllAsync(cancellationToken).ConfigureAwait(false);

            // ファイルがあるかどうかの確認は、NAS などでは 1 件ずつだと遅いので並列にまとめて行う
            var candidates = assets.SelectMany(x => new[] { x.SourcePath, x.OutputPath })
                                   .OfType<string>().Distinct(PathUtil.Comparer).ToList();
            var exists = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(PathUtil.Comparer);
            Parallel.ForEach(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                path => exists[path] = File.Exists(path));

            return Build(assets, _normalizer, path => exists.TryGetValue(path, out var e) ? e : File.Exists(path), cancellationToken);
        }, cancellationToken);

    internal static IReadOnlyList<LibraryPhoto> Build(
        IEnumerable<ImageAsset> assets,
        INormalizer normalizer,
        Func<string, bool> fileExists,
        CancellationToken cancellationToken = default)
    {
        var byPath = new Dictionary<string, LibraryPhoto>(PathUtil.Comparer);
        foreach (var a in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, exists) = ResolveCurrentPath(a, fileExists);
            var e = a.Exif;
            var photo = new LibraryPhoto(
                Asset: a,
                CurrentPath: path,
                Exists: exists,
                Camera: e?.Model == null ? "Unknown" : normalizer.NormalizeCamera(e.CameraName, e.CameraNameFull, e.Model),
                Lens: normalizer.NormalizeLens(e?.LensModel),
                Date: a.BestDate,
                HasExifDate: a.HasExifDate,
                Kind: a.Kind,
                Rating: Math.Clamp(e?.Rating ?? 0, 0, 5),
                Location: LocationOf(a));

            // 同じファイルが複数の記録に出てくる場合（コピー先を別の実行で読み込んだなど）は 1 枚にまとめる
            if (!byPath.TryGetValue(path, out var existing) || Preferred(photo, existing))
                byPath[path] = photo;
        }
        return byPath.Values.ToList();
    }

    /// <summary>出力先にあればそちら、なければ元の場所。どちらにもなければ記録上の最後の場所</summary>
    internal static (string Path, bool Exists) ResolveCurrentPath(ImageAsset a, Func<string, bool> fileExists)
    {
        bool moved = a.Status is AssetStatus.Processed or AssetStatus.Skipped
                     || (a.Status == AssetStatus.Duplicate && a.OutputPath != null);
        if (moved && a.OutputPath != null && fileExists(a.OutputPath)) return (a.OutputPath, true);
        if (fileExists(a.SourcePath)) return (a.SourcePath, true);
        if (a.OutputPath != null && fileExists(a.OutputPath)) return (a.OutputPath, true);
        return (moved && a.OutputPath != null ? a.OutputPath : a.SourcePath, false);
    }

    private static bool Preferred(LibraryPhoto candidate, LibraryPhoto existing)
    {
        if (candidate.HasExifDate != existing.HasExifDate) return candidate.HasExifDate;
        return (candidate.Asset.ProcessedAt ?? DateTime.MinValue) > (existing.Asset.ProcessedAt ?? DateTime.MinValue);
    }

    public static string? LocationOf(ImageAsset a)
    {
        var e = a.Exif;
        if (e == null) return null;
        var parts = new[] { e.GeolocationCountry, e.GeolocationCity }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    public static IReadOnlyList<LibraryPhoto> Filter(IEnumerable<LibraryPhoto> photos, LibraryCriteria c)
    {
        var from = c.From?.Date;
        var to = c.To?.Date.AddDays(1);
        var q = photos.Where(p =>
            (c.IncludeMissing || p.Exists)
            && (c.IncludeDuplicates || p.Asset.Status != AssetStatus.Duplicate)
            && (string.IsNullOrEmpty(c.Camera) || string.Equals(p.Camera, c.Camera, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(c.Lens) || string.Equals(p.Lens, c.Lens, StringComparison.OrdinalIgnoreCase))
            && (c.Kind == null || p.Kind == c.Kind)
            && p.Rating >= c.MinRating
            && (from == null || p.Date >= from)
            && (to == null || p.Date < to)
            && (string.IsNullOrEmpty(c.Location)
                || (p.Location != null && (p.Location == c.Location || p.Location.StartsWith(c.Location + " / ", StringComparison.Ordinal))))
            && (string.IsNullOrWhiteSpace(c.Search)
                || p.CurrentPath.Contains(c.Search.Trim(), StringComparison.OrdinalIgnoreCase)));

        q = c.Sort switch
        {
            LibrarySort.OldestFirst => q.OrderBy(p => p.Date).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase),
            LibrarySort.FileName    => q.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Date),
            _                       => q.OrderByDescending(p => p.Date).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase),
        };
        return q.ToList();
    }

    /// <summary>絞り込みの選択肢（値と件数）。件数の多い順</summary>
    public static IReadOnlyList<(string Value, int Count)> Facet(IEnumerable<LibraryPhoto> photos, Func<LibraryPhoto, string?> key) =>
        photos.Select(key)
              .Where(v => !string.IsNullOrWhiteSpace(v) && v != "Unknown")
              .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
              .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
              .Select(g => (g.Key, g.Count()))
              .ToList();
}
