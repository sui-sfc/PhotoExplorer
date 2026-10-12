using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;

namespace PhotoExplorer.Presentation.ViewModels;

/// <summary>棒グラフの1本</summary>
public sealed class BarItem
{
    public string Label   { get; init; } = string.Empty;
    public int    Value   { get; init; }
    /// <summary>最大値に対する比率（0.0〜1.0）。バーの長さに使う</summary>
    public double Ratio   { get; init; }
    public Brush? Fill    { get; init; }
    public string Tooltip { get; init; } = string.Empty;

    public static IReadOnlyList<BarItem> Build(IEnumerable<(string Label, int Count)> items, string hexColor)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hexColor)!;
        brush.Freeze();

        var list = items.ToList();
        int max = list.Count > 0 ? Math.Max(1, list.Max(x => x.Count)) : 1;
        int total = Math.Max(1, list.Sum(x => x.Count));
        return list.Select(x => new BarItem
        {
            Label   = x.Label,
            Value   = x.Count,
            Ratio   = (double)x.Count / max,
            Fill    = brush,
            Tooltip = $"{x.Label}: {x.Count:N0} 件（{100.0 * x.Count / total:F1}%）",
        }).ToList();
    }
}
