using PhotoExplorer.Domain.Entities;

namespace PhotoExplorer.Infrastructure;

/// <summary>ImageAssetの永続化を行うリポジトリ</summary>
public interface IAssetRepository
{
    Task SaveAllAsync(IEnumerable<ImageAsset> assets, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImageAsset>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
