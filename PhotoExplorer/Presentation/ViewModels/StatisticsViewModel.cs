using System.Collections.ObjectModel;
using PhotoExplorer.Application;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Presentation.ViewModels;

public sealed class StatisticsViewModel : ViewModelBase
{
    // ----- 集計数値 -----
    public int TotalFiles  { get; private set; }
    public int JpegCount   { get; private set; }
    public int RawCount    { get; private set; }
    public int WithExif    { get; private set; }
    public int WithGps     { get; private set; }
    public string DateRange { get; private set; } = "—";

    // ----- グラフ -----
    public IReadOnlyList<BarItem> CameraChart  { get; private set; } = [];
    public IReadOnlyList<BarItem> ExtChart     { get; private set; } = [];
    public IReadOnlyList<BarItem> MonthlyChart { get; private set; } = [];

    // ----- 実行履歴 -----
    public ObservableCollection<RunHistoryViewModel> History { get; } = new();

    public bool HasData => TotalFiles > 0;

    public void Update(ScanStatistics stats)
    {
        TotalFiles = stats.TotalFiles;
        JpegCount  = stats.JpegCount;
        RawCount   = stats.RawCount;
        WithExif   = stats.WithExif;
        WithGps    = stats.WithGps;
        DateRange  = (stats.EarliestShot, stats.LatestShot) switch
        {
            ({ } e, { } l) when e == l => e.ToString("yyyy/MM/dd"),
            ({ } e, { } l)             => $"{e:yyyy/MM/dd} 〜 {l:yyyy/MM/dd}",
            _                          => "—",
        };

        CameraChart  = BarItem.Build(stats.CameraBreakdown,  "#2980B9");
        ExtChart     = BarItem.Build(stats.ExtBreakdown,     "#27AE60");
        MonthlyChart = BarItem.Build(stats.MonthlyBreakdown, "#8E44AD");

        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(JpegCount));
        OnPropertyChanged(nameof(RawCount));
        OnPropertyChanged(nameof(WithExif));
        OnPropertyChanged(nameof(WithGps));
        OnPropertyChanged(nameof(DateRange));
        OnPropertyChanged(nameof(CameraChart));
        OnPropertyChanged(nameof(ExtChart));
        OnPropertyChanged(nameof(MonthlyChart));
        OnPropertyChanged(nameof(HasData));
    }

    public void LoadHistory(StatsHistoryRepository repo)
    {
        History.Clear();
        foreach (var r in repo.LoadHistory())
            History.Add(new RunHistoryViewModel(r));
    }
}

public sealed class RunHistoryViewModel
{
    private readonly RunHistoryRecord _rec;
    public RunHistoryViewModel(RunHistoryRecord rec) => _rec = rec;

    public string RunAt    => _rec.RunAt.ToString("yyyy/MM/dd HH:mm");
    public int  TotalFiles => _rec.TotalFiles;
    public int  JpegCount  => _rec.JpegCount;
    public int  RawCount   => _rec.RawCount;
    public int  WithGps    => _rec.WithGps;
    public int  Processed  => _rec.Processed;
    public int  Duplicates => _rec.Duplicates;
    public int  Errors     => _rec.Errors;
    public string Input    => _rec.InputDirectory;
}
