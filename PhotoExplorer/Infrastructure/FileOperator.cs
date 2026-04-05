using System.IO;

namespace PhotoExplorer.Infrastructure;

/// <summary>実際のファイル操作を行う実装（上書き禁止・衝突時リネーム）</summary>
public sealed class FileOperator : IFileOperator
{
    public Task<string> CopyAsync(
        string sourcePath, string destPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actual = ResolveDestination(destPath);
        Directory.CreateDirectory(Path.GetDirectoryName(actual)!);
        File.Copy(sourcePath, actual, overwrite: false);
        return Task.FromResult(actual);
    }

    public Task<string> MoveAsync(
        string sourcePath, string destPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actual = ResolveDestination(destPath);
        Directory.CreateDirectory(Path.GetDirectoryName(actual)!);
        File.Move(sourcePath, actual, overwrite: false);
        return Task.FromResult(actual);
    }

    public Task<string> MoveToTrashAsync(
        string sourcePath, string trashDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(trashDirectory, fileName);
        var actual = ResolveDestination(destPath);
        Directory.CreateDirectory(trashDirectory);
        File.Move(sourcePath, actual, overwrite: false);
        return Task.FromResult(actual);
    }

    /// <summary>同名ファイルが存在する場合は連番サフィックスを付ける</summary>
    private static string ResolveDestination(string destPath)
    {
        if (!File.Exists(destPath)) return destPath;

        var dir = Path.GetDirectoryName(destPath)!;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(destPath);
        var ext = Path.GetExtension(destPath);
        int counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt}_{counter:D3}{ext}");
            counter++;
        } while (File.Exists(candidate));
        return candidate;
    }
}
