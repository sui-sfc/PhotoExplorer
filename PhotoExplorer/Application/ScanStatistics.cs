namespace PhotoExplorer.Application;

/// <summary>スキャン・処理結果の統計情報</summary>
public sealed class ScanStatistics
{
    public int TotalFiles  { get; set; }
    public int JpegCount   { get; set; }
    public int RawCount    { get; set; }
    public int WithExif    { get; set; }
    public int WithCamera  { get; set; }
    public int WithGps     { get; set; }
    public int Filtered    { get; set; }   // 日付フィルタで除外された件数

    public DateTime? EarliestShot { get; set; }
    public DateTime? LatestShot   { get; set; }

    /// <summary>カメラ名 → 件数（件数降順）</summary>
    public IReadOnlyList<(string Camera, int Count)> CameraBreakdown { get; set; } = [];
    /// <summary>拡張子 → 件数（件数降順）</summary>
    public IReadOnlyList<(string Ext, int Count)> ExtBreakdown { get; set; } = [];
    /// <summary>年月 → 件数（昇順）</summary>
    public IReadOnlyList<(string YearMonth, int Count)> MonthlyBreakdown { get; set; } = [];
}
