using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Shell;
using System.Windows.Threading;
using PhotoExplorer.Application;
using PhotoExplorer.Domain;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Domain.ValueObjects;
using PhotoExplorer.Infrastructure;
using PhotoExplorer.Presentation.Services;

namespace PhotoExplorer.Presentation.ViewModels;

public enum LogFilter { All, HideDetails, WarningsAndErrors, ErrorsOnly }

public sealed class MainViewModel : ViewModelBase
{
    private readonly IOrganizeUseCase _useCase;
    private readonly UndoService _undo;
    private readonly StatsHistoryRepository _history;
    private readonly IPathResolver _pathResolver;
    private readonly INormalizer _normalizer;
    private readonly MappingConfigProvider _mappings;
    private readonly UserPreferences _prefs;

    private readonly ConcurrentQueue<LogEntry> _pendingLog = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _runClock = new();
    private CancellationTokenSource? _cts;
    private RunPhase _phase;
    private DateTime _phaseStartedAt;
    private int _phaseStartDone;
    private OrganizeResult? _lastResult;

    public MainViewModel(
        IOrganizeUseCase useCase,
        UndoService undo,
        StatsHistoryRepository history,
        IPathResolver pathResolver,
        INormalizer normalizer,
        MappingConfigProvider mappings)
    {
        _useCase = useCase;
        _undo = undo;
        _history = history;
        _pathResolver = pathResolver;
        _normalizer = normalizer;
        _mappings = mappings;
        _prefs = UserPreferences.Load();

        LogView = CollectionViewSource.GetDefaultView(LogEntries);
        LogView.Filter = o => o is LogEntry e && LogPasses(e);
        AssetsView = CollectionViewSource.GetDefaultView(Assets);
        AssetsView.Filter = o => o is ImageAssetViewModel a && AssetPasses(a);

        StartCommand           = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        StopCommand            = new RelayCommand(Stop, () => IsRunning && !_stopRequested);
        BrowseInputCommand     = new RelayCommand(() => Browse("整理したい写真のフォルダ", v => InputDirectory = v, InputDirectory), () => !IsRunning);
        BrowseOutputCommand    = new RelayCommand(() => Browse("整理後の保存先フォルダ", v => OutputDirectory = v, OutputDirectory), () => !IsRunning);
        BrowseTrashCommand     = new RelayCommand(() => Browse("重複ファイルの退避先フォルダ", v => TrashDirectory = v, TrashDirectory), () => !IsRunning);
        BrowseExifToolCommand  = new RelayCommand(BrowseExifTool, () => !IsRunning);
        OpenOutputCommand      = new RelayCommand(() => Shell.OpenFolder(OutputDirectory), () => Directory.Exists(OutputDirectory));
        OpenInputCommand       = new RelayCommand(() => Shell.OpenFolder(InputDirectory), () => Directory.Exists(InputDirectory));
        ClearLogCommand        = new RelayCommand(() => LogEntries.Clear(), () => LogEntries.Count > 0 && !IsRunning);
        SaveLogCommand         = new RelayCommand(SaveLog, () => LogEntries.Count > 0);
        ExportCsvCommand       = new RelayCommand(ExportCsv, () => _lastResult?.Assets.Count > 0);
        OpenMappingsCommand    = new RelayCommand(OpenMappings);
        OpenDataFolderCommand  = new RelayCommand(() => Shell.OpenFolder(AppPaths.DataDirectory));
        ClearFilterCommand     = new RelayCommand(() => { FilterStartDate = null; FilterEndDate = null; });
        UndoRunCommand         = new AsyncRelayCommand(p => UndoAsync(p as RunHistoryViewModel), p => !IsRunning && p is RunHistoryViewModel { CanUndo: true });
        UndoLastRunCommand     = new AsyncRelayCommand(() => UndoAsync(LastUndoableRun), () => !IsRunning && LastUndoableRun != null);
        ClearHistoryCommand    = new RelayCommand(ClearHistory, () => !IsRunning && Statistics.History.Count > 0);
        ShowInExplorerCommand  = new RelayCommand(p => { if (PathOf(p) is { } path) Shell.ShowInExplorer(path); });
        OpenFileCommand        = new RelayCommand(p => { if (PathOf(p) is { } path) Shell.OpenWithDefaultApp(path); });
        ShowOutputInExplorerCommand = new RelayCommand(p => { if (p is ImageAssetViewModel { OutputPath: { } o }) Shell.ShowInExplorer(o); });
        RecycleDuplicateCommand     = new RelayCommand(p => RecycleDuplicates(p is DuplicateFileViewModel f ? [f] : []), p => !IsRunning && p is DuplicateFileViewModel { CanRecycle: true });
        RecycleAllDuplicatesCommand = new RelayCommand(() => RecycleDuplicates(DuplicateGroups.SelectMany(g => g.Files).Where(f => f.CanRecycle).ToList()),
                                                       () => !IsRunning && DuplicateGroups.Any(g => g.Files.Any(f => f.CanRecycle)));
        InsertVariableCommand  = new RelayCommand(p => { if (p is TemplateVariable v) FolderTemplate = AppendToken(FolderTemplate, v.Token); });
        CheckExifToolCommand   = new RelayCommand(() => _ = CheckExifToolAsync());

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _uiTimer.Tick += (_, _) => { FlushLog(); UpdateClock(); };

        Statistics.LoadHistory(_history);
        UpdateTemplatePreview();
        _ = CheckExifToolAsync();
    }

    // ===================== 設定 =====================

    public string InputDirectory
    {
        get => _prefs.InputDirectory;
        set { if (_prefs.InputDirectory != value) { _prefs.InputDirectory = value.Trim(); Saved(); } }
    }

    public string OutputDirectory
    {
        get => _prefs.OutputDirectory;
        set { if (_prefs.OutputDirectory != value) { _prefs.OutputDirectory = value.Trim(); Saved(); UpdateTemplatePreview(); } }
    }

    public string TrashDirectory
    {
        get => _prefs.TrashDirectory;
        set { if (_prefs.TrashDirectory != value) { _prefs.TrashDirectory = value.Trim(); Saved(); } }
    }

    public string FolderTemplate
    {
        get => _prefs.FolderTemplate;
        set { if (_prefs.FolderTemplate != value) { _prefs.FolderTemplate = value; Saved(); UpdateTemplatePreview(); } }
    }

    public string FileNameTemplate
    {
        get => _prefs.FileNameTemplate;
        set { if (_prefs.FileNameTemplate != value) { _prefs.FileNameTemplate = value; Saved(); UpdateTemplatePreview(); } }
    }

    public OperationMode OperationMode
    {
        get => _prefs.Mode;
        set { if (_prefs.Mode != value) { _prefs.Mode = value; Saved(); OnPropertyChanged(nameof(StartButtonText)); } }
    }

    public IReadOnlyList<OperationMode> OperationModes { get; } = Enum.GetValues<OperationMode>();

    public int MaxParallelism
    {
        get => _prefs.MaxParallelism;
        set { var v = Math.Clamp(value, 1, 16); if (_prefs.MaxParallelism != v) { _prefs.MaxParallelism = v; Saved(); } }
    }

    public DateTime? FilterStartDate
    {
        get => _prefs.FilterStartDate;
        set { if (_prefs.FilterStartDate != value) { _prefs.FilterStartDate = value; Saved(); OnPropertyChanged(nameof(HasDateFilter)); } }
    }

    public DateTime? FilterEndDate
    {
        get => _prefs.FilterEndDate;
        set { if (_prefs.FilterEndDate != value) { _prefs.FilterEndDate = value; Saved(); OnPropertyChanged(nameof(HasDateFilter)); } }
    }

    public bool HasDateFilter => FilterStartDate.HasValue || FilterEndDate.HasValue;

    public string ExifToolPath
    {
        get => _prefs.ExifToolPath;
        set { if (_prefs.ExifToolPath != value) { _prefs.ExifToolPath = value.Trim(); Saved(); _ = CheckExifToolAsync(); } }
    }

    public bool IncludeVideos    { get => _prefs.IncludeVideos;    set { if (_prefs.IncludeVideos != value)    { _prefs.IncludeVideos = value;    Saved(); } } }
    public bool IncludeSidecars  { get => _prefs.IncludeSidecars;  set { if (_prefs.IncludeSidecars != value)  { _prefs.IncludeSidecars = value;  Saved(); } } }
    public bool VerifyCopies     { get => _prefs.VerifyCopies;     set { if (_prefs.VerifyCopies != value)     { _prefs.VerifyCopies = value;     Saved(); } } }
    public bool DetectDuplicates { get => _prefs.DetectDuplicates; set { if (_prefs.DetectDuplicates != value) { _prefs.DetectDuplicates = value; Saved(); } } }
    public bool UseMetadataCache { get => _prefs.UseMetadataCache; set { if (_prefs.UseMetadataCache != value) { _prefs.UseMetadataCache = value; Saved(); } } }
    public bool VerboseLog       { get => _prefs.VerboseLog;       set { if (_prefs.VerboseLog != value)       { _prefs.VerboseLog = value;       Saved(); } } }

    public bool UseNoDateFolder
    {
        get => _prefs.UseNoDateFolder;
        set { if (_prefs.UseNoDateFolder != value) { _prefs.UseNoDateFolder = value; Saved(); UpdateTemplatePreview(); } }
    }

    public string NoDateFolderName
    {
        get => _prefs.NoDateFolderName;
        set { if (_prefs.NoDateFolderName != value) { _prefs.NoDateFolderName = value; Saved(); UpdateTemplatePreview(); } }
    }

    public IReadOnlyList<string> FolderPresets => TemplateVariables.FolderPresets;
    public IReadOnlyList<string> FileNamePresets => TemplateVariables.FileNamePresets;
    public IReadOnlyList<TemplateVariable> Variables => TemplateVariables.All;
    public string MappingsFilePath => MappingConfig.DefaultPath;
    public string DataDirectory => AppPaths.DataDirectory;
    public string AppVersion => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";

    /// <summary>ウィンドウ位置などの保存用</summary>
    public UserPreferences Preferences => _prefs;

    private void Saved([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        _prefs.Save();
    }

    public void SavePreferences() => _prefs.Save();

    // ===================== テンプレートのプレビュー =====================

    private string _templatePreview = "";
    private string _templateProblem = "";

    public string TemplatePreview { get => _templatePreview; private set => SetField(ref _templatePreview, value); }
    public string TemplateProblem { get => _templateProblem; private set => SetField(ref _templateProblem, value); }

    private static readonly ImageAsset PreviewAsset = new()
    {
        SourcePath = Path.Combine("C:", "DCIM", "IMG_0001.CR3"),
        FileSize = 25_000_000,
        Exif = new ExifData
        {
            DateTimeOriginal = new DateTime(2024, 7, 15, 9, 5, 42),
            Make = "Canon", Model = "Canon EOS R5", LensModel = "RF24-105mm F4 L IS USM",
            GeolocationCity = "Shibuya", GeolocationRegion = "Tokyo", GeolocationCountry = "Japan",
            GeolocationCountryCode = "JP", Rating = 4,
        },
    };

    private void UpdateTemplatePreview()
    {
        var unknown = _pathResolver.FindUnknownVariables(FolderTemplate ?? "")
            .Concat(_pathResolver.FindUnknownVariables(FileNameTemplate ?? "")).Distinct().ToList();
        TemplateProblem = unknown.Count > 0
            ? $"使えない変数があります: {string.Join(" ", unknown)}（下の「ヘルプ」タブに一覧があります）"
            : string.IsNullOrWhiteSpace(FolderTemplate) ? "フォルダ構成を入力してください" : "";

        try
        {
            PreviewAsset.NormalizedCamera = _normalizer.NormalizeCamera(PreviewAsset.Exif!.CameraName, PreviewAsset.Exif.CameraNameFull);
            PreviewAsset.NormalizedLens = _normalizer.NormalizeLens(PreviewAsset.Exif.LensModel);
            const string root = "■";
            var path = _pathResolver.Resolve(PreviewAsset, new PathOptions(root, FolderTemplate ?? "", FileNameTemplate ?? ""));
            var relative = path.StartsWith(root) ? path[(root.Length + 1)..] : path;
            var output = string.IsNullOrWhiteSpace(OutputDirectory) ? "<出力フォルダ>" : OutputDirectory.TrimEnd('\\', '/');
            TemplatePreview = $"例: {output}{Path.DirectorySeparatorChar}{relative}"
                + (UseNoDateFolder && !string.IsNullOrWhiteSpace(NoDateFolderName)
                    ? $"     （撮影日のないファイル → {NoDateFolderName}{Path.DirectorySeparatorChar}）" : "");
        }
        catch (Exception ex)
        {
            TemplatePreview = $"プレビューを作成できません: {ex.Message}";
        }
    }

    private static string AppendToken(string template, string token)
    {
        if (string.IsNullOrEmpty(template)) return token;
        return template.EndsWith('/') || template.EndsWith('\\') ? template + token : template + "/" + token;
    }

    // ===================== exiftool の状態 =====================

    private string _exifToolStatus = "exiftool を確認しています...";
    private bool _isExifToolOk;

    public string ExifToolStatus { get => _exifToolStatus; private set => SetField(ref _exifToolStatus, value); }
    public bool IsExifToolOk { get => _isExifToolOk; private set => SetField(ref _isExifToolOk, value); }

    private async Task CheckExifToolAsync()
    {
        var path = ExifToolLocator.Resolve(ExifToolPath);
        ExifToolStatus = "exiftool を確認しています...";
        var version = await Task.Run(() => new ExifToolMetadataExtractor(path).GetVersion());
        IsExifToolOk = version != null;
        ExifToolStatus = version != null
            ? $"exiftool {version} を使用（{path}）"
            : $"exiftool が見つかりません（{path}）。撮影日・カメラ名が読めないため、ファイルの日時で整理します。";
    }

    // ===================== 実行状態 =====================

    private bool _isRunning;
    private bool _stopRequested;
    private int _progressTotal;
    private int _progressValue;
    private bool _isIndeterminate;
    private string _progressMessage = "準備ができたら「実行」を押してください。まずは DRY_RUN で結果を確認するのがおすすめです。";
    private string _currentFile = "";
    private string _elapsedText = "";
    private string _etaText = "";
    private string _statusMessage = "";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertiesChanged(nameof(IsIdle), nameof(TaskbarState));
        }
    }

    public bool IsIdle => !_isRunning;
    public string StartButtonText => OperationMode switch
    {
        OperationMode.Copy => "▶ コピーを実行",
        OperationMode.Move => "▶ 移動を実行",
        _ => "▶ DRY_RUN を実行",
    };

    public int ProgressTotal { get => _progressTotal; private set => SetField(ref _progressTotal, value); }
    public int ProgressValue { get => _progressValue; private set => SetField(ref _progressValue, value); }
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set { if (SetField(ref _isIndeterminate, value)) OnPropertyChanged(nameof(TaskbarState)); }
    }
    public double ProgressFraction => ProgressTotal > 0 ? Math.Clamp((double)ProgressValue / ProgressTotal, 0, 1) : 0;
    public string ProgressPercentText => IsRunning && !IsIndeterminate && ProgressTotal > 0 ? $"{ProgressFraction * 100:F0}%" : "";
    public TaskbarItemProgressState TaskbarState =>
        !IsRunning ? TaskbarItemProgressState.None
        : IsIndeterminate ? TaskbarItemProgressState.Indeterminate
        : TaskbarItemProgressState.Normal;

    public string ProgressMessage { get => _progressMessage; private set => SetField(ref _progressMessage, value); }
    public string CurrentFile     { get => _currentFile;     private set => SetField(ref _currentFile, value); }
    public string ElapsedText     { get => _elapsedText;     private set => SetField(ref _elapsedText, value); }
    public string EtaText         { get => _etaText;         private set => SetField(ref _etaText, value); }
    public string StatusMessage   { get => _statusMessage;   private set => SetField(ref _statusMessage, value); }

    // ===================== 集計 =====================

    private int _sumScanned, _sumProcessed, _sumSkipped, _sumDuplicates, _sumFiltered, _sumErrors;
    private string _lastRunText = "まだ実行していません";

    public int SummaryScanned    { get => _sumScanned;    private set => SetField(ref _sumScanned, value); }
    public int SummaryProcessed  { get => _sumProcessed;  private set => SetField(ref _sumProcessed, value); }
    public int SummarySkipped    { get => _sumSkipped;    private set => SetField(ref _sumSkipped, value); }
    public int SummaryDuplicates { get => _sumDuplicates; private set => SetField(ref _sumDuplicates, value); }
    public int SummaryFiltered   { get => _sumFiltered;   private set => SetField(ref _sumFiltered, value); }
    public int SummaryErrors     { get => _sumErrors;     private set => SetField(ref _sumErrors, value); }
    public string LastRunText    { get => _lastRunText;   private set => SetField(ref _lastRunText, value); }
    public string ProcessedLabel => _lastResult?.Mode == OperationMode.DryRun ? "整理予定" : "処理済み";

    // ===================== コレクション =====================

    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    public ICollectionView LogView { get; }
    public ObservableCollection<ImageAssetViewModel> Assets { get; } = new();
    public ICollectionView AssetsView { get; }
    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = new();
    public StatisticsViewModel Statistics { get; } = new();

    /// <summary>ログが追加されたとき（自動スクロール用）</summary>
    public event EventHandler? LogAppended;

    private LogFilter _logFilter = LogFilter.All;
    private string _logSearch = "";
    private bool _autoScroll = true;

    public IReadOnlyList<KeyValuePair<LogFilter, string>> LogFilters { get; } =
    [
        new(LogFilter.All, "すべて"),
        new(LogFilter.HideDetails, "詳細を隠す"),
        new(LogFilter.WarningsAndErrors, "注意とエラー"),
        new(LogFilter.ErrorsOnly, "エラーのみ"),
    ];

    public LogFilter LogFilterLevel
    {
        get => _logFilter;
        set { if (SetField(ref _logFilter, value)) LogView.Refresh(); }
    }

    public string LogSearch
    {
        get => _logSearch;
        set { if (SetField(ref _logSearch, value)) LogView.Refresh(); }
    }

    public bool AutoScroll { get => _autoScroll; set => SetField(ref _autoScroll, value); }

    private bool LogPasses(LogEntry e)
    {
        bool levelOk = _logFilter switch
        {
            LogFilter.HideDetails       => e.Level != LogLevel.Debug,
            LogFilter.WarningsAndErrors => e.Level >= LogLevel.Warning,
            LogFilter.ErrorsOnly        => e.Level == LogLevel.Error,
            _ => true,
        };
        return levelOk && (string.IsNullOrWhiteSpace(_logSearch) || e.Message.Contains(_logSearch, StringComparison.OrdinalIgnoreCase));
    }

    private StatusFilterOption _assetFilter = StatusFilterOption.All[0];
    private string _assetSearch = "";

    public IReadOnlyList<StatusFilterOption> AssetFilters => StatusFilterOption.All;

    public StatusFilterOption AssetFilter
    {
        get => _assetFilter;
        set { if (SetField(ref _assetFilter, value ?? StatusFilterOption.All[0])) RefreshAssets(); }
    }

    public string AssetSearch
    {
        get => _assetSearch;
        set { if (SetField(ref _assetSearch, value)) RefreshAssets(); }
    }

    public string AssetCountText => Assets.Count == 0
        ? "実行するとファイルごとの結果がここに表示されます"
        : $"{AssetsView.Cast<object>().Count():N0} / {Assets.Count:N0} 件を表示";

    private bool AssetPasses(ImageAssetViewModel a) =>
        _assetFilter.Predicate(a.Status) && (string.IsNullOrWhiteSpace(_assetSearch) || a.Matches(_assetSearch));

    private void RefreshAssets()
    {
        AssetsView.Refresh();
        OnPropertyChanged(nameof(AssetCountText));
    }

    public string DuplicateSummary
    {
        get
        {
            if (DuplicateGroups.Count == 0) return "";
            var extra = DuplicateGroups.Sum(g => g.Files.Count(f => !f.IsKept));
            var bytes = DuplicateGroups.Sum(g => g.WastedBytes);
            return $"{DuplicateGroups.Count:N0} グループ / 余分なファイル {extra:N0} 件（{Converters.FileSizeConverter.Format(bytes)} 削減できます）";
        }
    }

    private RunHistoryViewModel? LastUndoableRun => Statistics.History.FirstOrDefault(h => h.CanUndo);

    // ===================== コマンド =====================

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand BrowseInputCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand BrowseTrashCommand { get; }
    public RelayCommand BrowseExifToolCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand OpenInputCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SaveLogCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand OpenMappingsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand ClearFilterCommand { get; }
    public AsyncRelayCommand UndoRunCommand { get; }
    public AsyncRelayCommand UndoLastRunCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ShowInExplorerCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand ShowOutputInExplorerCommand { get; }
    public RelayCommand RecycleDuplicateCommand { get; }
    public RelayCommand RecycleAllDuplicatesCommand { get; }
    public RelayCommand InsertVariableCommand { get; }
    public RelayCommand CheckExifToolCommand { get; }

    // ===================== 実行 =====================

    private AppSettings BuildSettings() => new()
    {
        InputDirectory         = InputDirectory,
        OutputDirectory        = OutputDirectory,
        TrashDirectory         = TrashDirectory,
        FolderTemplate         = FolderTemplate,
        FileNameTemplate       = FileNameTemplate,
        Mode                   = OperationMode,
        MaxDegreeOfParallelism = MaxParallelism,
        ExifToolPath           = ExifToolPath,
        FilterStartDate        = FilterStartDate,
        FilterEndDate          = FilterEndDate,
        IncludeVideos          = IncludeVideos,
        IncludeSidecars        = IncludeSidecars,
        VerifyCopies           = VerifyCopies,
        DetectDuplicates       = DetectDuplicates,
        UseMetadataCache       = UseMetadataCache,
        NoDateFolderName       = UseNoDateFolder ? NoDateFolderName : "",
        VerboseLog             = VerboseLog,
    };

    private async Task StartAsync()
    {
        var settings = BuildSettings();
        var validation = SettingsValidator.Validate(settings, _pathResolver);
        if (!validation.IsValid)
        {
            Dialogs.Error("設定を確認してください:\n\n・" + string.Join("\n・", validation.Errors), "実行できません");
            return;
        }

        if (!IsExifToolOk)
        {
            await CheckExifToolAsync();
            if (!IsExifToolOk && !Dialogs.Confirm(
                    "exiftool が見つからないため、撮影日・カメラ名を読み取れません。\n" +
                    "このまま続けると、ファイルの日時でフォルダ分けされ、カメラ名は Unknown になります。\n\n続けますか？",
                    "exiftool が見つかりません", warning: true))
                return;
        }

        if (settings.Mode != OperationMode.DryRun)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(settings.Mode == OperationMode.Move
                ? "MOVE（移動）を実行します。入力フォルダの写真は出力フォルダへ移動し、元の場所からはなくなります。"
                : "COPY（コピー）を実行します。元の写真はそのまま残ります。");
            sb.AppendLine();
            sb.AppendLine($"入力: {settings.InputDirectory}");
            sb.AppendLine($"出力: {settings.OutputDirectory}");
            sb.AppendLine($"フォルダ構成: {settings.FolderTemplate}");
            if (settings.DetectDuplicates)
                sb.AppendLine(string.IsNullOrWhiteSpace(settings.TrashDirectory)
                    ? "重複: 元の場所に残します"
                    : $"重複: {settings.TrashDirectory} へ退避します");
            foreach (var w in validation.Warnings) sb.AppendLine($"※ {w}");
            sb.AppendLine();
            sb.AppendLine("実行後でも「元に戻す」で取り消せます。続けますか？");
            if (!Dialogs.Confirm(sb.ToString(), $"{OrganizeUseCase.ModeLabel(settings.Mode)}の確認", warning: settings.Mode == OperationMode.Move))
                return;
        }

        LogEntries.Clear();
        Assets.Clear();
        DuplicateGroups.Clear();
        _lastResult = null;
        RefreshAssets();
        OnPropertyChanged(nameof(DuplicateSummary));
        foreach (var w in validation.Warnings) LogEntries.Add(LogEntry.Warn(w));

        _cts = new CancellationTokenSource();
        _stopRequested = false;
        IsRunning = true;
        IsIndeterminate = true;
        ProgressValue = 0;
        ProgressTotal = 0;
        CurrentFile = "";
        StatusMessage = "";
        EtaText = "";
        _runClock.Restart();
        _phase = RunPhase.Preparing;
        _phaseStartedAt = DateTime.Now;
        _uiTimer.Start();
        OnPropertiesChanged(nameof(ProgressFraction), nameof(ProgressPercentText));

        var progress = new Progress<OrganizeProgress>(OnProgress);
        // ログは 1 件ずつ UI スレッドへ送ると重いので、キューに溜めてタイマーでまとめて表示する
        var logSink = new DirectLogSink(_pendingLog);

        try
        {
            var result = await _useCase.ExecuteAsync(settings, progress, logSink, _cts.Token);
            ShowResult(result);
        }
        catch (Exception ex)
        {
            FlushLog();
            LogEntries.Add(LogEntry.Error($"予期しないエラー: {ex.Message}"));
            ProgressMessage = "エラーで停止しました";
            Dialogs.Error($"処理中にエラーが発生しました:\n{ex.Message}");
        }
        finally
        {
            _uiTimer.Stop();
            FlushLog();
            _runClock.Stop();
            IsRunning = false;
            IsIndeterminate = false;
            EtaText = "";
            _cts.Dispose();
            _cts = null;
            OnPropertiesChanged(nameof(ProgressFraction), nameof(ProgressPercentText));
            try { Statistics.LoadHistory(_history); } catch { }
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private sealed class DirectLogSink(ConcurrentQueue<LogEntry> queue) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) => queue.Enqueue(value);
    }

    private void OnProgress(OrganizeProgress p)
    {
        if (p.Phase != _phase)
        {
            _phase = p.Phase;
            _phaseStartedAt = DateTime.Now;
            _phaseStartDone = p.Done;
        }
        IsIndeterminate = p.Total == 0;
        ProgressTotal = Math.Max(p.Total, 1);
        ProgressValue = Math.Min(p.Done, ProgressTotal);
        ProgressMessage = p.Message;
        CurrentFile = p.CurrentFile;
        OnPropertiesChanged(nameof(ProgressFraction), nameof(ProgressPercentText));
    }

    private void UpdateClock()
    {
        if (!IsRunning) return;
        ElapsedText = $"経過 {OrganizeUseCase.FormatElapsed(_runClock.Elapsed)}";

        var doneInPhase = ProgressValue - _phaseStartDone;
        var spent = DateTime.Now - _phaseStartedAt;
        if (!IsIndeterminate && doneInPhase > 0 && spent.TotalSeconds > 2 && ProgressValue < ProgressTotal)
        {
            var remaining = TimeSpan.FromSeconds(spent.TotalSeconds / doneInPhase * (ProgressTotal - ProgressValue));
            EtaText = $"この工程の残り 約{OrganizeUseCase.FormatElapsed(remaining)}";
        }
        else EtaText = "";
    }

    private void FlushLog()
    {
        if (_pendingLog.IsEmpty) return;
        int added = 0;
        while (added < 5000 && _pendingLog.TryDequeue(out var e))
        {
            LogEntries.Add(e);
            added++;
        }
        if (added > 0) LogAppended?.Invoke(this, EventArgs.Empty);
    }

    private void ShowResult(OrganizeResult r)
    {
        _lastResult = r;
        SummaryScanned    = r.TotalScanned;
        SummaryProcessed  = r.TotalProcessed;
        SummarySkipped    = r.TotalSkipped;
        SummaryDuplicates = r.TotalDuplicates;
        SummaryFiltered   = r.TotalFiltered;
        SummaryErrors     = r.TotalErrors;
        OnPropertyChanged(nameof(ProcessedLabel));
        LastRunText = $"{DateTime.Now:HH:mm} の {OrganizeUseCase.ModeLabel(r.Mode)}{(r.Cancelled ? "（中断）" : "")} ・ {OrganizeUseCase.FormatElapsed(r.Elapsed)}";

        foreach (var a in r.Assets) Assets.Add(new ImageAssetViewModel(a));
        RefreshAssets();

        foreach (var g in r.DuplicateGroups) DuplicateGroups.Add(new DuplicateGroupViewModel(g.GroupId, g.Files));
        OnPropertyChanged(nameof(DuplicateSummary));

        Statistics.Update(r.Statistics);

        ProgressTotal = 1;
        ProgressValue = r.Cancelled ? 0 : 1;
        ProgressMessage = r.Log.LastOrDefault()?.Message ?? "完了しました";
        CurrentFile = "";
        StatusMessage = r.TotalErrors > 0 ? $"エラーが {r.TotalErrors:N0} 件あります。ログの「エラーのみ」で確認できます。" : "";
    }

    private void Stop()
    {
        _stopRequested = true;
        _cts?.Cancel();
        ProgressMessage = "中断しています（処理中のファイルが終わるまでお待ちください）...";
    }

    // ===================== 元に戻す =====================

    private async Task UndoAsync(RunHistoryViewModel? run)
    {
        if (run?.Record.RunId is not { } runId) return;
        if (!Dialogs.Confirm(
                $"{run.RunAt} に行った {run.Mode}（{run.Record.OperationCount:N0} 件の操作）を取り消します。\n\n" +
                "・コピーしたファイルは出力先から削除します（コピー後に変更されたものは残します）\n" +
                "・移動・退避したファイルは元の場所に戻します\n\n続けますか？", "元に戻す", warning: true))
            return;

        IsRunning = true;
        IsIndeterminate = true;
        ProgressMessage = "取り消しています...";
        try
        {
            var result = await _undo.UndoAsync(runId, run.Record.OutputDirectory);
            LogEntries.Clear();
            foreach (var e in result.Log) LogEntries.Add(e);
            LogAppended?.Invoke(this, EventArgs.Empty);
            ProgressMessage = $"取り消し完了: 復元 {result.Restored:N0} 件 / スキップ {result.Skipped:N0} 件 / 失敗 {result.Failed:N0} 件";
            if (result.Failed > 0 || result.Skipped > 0)
                Dialogs.Info(ProgressMessage + "\n\n詳しくは「実行ログ」タブを確認してください。", "元に戻す");
        }
        finally
        {
            IsRunning = false;
            IsIndeterminate = false;
            Statistics.LoadHistory(_history);
        }
    }

    private void ClearHistory()
    {
        if (!Dialogs.Confirm("実行履歴と統計をすべて削除します。\n削除すると、過去の実行を「元に戻す」ことはできなくなります。\n\n続けますか？",
                "履歴の削除", warning: true))
            return;
        _history.ClearHistory();
        Statistics.LoadHistory(_history);
    }

    // ===================== 重複ファイル =====================

    private void RecycleDuplicates(IReadOnlyList<DuplicateFileViewModel> files)
    {
        if (files.Count == 0) return;
        var bytes = files.Sum(f => f.Asset.FileSize);
        var message = files.Count == 1
            ? $"このファイルを Windows のごみ箱へ送ります。\n\n{files[0].CurrentPath}\n\n（同じ内容のファイルは残ります）"
            : $"重複している {files.Count:N0} 件（{Converters.FileSizeConverter.Format(bytes)}）を Windows のごみ箱へ送ります。\n各グループで「残す」のファイルはそのまま残ります。\n\n続けますか？";
        if (!Dialogs.Confirm(message, "ごみ箱へ送る")) return;

        int ok = 0;
        var failed = new List<string>();
        foreach (var f in files)
        {
            try
            {
                Shell.SendToRecycleBin(f.CurrentPath);
                f.IsRemoved = true;
                ok++;
            }
            catch (Exception ex) { failed.Add($"{f.CurrentPath}: {ex.Message}"); }
        }
        StatusMessage = $"{ok:N0} 件をごみ箱へ送りました" + (failed.Count > 0 ? $"（{failed.Count} 件は失敗）" : "");
        if (failed.Count > 0) Dialogs.Error("ごみ箱へ送れなかったファイル:\n\n" + string.Join("\n", failed.Take(10)));
    }

    // ===================== その他 =====================

    private static void Browse(string title, Action<string> set, string current)
    {
        var picked = Dialogs.PickFolder(title, current);
        if (picked != null) set(picked);
    }

    private void BrowseExifTool()
    {
        var picked = Dialogs.PickFile("exiftool の実行ファイルを選択", "exiftool|exiftool*.exe|すべてのファイル|*.*", ExifToolPath);
        if (picked != null) ExifToolPath = picked;
    }

    private void SaveLog()
    {
        var path = Dialogs.SaveFile("ログを保存", "テキストファイル (*.txt)|*.txt", $"PhotoExplorer_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        if (path == null) return;
        try
        {
            File.WriteAllLines(path, LogEntries.Select(e => e.ToString()), new System.Text.UTF8Encoding(true));
            StatusMessage = $"ログを保存しました: {path}";
        }
        catch (Exception ex) { Dialogs.Error($"保存できませんでした:\n{ex.Message}"); }
    }

    private void ExportCsv()
    {
        if (_lastResult == null) return;
        var path = Dialogs.SaveFile("結果を CSV で保存", "CSV ファイル (*.csv)|*.csv", $"PhotoExplorer_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        if (path == null) return;
        try
        {
            var visible = AssetsView.Cast<ImageAssetViewModel>().Select(a => a.Asset).ToList();
            CsvExporter.Write(path, visible, a => ImageAssetViewModel.StatusText(a.Status));
            StatusMessage = $"{visible.Count:N0} 件を CSV に保存しました: {path}";
        }
        catch (Exception ex) { Dialogs.Error($"保存できませんでした:\n{ex.Message}"); }
    }

    private void OpenMappings()
    {
        _ = _mappings.Current; // ファイルがなければ作成済み
        try
        {
            Shell.OpenInNotepad(MappingConfig.DefaultPath);
            StatusMessage = "名前の置き換え設定をメモ帳で開きました。保存すると次の実行から反映されます。";
        }
        catch (Exception ex) { Dialogs.Error($"メモ帳を開けませんでした:\n{ex.Message}\n\n{MappingConfig.DefaultPath}"); }
    }

    private static string? PathOf(object? p) => p switch
    {
        string s => s,
        ImageAssetViewModel a => File.Exists(a.SourcePath) ? a.SourcePath : a.OutputPath ?? a.SourcePath,
        DuplicateFileViewModel d => d.CurrentPath,
        _ => null,
    };

    /// <summary>ドラッグ＆ドロップされたフォルダを設定する</summary>
    public void SetDroppedFolder(string target, string path)
    {
        if (IsRunning) return;
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;
        switch (target)
        {
            case "Input":  InputDirectory = dir; break;
            case "Output": OutputDirectory = dir; break;
            case "Trash":  TrashDirectory = dir; break;
        }
    }
}
