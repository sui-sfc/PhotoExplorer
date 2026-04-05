using System.IO;

namespace PhotoExplorer.Infrastructure;

/// <summary>対応拡張子のファイルを再帰スキャンする実装</summary>
public sealed class FileScanner : IFileScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg",          // JPEG
        ".cr2", ".cr3",           // Canon RAW
        ".nef",                   // Nikon RAW
        ".arw",                   // Sony RAW
        ".dng",                   // Adobe DNG
    };

    public Task<IReadOnlyList<ScannedFile>> ScanAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"ディレクトリが見つかりません: {rootDirectory}");

        var results = new List<ScannedFile>();
        ScanDirectory(new DirectoryInfo(rootDirectory), results, cancellationToken);
        return Task.FromResult<IReadOnlyList<ScannedFile>>(results);
    }

    private static void ScanDirectory(
        DirectoryInfo dir,
        List<ScannedFile> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 隠しディレクトリをスキップ
        if (dir.Attributes.HasFlag(FileAttributes.Hidden)) return;
        if (dir.Attributes.HasFlag(FileAttributes.System)) return;

        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 隠し・システム・シンボリックリンクをスキップ
                if (file.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                if (file.Attributes.HasFlag(FileAttributes.System)) continue;
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                if (!SupportedExtensions.Contains(file.Extension)) continue;

                results.Add(new ScannedFile(
                    FullPath: file.FullName,
                    FileSize: file.Length,
                    CreatedAt: file.CreationTime));
            }

            foreach (var subDir in dir.EnumerateDirectories())
                ScanDirectory(subDir, results, cancellationToken);
        }
        catch (UnauthorizedAccessException) { /* アクセス不可のフォルダはスキップ */ }
        catch (IOException) { /* IO エラーはスキップ */ }
    }
}
