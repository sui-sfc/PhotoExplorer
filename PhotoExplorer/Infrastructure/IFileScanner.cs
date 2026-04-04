namespace PhotoExplorer.Infrastructure;

public record ScannedFile(string FullPath, long FileSize, DateTime CreatedAt);

/// <summary>ファイルシステムを走査して対象ファイルを列挙する</summary>
public interface IFileScanner
{
    Task<IReadOnlyList<ScannedFile>> ScanAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default);
}
