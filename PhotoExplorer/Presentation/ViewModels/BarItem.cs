using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using BrushConverter = System.Windows.Media.BrushConverter;

namespace PhotoExplorer.Presentation.ViewModels;

/// <summary>棒グラフの1要素</summary>
public sealed class BarItem
{
    public string Label   { get; init; } = string.Empty;
    public int    Value   { get; init; }
    /// <summary>最大値に対する比率 (0.0〜1.0)</summary>
    public double Ratio   { get; init; }
    public Brush  Fill    { get; init; } = Brushes.SteelBlue;
    public string Tooltip { get; init; } = string.Empty;

    public static IReadOnlyList<BarItem> Build(
        IEnumerable<(string Label, int Count)> items,
        string hexColor = "#3498DB")
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hexColor)!;
        brush.Freeze(); // enable cross-thread reuse

        var list = items.ToList();
        int max = list.Count > 0 ? list.Max(x => x.Count) : 1;
        return list.Select(x => new BarItem
        {
            Label   = x.Label,
            Value   = x.Count,
            Ratio   = max > 0 ? (double)x.Count / max : 0,
            Fill    = brush,
            Tooltip = $"{x.Label}: {x.Count} 件",
        }).ToList();
    }
}
