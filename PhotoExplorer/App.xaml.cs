using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Infrastructure;
using PhotoExplorer.Presentation.ViewModels;
using PhotoExplorer.Presentation.Views;

namespace PhotoExplorer;

public partial class App : WpfApplication
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(args.ExceptionObject as Exception, "予期しないエラー");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        // README 用のスクリーンショットを作る: PhotoExplorer.exe --screenshots <出力フォルダ>
        int shotIndex = Array.IndexOf(e.Args, "--screenshots");
        string? screenshotDir = shotIndex >= 0 && shotIndex + 1 < e.Args.Length ? e.Args[shotIndex + 1] : null;
        if (screenshotDir != null)
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvironmentVariable,
                Path.Combine(Path.GetTempPath(), "PhotoExplorerScreenshots", "data"));

        try
        {
            AppPaths.EnsureCreated();
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            var window = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            if (screenshotDir != null)
                _ = Presentation.Tools.ScreenshotRunner.RunAsync(this, window,
                    _serviceProvider.GetRequiredService<MainViewModel>(), screenshotDir);
        }
        catch (Exception ex)
        {
            ReportFatal(ex, "起動に失敗しました");
            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_ => new MappingConfigProvider(MappingConfig.DefaultPath));
        services.AddSingleton<INormalizer>(sp => new Normalizer(sp.GetRequiredService<MappingConfigProvider>()));
        services.AddSingleton<IPathResolver, PathResolver>();
        services.AddSingleton<IDuplicateDetector, DuplicateDetector>();
        services.AddSingleton<IFileScanner, FileScanner>();
        services.AddSingleton<IMetadataExtractor>(_ => new ExifToolMetadataExtractor());
        services.AddSingleton<IFileOperator, FileOperator>();
        services.AddSingleton<IAssetRepository>(_ => new SqliteAssetRepository(AppPaths.AssetDatabase));
        services.AddSingleton(_ => new StatsHistoryRepository(AppPaths.HistoryDatabase));
        services.AddSingleton(_ => new OperationJournal(AppPaths.HistoryDatabase));
        services.AddSingleton<UndoService>();
        services.AddSingleton<IOrganizeUseCase, OrganizeUseCase>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportFatal(e.Exception, "エラーが発生しました");
    }

    private static void ReportFatal(Exception? ex, string title)
    {
        var logPath = WriteCrashLog(ex);
        var message = $"{title}。\n\n{ex?.Message}\n\n詳細はログに記録しました:\n{logPath}";
        try
        {
            System.Windows.MessageBox.Show(message, "PhotoExplorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    private static string WriteCrashLog(Exception? ex)
    {
        try
        {
            AppPaths.EnsureCreated();
            var sb = new StringBuilder()
                .AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} (v{typeof(App).Assembly.GetName().Version}) ====")
                .AppendLine(ex?.ToString() ?? "(例外情報なし)");
            File.AppendAllText(AppPaths.CrashLog, sb.ToString());
        }
        catch { }
        return AppPaths.CrashLog;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
