using System.Collections.ObjectModel;
using PhotoExplorer.Application;
using PhotoExplorer.Infrastructure;
using PhotoExplorer.Presentation.Converters;

namespace PhotoExplorer.Presentation.ViewModels;

public sealed class StatisticsViewModel : ViewModelBase
{
    private ScanStatistics _stats = new();

    public int TotalFiles     => _stats.TotalFiles;
    public string TotalSize   => FileSizeConverter.Format(_stats.TotalBytes);
    public int JpegCount      => _stats.JpegCount;
    public int RawCount       => _stats.RawCount;
    public int HeifVideoCount => _stats.HeifCount + _stats.VideoCount;
    public int WithGps        => _stats.WithGps;
    public string DateRange   => (_stats.EarliestShot, _stats.LatestShot) switch
    {
        ({ } e, { } l) when e.Date == l.Date => e.ToString("yyyy/MM/dd"),
        ({ } e, { } l) => $"{e:yyyy/MM/dd}\n〜 {l:yyyy/MM/dd}",
        _ => "—",
    };

    public IReadOnlyList<BarItem> CameraChart   { get; private set; } = [];
    public IReadOnlyList<BarItem> LensChart     { get; private set; } = [];
    public IReadOnlyList<BarItem> ExtChart      { get; private set; } = [];
    public IReadOnlyList<BarItem> MonthlyChart  { get; private set; } = [];
    public IReadOnlyList<BarItem> LocationChart { get; private set; } = [];
    public IReadOnlyList<BarItem> HourChart     { get; private set; } = [];
    public IReadOnlyList<BarItem> WeekdayChart  { get; private set; } = [];
    public IReadOnlyList<BarItem> CameraTotalsChart { get; private set; } = [];

    public bool HasData => _stats.TotalFiles > 0;
    public bool HasLocations => LocationChart.Count > 0;
    public bool HasCameraTotals => CameraTotalsChart.Count > 0;

    public ObservableCollection<RunHistoryViewModel> History { get; } = new();

    public void Update(ScanStatistics stats)
    {
        _stats = stats;
        CameraChart   = BarItem.Build(stats.CameraBreakdown,   "#2563EB");
        LensChart     = BarItem.Build(stats.LensBreakdown,     "#0891B2");
        ExtChart      = BarItem.Build(stats.ExtBreakdown,      "#16A34A");
        MonthlyChart  = BarItem.Build(stats.MonthlyBreakdown,  "#7C3AED");
        LocationChart = BarItem.Build(stats.LocationBreakdown, "#DB2777");
        HourChart     = BarItem.Build(stats.HourBreakdown,     "#EA580C");
        WeekdayChart  = BarItem.Build(stats.WeekdayBreakdown,  "#CA8A04");

        OnPropertiesChanged(nameof(TotalFiles), nameof(TotalSize), nameof(JpegCount), nameof(RawCount),
            nameof(HeifVideoCount), nameof(WithGps), nameof(DateRange), nameof(CameraChart), nameof(LensChart),
            nameof(ExtChart), nameof(MonthlyChart), nameof(LocationChart), nameof(HourChart), nameof(WeekdayChart),
            nameof(HasData), nameof(HasLocations));
    }

    public void LoadHistory(StatsHistoryRepository repo)
    {
        History.Clear();
        foreach (var r in repo.LoadHistory())
            History.Add(new RunHistoryViewModel(r));

        CameraTotalsChart = BarItem.Build(repo.LoadCameraTotals(), "#475569");
        OnPropertiesChanged(nameof(CameraTotalsChart), nameof(HasCameraTotals));
    }
}

public sealed class RunHistoryViewModel
{
    public RunHistoryViewModel(RunHistoryRecord rec) => Record = rec;

    public RunHistoryRecord Record { get; }

    public string RunAt     => Record.RunAt.ToString("yyyy/MM/dd HH:mm");
    public string Mode      => Record.Mode switch
    {
        "DryRun" => "DRY_RUN",
        "Copy"   => "COPY",
        "Move"   => "MOVE",
        "" => "—",
        var m => m,
    };
    public int TotalFiles   => Record.TotalFiles;
    public int Processed    => Record.Processed;
    public int Skipped      => Record.Skipped;
    public int Duplicates   => Record.Duplicates;
    public int Errors       => Record.Errors;
    public string Elapsed   => Record.ElapsedSeconds > 0 ? OrganizeUseCase.FormatElapsed(TimeSpan.FromSeconds(Record.ElapsedSeconds)) : "";
    public string Input     => Record.InputDirectory;
    public string State     => Record.UndoneAt != null ? $"取り消し済み（{Record.UndoneAt:MM/dd HH:mm}）"
                             : Record.Cancelled ? "中断"
                             : "完了";

    public bool CanUndo => Record.RunId != null && Record.UndoneAt == null && Record.OperationCount > 0;
}
