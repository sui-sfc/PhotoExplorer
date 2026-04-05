using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Infrastructure;
using PhotoExplorer.Presentation.ViewModels;
using PhotoExplorer.Presentation.Views;
// PhotoExplorer.Application 名前空間と System.Windows.Application クラスの衝突を回避
using WpfApplication = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace PhotoExplorer;

public partial class App : WpfApplication
{
    private IServiceProvider? _serviceProvider;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未処理例外をすべてキャッチしてダイアログ表示
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ShowFatal(args.ExceptionObject?.ToString() ?? "不明なエラー");
        DispatcherUnhandledException += (_, args) =>
        {
            ShowFatal(args.Exception.ToString());
            args.Handled = true;
        };

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            ShowFatal(ex.ToString());
            Shutdown(1);
        }
    }

    private static void ShowFatal(string message)
    {
        MessageBox.Show(
            $"起動に失敗しました。\n\n{message}",
            "PhotoExplorer - 起動エラー",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // exiftool のパスを決定（実行ファイルと同階層の Resources フォルダを優先）
        var appDir = AppContext.BaseDirectory;
        var exifToolPath = Path.Combine(appDir, "Resources", "exiftool.exe");
        if (!File.Exists(exifToolPath))
            exifToolPath = "exiftool.exe"; // PATH から探す

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoExplorer");
        Directory.CreateDirectory(appDataDir);

        var dbPath = Path.Combine(appDataDir, "photo_explorer.db");

        // カスタムマッピング設定を読み込む（なければデフォルトを生成）
        var mappingsPath = MappingConfig.DefaultPath;
        MappingConfig mappingConfig;
        if (File.Exists(mappingsPath))
        {
            mappingConfig = MappingConfig.Load(mappingsPath);
        }
        else
        {
            mappingConfig = MappingConfig.CreateDefault();
            mappingConfig.Save(mappingsPath);
        }

        // Infrastructure
        services.AddSingleton<IFileScanner, FileScanner>();
        services.AddSingleton<IMetadataExtractor>(_ => new ExifToolMetadataExtractor(exifToolPath));
        services.AddSingleton<IFileOperator, FileOperator>();
        services.AddSingleton<IAssetRepository>(_ => new SqliteAssetRepository(dbPath));
        services.AddSingleton(mappingConfig);
        services.AddSingleton<INormalizer>(sp => new Normalizer(sp.GetRequiredService<MappingConfig>()));
        services.AddSingleton<IPathResolver, PathResolver>();
        services.AddSingleton<IDuplicateDetector, DuplicateDetector>();

        // Application
        services.AddTransient<IOrganizeUseCase, OrganizeUseCase>();

        // Presentation
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_serviceProvider is IDisposable d) d.Dispose();
        base.OnExit(e);
    }
}
