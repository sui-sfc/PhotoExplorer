using System.Diagnostics;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Application;

/// <summary>
/// 写真整理のメイン処理:
/// スキャン → メタデータ読み取り → 日付フィルタ → 名前の正規化 → 出力先決定 → 重複検出 → コピー/移動 → 保存
/// </summary>
public sealed class OrganizeUseCase : IOrganizeUseCase
{
    private const int MaxPerFileLogLines = 300;
    private const string DryRunOutputPlaceholder = "<出力フォルダ>";

    private readonly IFileScanner _scanner;
    private readonly IMetadataExtractor _metadataExtractor;
    private readonly INormalizer _normalizer;
    private readonly IPathResolver _pathResolver;
    private readonly IDuplicateDetector _duplicateDetector;
    private readonly IFileOperator _fileOperator;
    private readonly IAssetRepository _repository;
    private readonly OperationJournal _journal;
    private readonly StatsHistoryRepository _history;
    private readonly MappingConfigProvider? _mappings;

    public OrganizeUseCase(
        IFileScanner scanner,
        IMetadataExtractor metadataExtractor,
        INormalizer normalizer,
        IPathResolver pathResolver,
        IDuplicateDetector duplicateDetector,
        IFileOperator fileOperator,
        IAssetRepository repository,
        OperationJournal journal,
        StatsHistoryRepository history,
        MappingConfigProvider? mappings = null)
    {
        _scanner = scanner;
        _metadataExtractor = metadataExtractor;
        _normalizer = normalizer;
        _pathResolver = pathResolver;
        _duplicateDetector = duplicateDetector;
        _fileOperator = fileOperator;
        _repository = repository;
        _journal = journal;
        _history = history;
        _mappings = mappings;
    }

    public Task<OrganizeResult> ExecuteAsync(
        AppSettings settings,
        IProgress<OrganizeProgress>? progress = null,
        IProgress<LogEntry>? log = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RunAsync(settings.Clone(), progress, log, cancellationToken), CancellationToken.None);

    private sealed class RunContext
    {
        public required AppSettings Settings { get; init; }
        public required IProgress<OrganizeProgress>? Progress { get; init; }
        public required IProgress<LogEntry>? LiveLog { get; init; }
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public List<LogEntry> Log { get; } = [];
        public List<ImageAsset> Assets { get; set; } = [];
        public int PerFileLines;
        private long _lastReport;

        public void Add(LogEntry e)
        {
            if (e.Level == LogLevel.Debug && !Settings.VerboseLog) return;
            lock (Log) Log.Add(e);
            LiveLog?.Report(e);
        }

        /// <summary>1ファイルごとのログ。大量になる場合は上限で打ち切る（詳細ログ時は全件）</summary>
        public void PerFile(LogEntry e)
        {
            if (e.Level >= LogLevel.Warning || Settings.VerboseLog || Interlocked.Increment(ref PerFileLines) <= MaxPerFileLogLines)
            {
                Add(e);
                return;
            }
            if (PerFileLines == MaxPerFileLogLines + 1)
                Add(LogEntry.Info($"…以降のファイルごとのログは省略します（全件は「ファイル一覧」タブと CSV 出力で確認できます）"));
        }

        public void Report(RunPhase phase, int total, int done, string file, string message, bool force = false)
        {
            if (Progress == null) return;
            var now = Environment.TickCount64;
            if (!force && done != total && now - Interlocked.Read(ref _lastReport) < 60) return;
            Interlocked.Exchange(ref _lastReport, now);
            Progress.Report(new OrganizeProgress(phase, total, done, file, message));
        }
    }

    private async Task<OrganizeResult> RunAsync(
        AppSettings s,
        IProgress<OrganizeProgress>? progress,
        IProgress<LogEntry>? liveLog,
        CancellationToken ct)
    {
        var ctx = new RunContext { Settings = s, Progress = progress, LiveLog = liveLog };
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.Now;
        bool cancelled = false;
        string? exifVersion = null;
        int filteredOut = 0, trashed = 0;
        var dupGroups = new List<DuplicateGroup>();
        ScanStatistics? stats = null;

        s.MaxDegreeOfParallelism = Math.Clamp(s.MaxDegreeOfParallelism, 1, 32);
        bool writes = s.Mode != OperationMode.DryRun;
        var outputRoot = string.IsNullOrWhiteSpace(s.OutputDirectory) ? DryRunOutputPlaceholder : s.OutputDirectory;

        ctx.Add(LogEntry.Info($"開始: モード={ModeLabel(s.Mode)}  入力={s.InputDirectory}  出力={outputRoot}"));

        try
        {
            // ----- exiftool -----
            ctx.Report(RunPhase.Preparing, 0, 0, "", "準備中...", force: true);
            _metadataExtractor.ToolPath = ExifToolLocator.Resolve(s.ExifToolPath);
            exifVersion = _metadataExtractor.GetVersion();
            if (exifVersion == null)
                ctx.Add(LogEntry.Warn($"exiftool が見つかりません（{_metadataExtractor.ToolPath}）。撮影日の代わりにファイルの日時を使い、カメラ・レンズは Unknown になります。"));
            else
                ctx.Add(LogEntry.Info($"exiftool {exifVersion} を使用します（{_metadataExtractor.ToolPath}）"));

            if (_mappings?.LastError is { } mapErr)
                ctx.Add(LogEntry.Warn(mapErr));

            // ----- 1. スキャン -----
            ctx.Report(RunPhase.Scanning, 0, 0, "", "ファイルを探しています...", force: true);
            var excluded = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.OutputDirectory)) excluded.Add(s.OutputDirectory);
            if (!string.IsNullOrWhiteSpace(s.TrashDirectory)) excluded.Add(s.TrashDirectory);

            var scanProgress = new Progress<int>(n => ctx.Report(RunPhase.Scanning, 0, n, "", $"ファイルを探しています... {n:N0} 件"));
            var files = await _scanner.ScanAsync(s.InputDirectory, new ScanOptions(s.IncludeVideos, excluded), scanProgress, ct)
                .ConfigureAwait(false);

            int sidecarCount = files.Count(f => f.SidecarPath != null);
            ctx.Add(LogEntry.Info($"スキャン完了: {files.Count:N0} 件" + (sidecarCount > 0 ? $"（XMP サイドカー {sidecarCount:N0} 件）" : "")));

            ctx.Assets = files.Select(f => new ImageAsset
            {
                SourcePath     = f.FullPath,
                FileSize       = f.FileSize,
                FileCreatedAt  = f.CreatedAt,
                FileModifiedAt = f.ModifiedAt,
                SidecarPath    = s.IncludeSidecars ? f.SidecarPath : null,
                Status         = AssetStatus.Scanned,
            }).ToList();
            var assets = ctx.Assets;

            if (assets.Count == 0)
            {
                ctx.Add(LogEntry.Warn("対象のファイルが見つかりませんでした。"));
            }
            else
            {
                await ReadMetadataAsync(ctx, exifVersion != null, ct).ConfigureAwait(false);
                filteredOut = ApplyDateFilter(ctx);
                var active = assets.Where(a => a.Status != AssetStatus.Filtered).ToList();

                // ----- 名前の正規化・出力先の決定 -----
                ctx.Report(RunPhase.Analyzing, 0, 0, "", "出力先を計算しています...", force: true);
                var pathOptions = new PathOptions(outputRoot, s.FolderTemplate, s.FileNameTemplate, s.NoDateFolderName);
                foreach (var a in active)
                {
                    ct.ThrowIfCancellationRequested();
                    var e = a.Exif;
                    a.NormalizedCamera = _normalizer.NormalizeCamera(e?.CameraName, e?.CameraNameFull, e?.Model);
                    a.NormalizedLens   = _normalizer.NormalizeLens(e?.LensModel);
                    a.OutputPath       = _pathResolver.Resolve(a, pathOptions);
                }

                // ----- 重複検出 -----
                if (s.DetectDuplicates)
                {
                    var hashProgress = new Progress<(int Done, int Total)>(p =>
                        ctx.Report(RunPhase.Hashing, p.Total, p.Done, "", $"重複を確認しています... {p.Done:N0}/{p.Total:N0}"));
                    await _duplicateDetector.DetectDuplicatesAsync(active, s.MaxDegreeOfParallelism, hashProgress, ct)
                        .ConfigureAwait(false);

                    dupGroups = active
                        .Where(a => a.DuplicateGroupId != null)
                        .GroupBy(a => a.DuplicateGroupId!)
                        .Select(g => new DuplicateGroup(g.Key, g.OrderByDescending(a => a.IsKeptInDuplicateGroup).ToList()))
                        .OrderByDescending(g => g.Files.Count).ThenBy(g => g.Files[0].SourcePath)
                        .ToList();
                    int dupCount = active.Count(a => a.Status == AssetStatus.Duplicate);
                    ctx.Add(dupCount > 0
                        ? LogEntry.Warn($"重複: {dupGroups.Count:N0} グループ / 余分なファイル {dupCount:N0} 件" +
                                        (string.IsNullOrWhiteSpace(s.TrashDirectory) ? "（退避先が未設定のため元の場所に残します）" : $"（{s.TrashDirectory} へ退避します）"))
                        : LogEntry.Info("重複ファイルはありませんでした。"));
                }

                WarnAboutNameCollisions(ctx, active);
                stats = ScanStatistics.Build(active, filteredOut);

                // ----- 実行 -----
                if (!writes)
                    LogDryRun(ctx, active);
                else
                    trashed = await TransferAsync(ctx, active, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            ctx.Add(LogEntry.Warn(writes
                ? "中断しました。ここまでに行ったコピー・移動は「統計」タブの履歴から元に戻せます。"
                : "中断しました。"));
        }

        // ----- 保存（中断時も行う） -----
        ctx.Report(RunPhase.Saving, 0, 0, "", "結果を保存しています...", force: true);
        var all = ctx.Assets;
        stats ??= ScanStatistics.Build(all.Where(a => a.Status != AssetStatus.Filtered).ToList(), filteredOut);

        try { await _repository.SaveAllAsync(all, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { ctx.Add(LogEntry.Warn($"処理状態の保存に失敗しました: {ex.Message}")); }

        int processed = all.Count(a => a.Status == AssetStatus.Processed);
        if (!writes) processed = all.Count(a => a.Status is AssetStatus.MetadataOk or AssetStatus.PartialMetadata or AssetStatus.NoMetadata);
        int skipped = all.Count(a => a.Status == AssetStatus.Skipped);
        int duplicates = all.Count(a => a.Status == AssetStatus.Duplicate);
        int errors = all.Count(a => a.Status == AssetStatus.Error);

        sw.Stop();
        try
        {
            _history.SaveRun(new RunSummary(ctx.RunId, startedAt, s.Mode, s.InputDirectory, s.OutputDirectory,
                stats, processed, skipped, duplicates, errors, sw.Elapsed.TotalSeconds, cancelled));
        }
        catch (Exception ex) { ctx.Add(LogEntry.Warn($"実行履歴の保存に失敗しました: {ex.Message}")); }

        var summary = writes
            ? $"{(cancelled ? "中断" : "完了")}: {ModeLabel(s.Mode)} {processed:N0} 件 / スキップ {skipped:N0} / 重複 {duplicates:N0}（退避 {trashed:N0}）/ 除外 {filteredOut:N0} / エラー {errors:N0}  （{FormatElapsed(sw.Elapsed)}）"
            : $"{(cancelled ? "中断" : "完了")}: DRY_RUN — 整理予定 {processed:N0} 件 / 重複 {duplicates:N0} / 除外 {filteredOut:N0} / エラー {errors:N0}  （{FormatElapsed(sw.Elapsed)}）";
        ctx.Add(errors > 0 ? LogEntry.Warn(summary) : LogEntry.Ok(summary));
        ctx.Report(RunPhase.Completed, 1, 1, "", summary, force: true);

        return new OrganizeResult
        {
            RunId           = ctx.RunId,
            Mode            = s.Mode,
            Cancelled       = cancelled,
            Elapsed         = sw.Elapsed,
            ExifToolVersion = exifVersion,
            TotalScanned    = all.Count,
            TotalProcessed  = processed,
            TotalSkipped    = skipped,
            TotalDuplicates = duplicates,
            TotalFiltered   = filteredOut,
            TotalErrors     = errors,
            Assets          = all,
            Log             = ctx.Log.ToList(),
            Statistics      = stats,
            DuplicateGroups = dupGroups,
        };
    }

    private async Task ReadMetadataAsync(RunContext ctx, bool exifToolAvailable, CancellationToken ct)
    {
        var s = ctx.Settings;
        var assets = ctx.Assets;
        int cacheHits = 0;

        if (s.UseMetadataCache)
        {
            ctx.Report(RunPhase.ReadingMetadata, 0, 0, "", "前回の読み取り結果を確認しています...", force: true);
            try
            {
                var cached = await _repository.LoadByPathsAsync(assets.Select(a => a.SourcePath), ct).ConfigureAwait(false);
                foreach (var a in assets)
                {
                    if (!cached.TryGetValue(a.SourcePath, out var c)) continue;
                    // サイズと更新日時が同じ = 中身が変わっていないとみなす
                    if (c.FileSize != a.FileSize || c.FileModifiedAt != a.FileModifiedAt) continue;
                    a.PartialHash = c.PartialHash;
                    a.FullHash = c.FullHash;
                    if (c.MetadataExtracted)
                    {
                        a.Exif = c.Exif;
                        a.MetadataExtracted = true;
                        cacheHits++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Add(LogEntry.Warn($"前回の読み取り結果を利用できませんでした: {ex.Message}"));
            }
        }

        var needs = assets.Where(a => !a.MetadataExtracted).ToList();
        if (cacheHits > 0)
            ctx.Add(LogEntry.Info($"メタデータ: 前回の結果を再利用 {cacheHits:N0} 件 / 新たに読み取り {needs.Count:N0} 件"));

        if (exifToolAvailable && needs.Count > 0)
        {
            ctx.Report(RunPhase.ReadingMetadata, needs.Count, 0, "", "メタデータを読み取っています...", force: true);
            var readProgress = new Progress<int>(n =>
                ctx.Report(RunPhase.ReadingMetadata, needs.Count, n, "", $"メタデータを読み取っています... {n:N0}/{needs.Count:N0}"));

            var results = await _metadataExtractor.ExtractAsync(
                needs.Select(a => a.SourcePath).ToList(), s.MaxDegreeOfParallelism, readProgress, ct).ConfigureAwait(false);

            int failures = 0;
            foreach (var a in needs)
            {
                if (!results.TryGetValue(a.SourcePath, out var r)) continue;
                if (r.Data != null)
                {
                    a.Exif = r.Data;
                    a.MetadataExtracted = true;
                }
                else if (r.Error != null)
                {
                    failures++;
                    a.ErrorMessage = $"メタデータ読み取り失敗: {r.Error}";
                    if (failures <= 20) ctx.Add(LogEntry.Warn($"メタデータを読み取れません: {a.FileName} - {r.Error}"));
                }
            }
            if (failures > 20) ctx.Add(LogEntry.Warn($"…ほか {failures - 20:N0} 件のファイルでメタデータを読み取れませんでした。"));
        }

        foreach (var a in assets)
        {
            a.Status = a.HasExifDate ? AssetStatus.MetadataOk
                : a.Exif != null ? AssetStatus.PartialMetadata
                : AssetStatus.NoMetadata;
        }

        int withDate = assets.Count(a => a.HasExifDate);
        int noDate = assets.Count - withDate;
        int withCamera = assets.Count(a => a.Exif?.Model != null);
        int withGps = assets.Count(a => a.Exif?.HasGps == true);
        ctx.Add(LogEntry.Info($"メタデータ: 撮影日あり {withDate:N0} / なし {noDate:N0} / カメラ名あり {withCamera:N0} / GPS あり {withGps:N0}"));
        if (noDate > 0)
        {
            ctx.Add(LogEntry.Info(string.IsNullOrWhiteSpace(s.NoDateFolderName)
                ? $"撮影日がない {noDate:N0} 件はファイルの日時でフォルダ分けします。"
                : $"撮影日がない {noDate:N0} 件は「{s.NoDateFolderName}」フォルダにまとめます。"));
        }

        foreach (var a in assets.Take(s.VerboseLog ? int.MaxValue : 0))
        {
            var e = a.Exif;
            ctx.Add(LogEntry.Debug(e == null
                ? $"{a.FileName}: メタデータなし"
                : $"{a.FileName}: Make={e.Make ?? "-"} Model={e.Model ?? "-"} Lens={e.LensModel ?? "-"} " +
                  $"日時={e.EffectiveDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}({e.UsedDateField ?? "-"}) " +
                  $"GPS={(e.HasGps ? $"{e.GPSLatitude:F5},{e.GPSLongitude:F5}" : "-")} 都市={e.GeolocationCity ?? "-"}"));
        }
    }

    private static int ApplyDateFilter(RunContext ctx)
    {
        var s = ctx.Settings;
        if (!s.FilterStartDate.HasValue && !s.FilterEndDate.HasValue) return 0;

        var start = s.FilterStartDate?.Date ?? DateTime.MinValue;
        var end = s.FilterEndDate.HasValue ? s.FilterEndDate.Value.Date.AddDays(1).AddTicks(-1) : DateTime.MaxValue;
        int filtered = 0;
        foreach (var a in ctx.Assets)
        {
            var d = a.BestDate;
            if (d < start || d > end)
            {
                a.Status = AssetStatus.Filtered;
                a.ErrorMessage = null;
                filtered++;
            }
        }
        var range = $"{(s.FilterStartDate.HasValue ? start.ToString("yyyy/MM/dd") : "")}〜{(s.FilterEndDate.HasValue ? end.ToString("yyyy/MM/dd") : "")}";
        ctx.Add(LogEntry.Info($"日付フィルタ {range}: 対象 {ctx.Assets.Count - filtered:N0} 件 / 対象外 {filtered:N0} 件"));
        return filtered;
    }

    private static void WarnAboutNameCollisions(RunContext ctx, List<ImageAsset> active)
    {
        var collisions = active
            .Where(a => a.Status != AssetStatus.Duplicate && a.OutputPath != null)
            .GroupBy(a => a.OutputPath!, PathUtil.Comparer)
            .Where(g => g.Count() > 1)
            .ToList();
        if (collisions.Count == 0) return;

        int files = collisions.Sum(g => g.Count() - 1);
        ctx.Add(LogEntry.Warn($"同じ出力先になるファイルが {files:N0} 件あります（中身が違うため、_001 のような連番を付けて保存します）。"));
        foreach (var g in collisions.Take(5))
            ctx.Add(LogEntry.Info($"  例: {Path.GetFileName(g.Key)} ← {string.Join(", ", g.Select(a => a.SourcePath).Take(3))}"));
    }

    private static void LogDryRun(RunContext ctx, List<ImageAsset> active)
    {
        var s = ctx.Settings;
        ctx.Add(LogEntry.Info("DRY_RUN: ファイルは一切変更しません。以下は実行した場合の予定です。"));
        foreach (var a in active)
        {
            if (a.Status == AssetStatus.Error) continue;
            if (a.Status == AssetStatus.Duplicate)
            {
                var dest = string.IsNullOrWhiteSpace(s.TrashDirectory) ? "そのまま残す" : $"{s.TrashDirectory} へ退避";
                ctx.PerFile(LogEntry.Info($"[予定] 重複: {a.SourcePath} → {dest}"));
                continue;
            }
            ctx.PerFile(LogEntry.Info($"[予定] {a.SourcePath} → {a.OutputPath}"));
        }
    }

    private async Task<int> TransferAsync(RunContext ctx, List<ImageAsset> active, CancellationToken ct)
    {
        var s = ctx.Settings;
        bool move = s.Mode == OperationMode.Move;
        var kind = move ? OperationKind.Move : OperationKind.Copy;
        var targets = active.Where(a => a.Status != AssetStatus.Error).ToList();
        int done = 0, trashed = 0;

        ctx.Add(LogEntry.Info($"{ModeLabel(s.Mode)} を開始します（{targets.Count:N0} 件、コピー検証: {(s.VerifyCopies ? "あり" : "なし")}）"));

        foreach (var a in targets)
        {
            ct.ThrowIfCancellationRequested();
            ctx.Report(RunPhase.Transferring, targets.Count, done, a.FileName, $"{ModeLabel(s.Mode)}中... {done:N0}/{targets.Count:N0}");

            try
            {
                if (a.Status == AssetStatus.Duplicate)
                {
                    if (!string.IsNullOrWhiteSpace(s.TrashDirectory))
                    {
                        var r = await _fileOperator.MoveToTrashAsync(a.SourcePath, s.TrashDirectory, ct).ConfigureAwait(false);
                        _journal.Record(ctx.RunId, OperationKind.Trash, a.SourcePath, r.Path, r.Hash, a.FileSize);
                        await MoveSidecarToTrashAsync(ctx, a, ct).ConfigureAwait(false);
                        a.OutputPath = r.Path;
                        trashed++;
                        ctx.PerFile(LogEntry.Info($"重複を退避: {a.SourcePath} → {r.Path}"));
                    }
                    else
                    {
                        a.OutputPath = null;
                        ctx.PerFile(LogEntry.Info($"重複のため残しました: {a.SourcePath}"));
                    }
                }
                else
                {
                    var r = move
                        ? await _fileOperator.MoveAsync(a.SourcePath, a.OutputPath!, s.VerifyCopies, a.FullHash, ct).ConfigureAwait(false)
                        : await _fileOperator.CopyAsync(a.SourcePath, a.OutputPath!, s.VerifyCopies, a.FullHash, ct).ConfigureAwait(false);

                    a.FullHash ??= r.Hash;
                    if (r.Outcome == FileOpOutcome.AlreadyExists)
                    {
                        a.Status = AssetStatus.Skipped;
                        a.OutputPath = r.Path;
                        ctx.PerFile(LogEntry.Info($"スキップ（出力先に同じファイルがあります）: {a.SourcePath}"));
                    }
                    else
                    {
                        _journal.Record(ctx.RunId, kind, a.SourcePath, r.Path, r.Hash, a.FileSize);
                        a.OutputPath = r.Path;
                        a.Status = AssetStatus.Processed;
                        a.ProcessedAt = DateTime.Now;
                        await TransferSidecarAsync(ctx, a, r.Path, move, kind, ct).ConfigureAwait(false);
                        ctx.PerFile(LogEntry.Ok($"{a.SourcePath} → {r.Path}"));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                a.Status = AssetStatus.Error;
                a.ErrorMessage = ex.Message;
                ctx.Add(LogEntry.Error($"{ModeLabel(s.Mode)}できませんでした: {a.SourcePath} - {ex.Message}"));
            }

            done++;
        }
        ctx.Report(RunPhase.Transferring, targets.Count, done, "", $"{ModeLabel(s.Mode)} 完了", force: true);
        return trashed;
    }

    private async Task TransferSidecarAsync(RunContext ctx, ImageAsset a, string mainDest, bool move,
        OperationKind kind, CancellationToken ct)
    {
        if (a.SidecarPath == null || !File.Exists(a.SidecarPath)) return;
        try
        {
            var dest = SidecarDestination(a.SidecarPath, a.SourcePath, mainDest);
            var size = new FileInfo(a.SidecarPath).Length;
            var r = move
                ? await _fileOperator.MoveAsync(a.SidecarPath, dest, verify: true, null, ct).ConfigureAwait(false)
                : await _fileOperator.CopyAsync(a.SidecarPath, dest, verify: true, null, ct).ConfigureAwait(false);
            if (r.Outcome == FileOpOutcome.Done)
                _journal.Record(ctx.RunId, kind, a.SidecarPath, r.Path, r.Hash, size);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.Add(LogEntry.Warn($"サイドカーを{(move ? "移動" : "コピー")}できませんでした: {a.SidecarPath} - {ex.Message}"));
        }
    }

    private async Task MoveSidecarToTrashAsync(RunContext ctx, ImageAsset a, CancellationToken ct)
    {
        if (a.SidecarPath == null || !File.Exists(a.SidecarPath)) return;
        try
        {
            var size = new FileInfo(a.SidecarPath).Length;
            var r = await _fileOperator.MoveToTrashAsync(a.SidecarPath, ctx.Settings.TrashDirectory, ct).ConfigureAwait(false);
            _journal.Record(ctx.RunId, OperationKind.Trash, a.SidecarPath, r.Path, r.Hash, size);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.Add(LogEntry.Warn($"サイドカーを退避できませんでした: {a.SidecarPath} - {ex.Message}"));
        }
    }

    /// <summary>本体の出力先名に合わせたサイドカーのパス（IMG_1.xmp / IMG_1.CR3.xmp の両形式に対応）</summary>
    internal static string SidecarDestination(string sidecarPath, string sourcePath, string mainDest)
    {
        var sidecarName = Path.GetFileName(sidecarPath);
        var sidecarExt = Path.GetExtension(sidecarName);
        if (sidecarName.Equals(Path.GetFileName(sourcePath) + sidecarExt, StringComparison.OrdinalIgnoreCase))
            return mainDest + sidecarExt;
        return Path.ChangeExtension(mainDest, sidecarExt);
    }

    public static string ModeLabel(OperationMode mode) => mode switch
    {
        OperationMode.DryRun => "DRY_RUN",
        OperationMode.Copy   => "コピー",
        OperationMode.Move   => "移動",
        _ => mode.ToString(),
    };

    public static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}時間{t.Minutes}分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}分{t.Seconds}秒"
        : $"{t.TotalSeconds:F1}秒";
}
