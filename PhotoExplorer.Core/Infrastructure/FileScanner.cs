using PhotoExplorer.Domain;

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

/// <summary>対応拡張子のファイルを再帰的に列挙する</summary>
public sealed class FileScanner : IFileScanner
{
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
            var excluded = (options.ExcludedDirectories ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(PathUtil.NormalizeDirectory)
                .ToList();

            var results = new List<ScannedFile>();
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(rootDirectory));
            bool isRoot = true;

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = pending.Pop();

                if (!isRoot)
                {
                    try
                    {
                        var attr = dir.Attributes;
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                            continue;
                    }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                }
                isRoot = false;

                var dirPath = PathUtil.NormalizeDirectory(dir.FullName);
                if (excluded.Any(e => PathUtil.PathEquals(e, dirPath))) continue;

                ScanOneDirectory(dir, options, results, pending, cancellationToken);
                foundCount?.Report(results.Count);
            }

            results.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
            return results;
        }, cancellationToken);
    }

    private static void ScanOneDirectory(
        DirectoryInfo dir,
        ScanOptions options,
        List<ScannedFile> results,
        Stack<DirectoryInfo> pending,
        CancellationToken cancellationToken)
    {
        FileInfo[] files;
        try { files = dir.GetFiles(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        var xmpByName = files
            .Where(f => f.Extension.Equals(".xmp", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FullName, StringComparer.OrdinalIgnoreCase);

        var media = new List<FileInfo>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                    continue;
            }
            catch (IOException) { continue; }
            if (!MediaFormats.IsSupported(file.Name, options.IncludeVideos)) continue;
            media.Add(file);
        }

        // "IMG_1.xmp" は同名の RAW（なければ他の形式）に、"IMG_1.CR3.xmp" はそのファイルに紐づける
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = media
            .OrderBy(f => MediaFormats.GetKind(f.Name) == Domain.Enums.MediaKind.Raw ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var sidecars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ordered)
        {
            if (xmpByName.TryGetValue(f.Name + ".xmp", out var exact) && claimed.Add(exact))
            {
                sidecars[f.FullName] = exact;
                continue;
            }
            var baseXmp = Path.GetFileNameWithoutExtension(f.Name) + ".xmp";
            if (xmpByName.TryGetValue(baseXmp, out var byBase) && claimed.Add(byBase))
                sidecars[f.FullName] = byBase;
        }

        foreach (var f in media)
        {
            try
            {
                results.Add(new ScannedFile(
                    f.FullName, f.Length, f.CreationTime, f.LastWriteTime,
                    sidecars.GetValueOrDefault(f.FullName)));
            }
            catch (IOException) { }
        }

        DirectoryInfo[] subDirs;
        try { subDirs = dir.GetDirectories(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var sub in subDirs.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
            pending.Push(sub);
    }
}
