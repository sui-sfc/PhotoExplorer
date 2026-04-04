using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Infrastructure;

/// <summary>ファイルからEXIFメタデータを抽出する</summary>
public interface IMetadataExtractor
{
    Task<ExifData?> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
