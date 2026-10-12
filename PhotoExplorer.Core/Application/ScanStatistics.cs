using System.Globalization;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Application;

/// <summary>スキャン結果の統計</summary>
public sealed class ScanStatistics
{
    public int TotalFiles  { get; init; }
    public long TotalBytes { get; init; }
    public int JpegCount   { get; init; }
    public int RawCount    { get; init; }
    public int HeifCount   { get; init; }
    public int VideoCount  { get; init; }
    public int WithExif    { get; init; }
    public int WithCamera  { get; init; }
    public int WithGps     { get; init; }
    /// <summary>日付フィルタで除外された件数</summary>
    public int Filtered    { get; init; }

    public DateTime? EarliestShot { get; init; }
    public DateTime? LatestShot   { get; init; }

    public IReadOnlyList<(string Label, int Count)> CameraBreakdown   { get; init; } = [];
    public IReadOnlyList<(string Label, int Count)> LensBreakdown     { get; init; } = [];
    public IReadOnlyList<(string Label, int Count)> ExtBreakdown      { get; init; } = [];
    public IReadOnlyList<(string Label, int Count)> MonthlyBreakdown  { get; init; } = [];
    public IReadOnlyList<(string Label, int Count)> LocationBreakdown { get; init; } = [];
    /// <summary>撮影時刻（0〜23時）の分布。常に24件</summary>
    public IReadOnlyList<(string Label, int Count)> HourBreakdown     { get; init; } = [];
    /// <summary>曜日の分布（月〜日）。常に7件</summary>
    public IReadOnlyList<(string Label, int Count)> WeekdayBreakdown  { get; init; } = [];

    public static ScanStatistics Build(IReadOnlyCollection<ImageAsset> assets, int filteredOut, int topN = 15)
    {
        static List<(string, int)> Top(IEnumerable<string> keys, int n) => keys
            .GroupBy(k => k)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(n)
            .Select(g => (g.Key, g.Count()))
            .ToList();

        var withDate = assets.Where(a => a.HasExifDate).Select(a => a.Exif!.EffectiveDate!.Value).ToList();
        var dates = assets.Select(a => a.BestDate).Where(d => d != default).ToList();

        var hours = new int[24];
        var weekdays = new int[7];
        foreach (var d in withDate)
        {
            hours[d.Hour]++;
            weekdays[((int)d.DayOfWeek + 6) % 7]++;
        }
        string[] weekdayNames = ["月", "火", "水", "木", "金", "土", "日"];

        return new ScanStatistics
        {
            TotalFiles   = assets.Count,
            TotalBytes   = assets.Sum(a => a.FileSize),
            JpegCount    = assets.Count(a => a.Kind == MediaKind.Jpeg),
            RawCount     = assets.Count(a => a.Kind == MediaKind.Raw),
            HeifCount    = assets.Count(a => a.Kind == MediaKind.Heif),
            VideoCount   = assets.Count(a => a.Kind == MediaKind.Video),
            WithExif     = assets.Count(a => a.Exif != null),
            WithCamera   = assets.Count(a => !string.IsNullOrWhiteSpace(a.Exif?.Model)),
            WithGps      = assets.Count(a => a.Exif?.HasGps == true),
            Filtered     = filteredOut,
            EarliestShot = dates.Count > 0 ? dates.Min() : null,
            LatestShot   = dates.Count > 0 ? dates.Max() : null,

            CameraBreakdown = Top(assets
                .Where(a => !string.IsNullOrWhiteSpace(a.NormalizedCamera) && a.NormalizedCamera != "Unknown")
                .Select(a => a.NormalizedCamera!), topN),
            LensBreakdown = Top(assets
                .Where(a => !string.IsNullOrWhiteSpace(a.NormalizedLens) && a.NormalizedLens != "Unknown")
                .Select(a => a.NormalizedLens!), topN),
            ExtBreakdown = Top(assets.Select(a => a.Extension.TrimStart('.')), topN),
            MonthlyBreakdown = dates
                .GroupBy(d => d.ToString("yyyy/MM", CultureInfo.InvariantCulture))
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count()))
                .ToList(),
            LocationBreakdown = Top(assets
                .Where(a => a.Exif?.GeolocationCity != null || a.Exif?.GeolocationCountry != null)
                .Select(a => string.Join(" / ", new[] { a.Exif!.GeolocationCountry, a.Exif.GeolocationCity }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))), topN),
            HourBreakdown = Enumerable.Range(0, 24).Select(h => ($"{h:D2}時", hours[h])).ToList(),
            WeekdayBreakdown = Enumerable.Range(0, 7).Select(i => (weekdayNames[i], weekdays[i])).ToList(),
        };
    }
}
