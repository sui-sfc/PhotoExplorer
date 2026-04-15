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

        // ----- exiftool 可用性チェック -----
        if (_metadataExtractor is ExifToolMetadataExtractor et)
        {
            var ver = et.GetVersion();
            if (ver == null)
                Log($"⚠ exiftool が見つかりません（パス: {settings.ExifToolPath}）。カメラ/レンズ名は不明になります。");
            else
                Log($"exiftool v{ver} を使用します。");
        }

        // ----- Step 1: Scan -----
        Log("スキャン開始...");
        progress?.Report(new(0, 0, "", "ファイルスキャン中..."));

        var files = await _scanner.ScanAsync(settings.InputDirectory, cancellationToken);
        Log($"スキャン完了: {files.Count} ファイル検出");

        if (files.Count == 0)
        {
            Log("対象ファイルが見つかりませんでした。");
            return new OrganizeResult { Log = log };
        }

        var assets = files.Select(f => new ImageAsset
        {
            SourcePath    = f.FullPath,
            FileSize      = f.FileSize,
            FileCreatedAt = f.CreatedAt,
            Status        = AssetStatus.Scanned,
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

        int withExif   = assets.Count(a => a.Exif != null);
        int withCamera = assets.Count(a => !string.IsNullOrWhiteSpace(a.Exif?.CameraName));
        int withGps    = assets.Count(a => a.Exif?.HasGps == true);
        Log($"メタデータ抽出完了: EXIF取得={withExif} カメラ={withCamera} GPS={withGps} EXIF無し={assets.Count - withExif}");

        // 先頭5件の診断ログ
        foreach (var asset in assets.Take(5))
        {
            var exif = asset.Exif;
            if (exif == null)
                Log($"  [診断] {asset.FileName}: EXIF=null");
            else
                Log($"  [診断] {asset.FileName}: Make={exif.Make ?? "(null)"} / Model={exif.Model ?? "(null)"} / City={exif.GeolocationCity ?? "(null)"} / Date={exif.EffectiveDate?.ToString("yyyy-MM-dd") ?? "(null)"}");
        }

        // ----- Step 3: 日付フィルタ -----
        int filteredOut = 0;
        if (settings.FilterStartDate.HasValue || settings.FilterEndDate.HasValue)
        {
            var start = settings.FilterStartDate ?? DateTime.MinValue;
            var end   = (settings.FilterEndDate ?? DateTime.MaxValue).AddDays(1).AddTicks(-1);
            foreach (var asset in assets)
            {
                var d = asset.Exif?.EffectiveDate ?? asset.FileCreatedAt;
                if (d < start || d > end)
                {
                    asset.Status = AssetStatus.Error;
                    asset.ErrorMessage = "日付フィルタにより除外";
                    filteredOut++;
                }
            }
            if (filteredOut > 0)
                Log($"日付フィルタ: {filteredOut} 件を除外（{start:yyyy/MM/dd}〜{end:yyyy/MM/dd}）");
        }

        // ----- Step 4: Normalize -----
        Log("名称正規化中...");
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            asset.NormalizedCamera = _normalizer.NormalizeCamera(asset.Exif?.CameraName);
            asset.NormalizedLens   = _normalizer.NormalizeLens(asset.Exif?.LensModel);
        }

        // ----- Step 5: Resolve output path -----
        Log("出力パス計算中...");
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.Status == AssetStatus.Error) continue;
            asset.OutputPath = _pathResolver.Resolve(asset, settings.OutputDirectory, settings.FolderTemplate);
        }

        // ----- Step 6: Duplicate detection -----
        Log("重複検出中...");
        var targetForDup = assets.Where(a => a.Status != AssetStatus.Error).ToList();
        var progressReporter = new Progress<string>(msg =>
            progress?.Report(new(assets.Count, processed, "", msg)));
        await _duplicateDetector.DetectDuplicatesAsync(targetForDup, progressReporter, cancellationToken);
        int duplicateCount = assets.Count(a => a.Status == AssetStatus.Duplicate);
        Log($"重複検出完了: {duplicateCount} 件の重複");

        // 重複グループをまとめる
        var dupGroups = assets
            .Where(a => a.DuplicateGroupId != null)
            .GroupBy(a => a.DuplicateGroupId!)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(a => a.SourcePath).ToList());

        // ----- Step 7: 統計収集 -----
        var activeAssets = assets.Where(a => a.ErrorMessage != "日付フィルタにより除外").ToList();
        var stats = BuildStatistics(activeAssets, filteredOut);

        // ----- Step 8: Execute file operations -----
        if (settings.Mode == OperationMode.DryRun)
        {
            Log("DRY_RUN モード: 実際のファイル操作はスキップ");
            foreach (var asset in assets.Where(a => a.Status != AssetStatus.Duplicate && a.Status != AssetStatus.Error))
                Log($"  [DRY_RUN] {asset.FileName} => {asset.OutputPath}");
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
                    if (!string.IsNullOrWhiteSpace(settings.TrashDirectory))
                    {
                        try
                        {
                            var trashPath = await _fileOperator.MoveToTrashAsync(
                                asset.SourcePath, settings.TrashDirectory, cancellationToken);
                            Log($"重複削除: {asset.SourcePath} => {trashPath}");
                        }
                        catch (Exception ex) { Log($"ゴミ箱移動エラー: {asset.SourcePath} - {ex.Message}"); }
                    }
                    continue;
                }

                if (asset.OutputPath == null) continue;

                try
                {
                    string actualOutput = settings.Mode == OperationMode.Copy
                        ? await _fileOperator.CopyAsync(asset.SourcePath, asset.OutputPath, cancellationToken)
                        : await _fileOperator.MoveAsync(asset.SourcePath, asset.OutputPath, cancellationToken);

                    asset.OutputPath  = actualOutput;
                    asset.Status      = AssetStatus.Processed;
                    asset.ProcessedAt = DateTime.Now;
                    Log($"完了: {asset.SourcePath} => {actualOutput}");
                }
                catch (Exception ex)
                {
                    asset.Status      = AssetStatus.Error;
                    asset.ErrorMessage = ex.Message;
                    Log($"操作エラー: {asset.SourcePath} - {ex.Message}");
                }

                processed++;
                progress?.Report(new(assets.Count, processed, asset.FileName, $"処理中: {asset.FileName}"));
            }
        }

        // ----- Step 9: Save state -----
        await _repository.SaveAllAsync(assets, cancellationToken);
        Log("処理完了");

        return new OrganizeResult
        {
            TotalScanned    = assets.Count,
            TotalProcessed  = assets.Count(a => a.Status == AssetStatus.Processed),
            TotalDuplicates = duplicateCount,
            TotalErrors     = assets.Count(a => a.Status == AssetStatus.Error),
            Log             = log,
            Statistics      = stats,
            DuplicateGroups = dupGroups,
        };
    }

    private static ScanStatistics BuildStatistics(IList<ImageAsset> assets, int filteredOut)
    {
        var jpegExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg" };
        var dates = assets
            .Select(a => a.Exif?.EffectiveDate ?? a.FileCreatedAt)
            .Where(d => d != default)
            .ToList();

        var cameraBreakdown = assets
            .Where(a => !string.IsNullOrWhiteSpace(a.NormalizedCamera) && a.NormalizedCamera != "Unknown")
            .GroupBy(a => a.NormalizedCamera!)
            .OrderByDescending(g => g.Count())
            .Select(g => (g.Key, g.Count()))
            .ToList();

        var extBreakdown = assets
            .GroupBy(a => a.Extension.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Select(g => (g.Key.TrimStart('.'), g.Count()))
            .ToList();

        var monthlyBreakdown = assets
            .Select(a => a.Exif?.EffectiveDate ?? a.FileCreatedAt)
            .Where(d => d != default)
            .GroupBy(d => d.ToString("yyyy/MM"))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count()))
            .ToList();

        return new ScanStatistics
        {
            TotalFiles  = assets.Count,
            JpegCount   = assets.Count(a => jpegExts.Contains(a.Extension)),
            RawCount    = assets.Count(a => !jpegExts.Contains(a.Extension)),
            WithExif    = assets.Count(a => a.Exif != null),
            WithCamera  = assets.Count(a => !string.IsNullOrWhiteSpace(a.Exif?.CameraName)),
            WithGps     = assets.Count(a => a.Exif?.HasGps == true),
            Filtered    = filteredOut,
            EarliestShot = dates.Count > 0 ? dates.Min() : null,
            LatestShot   = dates.Count > 0 ? dates.Max() : null,
            CameraBreakdown  = cameraBreakdown,
            ExtBreakdown     = extBreakdown,
            MonthlyBreakdown = monthlyBreakdown,
        };
    }
}
