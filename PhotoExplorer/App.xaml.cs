using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Infrastructure;
using PhotoExplorer.Presentation.ViewModels;
using PhotoExplorer.Presentation.Views;

namespace PhotoExplorer;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // exiftool のパスを決定（実行ファイルと同階層の Resources フォルダを優先）
        var appDir = AppContext.BaseDirectory;
        var exifToolPath = Path.Combine(appDir, "Resources", "exiftool.exe");
        if (!File.Exists(exifToolPath))
            exifToolPath = "exiftool.exe"; // PATH から探す

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoExplorer", "photo_explorer.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // Infrastructure
        services.AddSingleton<IFileScanner, FileScanner>();
        services.AddSingleton<IMetadataExtractor>(_ => new ExifToolMetadataExtractor(exifToolPath));
        services.AddSingleton<IFileOperator, FileOperator>();
        services.AddSingleton<IAssetRepository>(_ => new SqliteAssetRepository(dbPath));
        services.AddSingleton<INormalizer, Normalizer>();
        services.AddSingleton<IPathResolver, PathResolver>();
        services.AddSingleton<IDuplicateDetector, DuplicateDetector>();

        // Application
        services.AddTransient<IOrganizeUseCase, OrganizeUseCase>();

        // Presentation
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider is IDisposable d) d.Dispose();
        base.OnExit(e);
    }
}
