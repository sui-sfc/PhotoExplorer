using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Infrastructure;
using WpfApplication = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace PhotoExplorer.Presentation.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly IOrganizeUseCase _useCase;
    private readonly UserPreferences  _prefs;
    private readonly StatsHistoryRepository _statsRepo;

    // ----- Settings -----
    private string _inputDirectory;
    private string _outputDirectory;
    private string _trashDirectory;
    private string _folderTemplate;
    private OperationMode _operationMode;
    private int _maxParallelism;
    private DateTime? _filterStartDate;
    private DateTime? _filterEndDate;

    // ----- State -----
    private bool _isRunning;
    private int  _progressTotal;
    private int  _progressValue;
    private string _progressMessage = string.Empty;
    private string _currentFile     = string.Empty;
    private CancellationTokenSource? _cts;

    // ----- Summary -----
    private int _summaryScanned, _summaryProcessed, _summaryDuplicates, _summaryErrors;

    public MainViewModel(IOrganizeUseCase useCase, StatsHistoryRepository statsRepo)
    {
        _useCase   = useCase;
        _statsRepo = statsRepo;

        _prefs = UserPreferences.Load();
        _inputDirectory  = _prefs.InputDirectory;
        _outputDirectory = _prefs.OutputDirectory;
        _trashDirectory  = _prefs.TrashDirectory;
        _folderTemplate  = _prefs.FolderTemplate;
        _operationMode   = _prefs.Mode;
        _maxParallelism  = _prefs.MaxParallelism;
        _filterStartDate = _prefs.FilterStartDate;
        _filterEndDate   = _prefs.FilterEndDate;

        StartCommand        = new AsyncRelayCommand(StartAsync, () => !IsRunning && !string.IsNullOrWhiteSpace(InputDirectory));
        StopCommand         = new RelayCommand(Stop, () => IsRunning);
        BrowseInputCommand  = new RelayCommand(BrowseInput);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        BrowseTrashCommand  = new RelayCommand(BrowseTrash);
        ClearLogCommand     = new RelayCommand(ClearLog, () => LogEntries.Count > 0);
        SaveLogCommand      = new RelayCommand(SaveLog,  () => LogEntries.Count > 0);
        OpenMappingsCommand = new RelayCommand(OpenMappingsFile);
        ClearFilterCommand  = new RelayCommand(ClearFilter);

        Statistics.LoadHistory(_statsRepo);
    }

    // ----- Settings Properties -----

    public string InputDirectory
    {
        get => _inputDirectory;
        set { if (SetField(ref _inputDirectory, value)) SavePrefs(); }
    }
    public string OutputDirectory
    {
        get => _outputDirectory;
        set { if (SetField(ref _outputDirectory, value)) SavePrefs(); }
    }
    public string TrashDirectory
    {
        get => _trashDirectory;
        set { if (SetField(ref _trashDirectory, value)) SavePrefs(); }
    }
    public string FolderTemplate
    {
        get => _folderTemplate;
        set { if (SetField(ref _folderTemplate, value)) SavePrefs(); }
    }
    public OperationMode OperationMode
    {
        get => _operationMode;
        set { if (SetField(ref _operationMode, value)) SavePrefs(); }
    }
    public IEnumerable<OperationMode> OperationModes => Enum.GetValues<OperationMode>();

    public int MaxParallelism
    {
        get => _maxParallelism;
        set { if (SetField(ref _maxParallelism, Math.Clamp(value, 1, 16))) SavePrefs(); }
    }
    public DateTime? FilterStartDate
    {
        get => _filterStartDate;
        set { if (SetField(ref _filterStartDate, value)) SavePrefs(); }
    }
    public DateTime? FilterEndDate
    {
        get => _filterEndDate;
        set { if (SetField(ref _filterEndDate, value)) SavePrefs(); }
    }
    public bool HasDateFilter => _filterStartDate.HasValue || _filterEndDate.HasValue;

    // ----- State Properties -----

    public bool IsRunning
    {
        get => _isRunning;
        private set { SetField(ref _isRunning, value); OnPropertyChanged(nameof(IsIdle)); }
    }
    public bool IsIdle => !_isRunning;

    public int ProgressTotal
    {
        get => _progressTotal;
        private set => SetField(ref _progressTotal, value);
    }
    public int ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }
    public string ProgressMessage
    {
        get => _progressMessage;
        private set => SetField(ref _progressMessage, value);
    }
    public string CurrentFile
    {
        get => _currentFile;
        private set => SetField(ref _currentFile, value);
    }
    public double ProgressPercent =>
        ProgressTotal > 0 ? (double)ProgressValue / ProgressTotal * 100 : 0;

    public int SummaryScanned    { get => _summaryScanned;    private set => SetField(ref _summaryScanned,    value); }
    public int SummaryProcessed  { get => _summaryProcessed;  private set => SetField(ref _summaryProcessed,  value); }
    public int SummaryDuplicates { get => _summaryDuplicates; private set => SetField(ref _summaryDuplicates, value); }
    public int SummaryErrors     { get => _summaryErrors;     private set => SetField(ref _summaryErrors,     value); }

    // ----- Collections -----
    public ObservableCollection<string> LogEntries { get; } = new();
    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = new();

    // ----- Sub ViewModels -----
    public StatisticsViewModel Statistics { get; } = new();

    // ----- Commands -----
    public AsyncRelayCommand StartCommand        { get; }
    public RelayCommand      StopCommand         { get; }
    public RelayCommand      BrowseInputCommand  { get; }
    public RelayCommand      BrowseOutputCommand { get; }
    public RelayCommand      BrowseTrashCommand  { get; }
    public RelayCommand      ClearLogCommand     { get; }
    public RelayCommand      SaveLogCommand      { get; }
    public RelayCommand      OpenMappingsCommand { get; }
    public RelayCommand      ClearFilterCommand  { get; }

    public string MappingsFilePath => MappingConfig.DefaultPath;

    // ----- Methods -----

    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(InputDirectory))
        {
            MessageBox.Show("入力フォルダを指定してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (OperationMode != OperationMode.DryRun && string.IsNullOrWhiteSpace(OutputDirectory))
        {
            MessageBox.Show("出力フォルダを指定してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LogEntries.Clear();
        DuplicateGroups.Clear();
        IsRunning     = true;
        ProgressValue = 0;
        ProgressTotal = 0;
        _cts = new CancellationTokenSource();

        var settings = new AppSettings
        {
            InputDirectory         = InputDirectory,
            OutputDirectory        = OutputDirectory,
            TrashDirectory         = TrashDirectory,
            FolderTemplate         = FolderTemplate,
            Mode                   = OperationMode,
            MaxDegreeOfParallelism = MaxParallelism,
            FilterStartDate        = FilterStartDate,
            FilterEndDate          = FilterEndDate,
        };

        var progress = new Progress<OrganizeProgress>(p =>
        {
            WpfApplication.Current.Dispatcher.Invoke(() =>
            {
                ProgressTotal   = p.Total;
                ProgressValue   = p.Processed;
                ProgressMessage = p.Message;
                CurrentFile     = p.CurrentFile;
                OnPropertyChanged(nameof(ProgressPercent));
            });
        });

        try
        {
            var result = await _useCase.ExecuteAsync(settings, progress, _cts.Token);

            SummaryScanned    = result.TotalScanned;
            SummaryProcessed  = result.TotalProcessed;
            SummaryDuplicates = result.TotalDuplicates;
            SummaryErrors     = result.TotalErrors;

            foreach (var entry in result.Log)
                LogEntries.Add(entry);

            // 統計タブを更新
            Statistics.Update(result.Statistics);

            // 重複タブを更新
            foreach (var (groupId, paths) in result.DuplicateGroups)
            {
                var vm = new DuplicateGroupViewModel { GroupId = groupId };
                foreach (var p in paths) vm.Files.Add(p);
                DuplicateGroups.Add(vm);
            }

            // 実行履歴を保存
            _statsRepo.SaveRun(InputDirectory, result.Statistics,
                result.TotalProcessed, result.TotalDuplicates, result.TotalErrors);
            Statistics.LoadHistory(_statsRepo);

            ProgressMessage = $"完了 — スキャン:{result.TotalScanned} 処理:{result.TotalProcessed} 重複:{result.TotalDuplicates} エラー:{result.TotalErrors}";
        }
        catch (OperationCanceledException)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] 処理が中断されました");
            ProgressMessage = "中断されました";
        }
        catch (Exception ex)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] 予期しないエラー: {ex.Message}");
            MessageBox.Show($"エラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Stop() { _cts?.Cancel(); ProgressMessage = "中断中..."; }

    private void BrowseInput()  { var p = BrowseFolder("入力フォルダを選択");   if (p != null) InputDirectory  = p; }
    private void BrowseOutput() { var p = BrowseFolder("出力フォルダを選択");   if (p != null) OutputDirectory = p; }
    private void BrowseTrash()  { var p = BrowseFolder("ゴミ箱フォルダを選択"); if (p != null) TrashDirectory  = p; }
    private void ClearLog()     => LogEntries.Clear();
    private void ClearFilter()
    {
        FilterStartDate = null;
        FilterEndDate   = null;
        OnPropertyChanged(nameof(HasDateFilter));
    }

    private void SaveLog()
    {
        var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title            = "ログを保存",
            Filter           = "テキストファイル (*.txt)|*.txt|すべてのファイル|*.*",
            FileName         = $"PhotoExplorer_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            DefaultExt       = "txt",
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try
        {
            File.WriteAllLines(dialog.FileName, LogEntries);
            MessageBox.Show($"ログを保存しました:\n{dialog.FileName}", "保存完了",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenMappingsFile()
    {
        var path = MappingConfig.DefaultPath;
        if (!File.Exists(path)) MappingConfig.CreateDefault().Save(path);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        MessageBox.Show(
            $"マッピングファイルをメモ帳で開きました。\n保存後、次回の実行から反映されます。\n\n場所: {path}",
            "マッピング設定", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SavePrefs()
    {
        _prefs.InputDirectory  = _inputDirectory;
        _prefs.OutputDirectory = _outputDirectory;
        _prefs.TrashDirectory  = _trashDirectory;
        _prefs.FolderTemplate  = _folderTemplate;
        _prefs.Mode            = _operationMode;
        _prefs.MaxParallelism  = _maxParallelism;
        _prefs.FilterStartDate = _filterStartDate;
        _prefs.FilterEndDate   = _filterEndDate;
        _prefs.Save();
    }

    private static string? BrowseFolder(string description)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath : null;
    }
}
