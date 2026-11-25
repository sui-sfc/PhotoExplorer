using System.Collections.Concurrent;
using System.IO.Enumeration;
using PhotoExplorer.Domain;
using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Infrastructure;

public sealed record ScannedFile(
    string FullPath,
    long FileSize,
    DateTime CreatedAt,
    DateTime ModifiedAt,
    string? SidecarPath = null);

public sealed record ScanOptions(
    bool IncludeVideos = false,
    IReadOnlyCollection<string>? ExcludedDirectories = null);

public interface IFileScanner
{
    Task<IReadOnlyList<ScannedFile>> ScanAsync(
        string rootDirectory,
        ScanOptions options,
        IProgress<int>? foundCount = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 対応拡張子のファイルを再帰的に列挙する。
/// 1 ディレクトリ 1 回のシステムコール（FileSystemEnumerable）＋サブフォルダ並列探索で高速化する。
/// </summary>
public sealed class FileScanner : IFileScanner
{
    private readonly record struct DirEntry(
        string FullPath,
        string Name,
        bool IsDirectory,
        long Length,
        DateTime CreatedAt,
        DateTime ModifiedAt);

    public Task<IReadOnlyList<ScannedFile>> ScanAsync(
        string rootDirectory,
        ScanOptions options,
        IProgress<int>? foundCount = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"入力フォルダが見つかりません: {rootDirectory}");

        // 大きなフォルダでも UI が固まらないようスレッドプールで実行する
        return Task.Run<IReadOnlyList<ScannedFile>>(() =>
        {
            var excluded = new HashSet<string>(
                (options.ExcludedDirectories ?? [])
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(PathUtil.NormalizeDirectory),
                PathUtil.Comparer);

            var rootPath = PathUtil.NormalizeDirectory(rootDirectory);
            if (excluded.Contains(rootPath)) return [];

            var results = new ConcurrentBag<ScannedFile>();
            var pending = new ConcurrentQueue<(string Path, bool IsRoot)>();
            pending.Enqueue((rootPath, true));

            int activeWorkers = 0;
            int found = 0;
            int maxWorkers = Math.Clamp(Environment.ProcessorCount, 2, 8);
            using var signal = new ManualResetEventSlim(false);
            Exception? fatal = null;

            void Worker()
            {
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!pending.TryDequeue(out var item))
                        {
                            if (Volatile.Read(ref activeWorkers) == 0 && pending.IsEmpty)
                                break;
                            signal.Wait(10, cancellationToken);
                            continue;
                        }

                        Interlocked.Increment(ref activeWorkers);
                        try
                        {
                            int added = ScanOneDirectory(item.Path, options, excluded, results, pending, cancellationToken);
                            if (added > 0)
                            {
                                var total = Interlocked.Add(ref found, added);
                                foundCount?.Report(total);
                            }
                            if (!pending.IsEmpty) signal.Set();
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref activeWorkers) == 0 && pending.IsEmpty)
                                signal.Set();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref fatal, ex, null);
                    signal.Set();
                }
            }

            // まずルートを 1 回スキャンし、サブフォルダがあれば並列ワーカーを起動する
            if (pending.TryDequeue(out var root))
            {
                int added = ScanOneDirectory(root.Path, options, excluded, results, pending, cancellationToken);
                if (added > 0)
                {
                    found = added;
                    foundCount?.Report(found);
                }
            }

            if (!pending.IsEmpty)
            {
                int workerCount = Math.Min(maxWorkers, Math.Max(1, pending.Count));
                if (workerCount == 1)
                {
                    Worker();
                }
                else
                {
                    var tasks = Enumerable.Range(0, workerCount).Select(_ => Task.Run(Worker, CancellationToken.None)).ToArray();
                    Task.WaitAll(tasks);
                }
            }

            if (fatal != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fatal);

            var sorted = results.ToList();
            sorted.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
            return sorted;
        }, cancellationToken);
    }

    private static int ScanOneDirectory(
        string dirPath,
        ScanOptions options,
        HashSet<string> excluded,
        ConcurrentBag<ScannedFile> results,
        ConcurrentQueue<(string Path, bool IsRoot)> pending,
        CancellationToken cancellationToken)
    {
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        Dictionary<string, string>? xmpByName = null;
        var media = new List<DirEntry>();

        try
        {
            var enumerable = new FileSystemEnumerable<DirEntry>(
                dirPath,
                (ref FileSystemEntry entry) => new DirEntry(
                    entry.ToFullPath(),
                    entry.FileName.ToString(),
                    entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.Length,
                    entry.CreationTimeUtc.LocalDateTime,
                    entry.LastWriteTimeUtc.LocalDateTime),
                enumOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return true;
                    var ext = Path.GetExtension(entry.FileName);
                    if (ext.Equals(".xmp", StringComparison.OrdinalIgnoreCase)) return true;
                    return MediaFormats.IsSupportedExtension(ext, options.IncludeVideos);
                },
            };

            foreach (var entry in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    var norm = PathUtil.NormalizeDirectory(entry.FullPath);
                    if (!excluded.Contains(norm))
                        pending.Enqueue((norm, false));
                }
                else if (entry.Name.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase))
                {
                    xmpByName ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    xmpByName.TryAdd(entry.Name, entry.FullPath);
                }
                else
                {
                    media.Add(entry);
                }
            }
        }
        catch (UnauthorizedAccessException) { return 0; }
        catch (IOException) { return 0; }

        if (media.Count == 0) return 0;

        Dictionary<string, string>? sidecars = null;
        if (xmpByName != null)
        {
            // "IMG_1.xmp" は同名の RAW（なければ他の形式）に、"IMG_1.CR3.xmp" はそのファイルに紐づける
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = media
                .OrderBy(f => MediaFormats.GetKind(f.Name) == MediaKind.Raw ? 0 : 1)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

            sidecars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in ordered)
            {
                if (xmpByName.TryGetValue(f.Name + ".xmp", out var exact) && claimed.Add(exact))
                {
                    sidecars[f.FullPath] = exact;
                    continue;
                }
                var baseXmp = Path.GetFileNameWithoutExtension(f.Name) + ".xmp";
                if (xmpByName.TryGetValue(baseXmp, out var byBase) && claimed.Add(byBase))
                    sidecars[f.FullPath] = byBase;
            }
        }

        foreach (var f in media)
        {
            results.Add(new ScannedFile(
                f.FullPath, f.Length, f.CreatedAt, f.ModifiedAt,
                sidecars?.GetValueOrDefault(f.FullPath)));
        }

        return media.Count;
    }
}
