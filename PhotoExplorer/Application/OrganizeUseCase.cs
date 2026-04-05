using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Application;

public sealed class OrganizeUseCase : IOrganizeUseCase
{
    private readonly IFileScanner _scanner;
    private readonly IMetadataExtractor _metadataExtractor;
    private readonly INormalizer _normalizer;
    private readonly IPathResolver _pathResolver;
    private readonly IDuplicateDetector _duplicateDetector;
    private readonly IFileOperator _fileOperator;
    private readonly IAssetRepository _repository;

    public OrganizeUseCase(
        IFileScanner scanner,
        IMetadataExtractor metadataExtractor,
        INormalizer normalizer,
        IPathResolver pathResolver,
        IDuplicateDetector duplicateDetector,
        IFileOperator fileOperator,
        IAssetRepository repository)
    {
        _scanner = scanner;
        _metadataExtractor = metadataExtractor;
        _normalizer = normalizer;
        _pathResolver = pathResolver;
        _duplicateDetector = duplicateDetector;
        _fileOperator = fileOperator;
        _repository = repository;
    }

    public async Task<OrganizeResult> ExecuteAsync(
        AppSettings settings,
        IProgress<OrganizeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        void Log(string msg) { log.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"); }

        // ----- Step 1: Scan -----
        Log("スキャン開始...");
        progress?.Report(new(0, 0, "", "ファイルスキャン中..."));

        var files = await _scanner.ScanAsync(settings.InputDirectory, cancellationToken);
        Log($"スキャン完了: {files.Count} ファイル検出");

        var assets = files.Select(f => new ImageAsset
        {
            SourcePath = f.FullPath,
            FileSize = f.FileSize,
            FileCreatedAt = f.CreatedAt,
            Status = AssetStatus.Scanned,
        }).ToList();

        await _repository.SaveAllAsync(assets, cancellationToken);

        // ----- Step 2: EXIF extraction -----
        Log("メタデータ抽出中...");
        int processed = 0;
        var semaphore = new SemaphoreSlim(settings.MaxDegreeOfParallelism);
        var tasks = assets.Select(async asset =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exif = await _metadataExtractor.ExtractAsync(asset.SourcePath, cancellationToken);
                asset.Exif = exif;
                asset.Status = exif?.EffectiveDate.HasValue == true
                    ? AssetStatus.MetadataOk
                    : (exif != null ? AssetStatus.PartialMetadata : AssetStatus.NoMetadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                asset.Status = AssetStatus.Error;
                asset.ErrorMessage = ex.Message;
                Log($"EXIF抽出エラー: {asset.FileName} - {ex.Message}");
            }
            finally
            {
                semaphore.Release();
                var current = Interlocked.Increment(ref processed);
                progress?.Report(new(assets.Count, current, asset.FileName, $"メタデータ抽出: {asset.FileName}"));
            }
        });
        await Task.WhenAll(tasks);
        Log($"メタデータ抽出完了");

        // ----- Step 3: Normalize -----
        Log("名称正規化中...");
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            asset.NormalizedCamera = _normalizer.NormalizeCamera(asset.Exif?.CameraName);
            asset.NormalizedLens = _normalizer.NormalizeLens(asset.Exif?.LensModel);
        }

        // ----- Step 4: Resolve output path -----
        Log("出力パス計算中...");
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            asset.OutputPath = _pathResolver.Resolve(asset, settings.OutputDirectory, settings.FolderTemplate);
        }

        // ----- Step 5: Duplicate detection -----
        Log("重複検出中...");
        var progressReporter = new Progress<string>(msg =>
            progress?.Report(new(assets.Count, processed, "", msg)));
        await _duplicateDetector.DetectDuplicatesAsync(assets, progressReporter, cancellationToken);
        int duplicateCount = assets.Count(a => a.Status == AssetStatus.Duplicate);
        Log($"重複検出完了: {duplicateCount} 件の重複");

        // ----- Step 6: Execute file operations -----
        if (settings.Mode == OperationMode.DryRun)
        {
            Log("DRY_RUN モード: 実際のファイル操作はスキップ");
            foreach (var asset in assets.Where(a => a.Status != AssetStatus.Duplicate && a.Status != AssetStatus.Error))
                Log($"  [{settings.Mode}] {asset.SourcePath} => {asset.OutputPath}");
        }
        else
        {
            Log($"ファイル操作開始 (モード: {settings.Mode})...");
            processed = 0;
            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asset.Status == AssetStatus.Error) continue;

                if (asset.Status == AssetStatus.Duplicate && !asset.IsKeptInDuplicateGroup)
                {
                    // 重複ファイルをゴミ箱へ
                    if (!string.IsNullOrWhiteSpace(settings.TrashDirectory))
                    {
                        try
                        {
                            var trashPath = await _fileOperator.MoveToTrashAsync(
                                asset.SourcePath, settings.TrashDirectory, cancellationToken);
                            Log($"重複削除: {asset.SourcePath} => {trashPath}");
                        }
                        catch (Exception ex)
                        {
                            Log($"ゴミ箱移動エラー: {asset.SourcePath} - {ex.Message}");
                        }
                    }
                    continue;
                }

                if (asset.OutputPath == null) continue;

                try
                {
                    string actualOutput;
                    if (settings.Mode == OperationMode.Copy)
                        actualOutput = await _fileOperator.CopyAsync(asset.SourcePath, asset.OutputPath, cancellationToken);
                    else
                        actualOutput = await _fileOperator.MoveAsync(asset.SourcePath, asset.OutputPath, cancellationToken);

                    asset.OutputPath = actualOutput;
                    asset.Status = AssetStatus.Processed;
                    asset.ProcessedAt = DateTime.Now;
                    Log($"完了: {asset.SourcePath} => {actualOutput}");
                }
                catch (Exception ex)
                {
                    asset.Status = AssetStatus.Error;
                    asset.ErrorMessage = ex.Message;
                    Log($"操作エラー: {asset.SourcePath} - {ex.Message}");
                }

                processed++;
                progress?.Report(new(assets.Count, processed, asset.FileName, $"処理中: {asset.FileName}"));
            }
        }

        // ----- Step 7: Save state -----
        await _repository.SaveAllAsync(assets, cancellationToken);
        Log("処理完了");

        return new OrganizeResult
        {
            TotalScanned = assets.Count,
            TotalProcessed = assets.Count(a => a.Status == AssetStatus.Processed),
            TotalDuplicates = duplicateCount,
            TotalErrors = assets.Count(a => a.Status == AssetStatus.Error),
            Log = log,
        };
    }
}
