using System.Collections.ObjectModel;
using System.Windows;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using WpfApplication = System.Windows.Application;

namespace PhotoExplorer.Presentation.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly IOrganizeUseCase _useCase;

    // ----- Settings -----
    private string _inputDirectory = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _trashDirectory = string.Empty;
    private string _folderTemplate = "{yyyy}/{mm}/{dd}/{camera}";
    private OperationMode _operationMode = OperationMode.DryRun;
    private int _maxParallelism = 4;

    // ----- State -----
    private bool _isRunning;
    private int _progressTotal;
    private int _progressValue;
    private string _progressMessage = string.Empty;
    private string _currentFile = string.Empty;
    private CancellationTokenSource? _cts;

    public MainViewModel(IOrganizeUseCase useCase)
    {
        _useCase = useCase;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning && !string.IsNullOrWhiteSpace(InputDirectory));
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        BrowseInputCommand = new RelayCommand(BrowseInput);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        BrowseTrashCommand = new RelayCommand(BrowseTrash);
        ClearLogCommand = new RelayCommand(ClearLog, () => LogEntries.Count > 0);
    }

    // ----- Settings Properties -----

    public string InputDirectory
    {
        get => _inputDirectory;
        set { SetField(ref _inputDirectory, value); }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetField(ref _outputDirectory, value);
    }

    public string TrashDirectory
    {
        get => _trashDirectory;
        set => SetField(ref _trashDirectory, value);
    }

    public string FolderTemplate
    {
        get => _folderTemplate;
        set => SetField(ref _folderTemplate, value);
    }

    public OperationMode OperationMode
    {
        get => _operationMode;
        set => SetField(ref _operationMode, value);
    }

    public IEnumerable<OperationMode> OperationModes => Enum.GetValues<OperationMode>();

    public int MaxParallelism
    {
        get => _maxParallelism;
        set => SetField(ref _maxParallelism, Math.Clamp(value, 1, 16));
    }

    // ----- State Properties -----

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            SetField(ref _isRunning, value);
            OnPropertyChanged(nameof(IsIdle));
        }
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

    // ----- Summary -----
    private int _summaryScanned, _summaryProcessed, _summaryDuplicates, _summaryErrors;
    public int SummaryScanned { get => _summaryScanned; private set => SetField(ref _summaryScanned, value); }
    public int SummaryProcessed { get => _summaryProcessed; private set => SetField(ref _summaryProcessed, value); }
    public int SummaryDuplicates { get => _summaryDuplicates; private set => SetField(ref _summaryDuplicates, value); }
    public int SummaryErrors { get => _summaryErrors; private set => SetField(ref _summaryErrors, value); }

    // ----- Collections -----
    public ObservableCollection<ImageAssetViewModel> Assets { get; } = new();
    public ObservableCollection<string> LogEntries { get; } = new();

    // ----- Commands -----
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand BrowseInputCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand BrowseTrashCommand { get; }
    public RelayCommand ClearLogCommand { get; }

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

        Assets.Clear();
        LogEntries.Clear();
        IsRunning = true;
        ProgressValue = 0;
        ProgressTotal = 0;

        _cts = new CancellationTokenSource();

        var settings = new AppSettings
        {
            InputDirectory = InputDirectory,
            OutputDirectory = OutputDirectory,
            TrashDirectory = TrashDirectory,
            FolderTemplate = FolderTemplate,
            Mode = OperationMode,
            MaxDegreeOfParallelism = MaxParallelism,
        };

        var progress = new Progress<OrganizeProgress>(p =>
        {
            WpfApplication.Current.Dispatcher.Invoke(() =>
            {
                ProgressTotal = p.Total;
                ProgressValue = p.Processed;
                ProgressMessage = p.Message;
                CurrentFile = p.CurrentFile;
                OnPropertyChanged(nameof(ProgressPercent));
            });
        });

        try
        {
            var result = await _useCase.ExecuteAsync(settings, progress, _cts.Token);

            SummaryScanned = result.TotalScanned;
            SummaryProcessed = result.TotalProcessed;
            SummaryDuplicates = result.TotalDuplicates;
            SummaryErrors = result.TotalErrors;

            foreach (var entry in result.Log)
                LogEntries.Add(entry);

            ProgressMessage = $"完了 - スキャン:{result.TotalScanned} 処理:{result.TotalProcessed} 重複:{result.TotalDuplicates} エラー:{result.TotalErrors}";
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

    private void Stop()
    {
        _cts?.Cancel();
        ProgressMessage = "中断中...";
    }

    private void BrowseInput()
    {
        var path = BrowseFolder("入力フォルダを選択");
        if (path != null) InputDirectory = path;
    }

    private void BrowseOutput()
    {
        var path = BrowseFolder("出力フォルダを選択");
        if (path != null) OutputDirectory = path;
    }

    private void BrowseTrash()
    {
        var path = BrowseFolder("ゴミ箱フォルダを選択");
        if (path != null) TrashDirectory = path;
    }

    private void ClearLog() => LogEntries.Clear();

    private static string? BrowseFolder(string description)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }
}
