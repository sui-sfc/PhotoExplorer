using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Presentation.ViewModels;

public sealed class ImageAssetViewModel : ViewModelBase
{
    private readonly ImageAsset _asset;

    public ImageAssetViewModel(ImageAsset asset)
    {
        _asset = asset;
    }

    public string FileName => _asset.FileName;
    public string SourcePath => _asset.SourcePath;
    public string? OutputPath => _asset.OutputPath;
    public long FileSize => _asset.FileSize;
    public string FileSizeDisplay => FormatSize(_asset.FileSize);
    public AssetStatus Status => _asset.Status;
    public string StatusDisplay => _asset.Status switch
    {
        AssetStatus.Scanned => "スキャン済",
        AssetStatus.MetadataOk => "EXIF完全",
        AssetStatus.PartialMetadata => "EXIF一部",
        AssetStatus.NoMetadata => "EXIFなし",
        AssetStatus.Duplicate => "重複",
        AssetStatus.Processed => "完了",
        AssetStatus.Error => "エラー",
        _ => _asset.Status.ToString()
    };
    public string? ErrorMessage => _asset.ErrorMessage;
    public string? CameraName => _asset.NormalizedCamera;
    public string? LensName => _asset.NormalizedLens;
    public DateTime? ShotAt => _asset.Exif?.EffectiveDate;
    public string ShotAtDisplay => ShotAt?.ToString("yyyy/MM/dd HH:mm:ss") ?? "(不明)";
    public bool IsDuplicate => _asset.Status == AssetStatus.Duplicate;
    public string? DuplicateGroupId => _asset.DuplicateGroupId;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(IsDuplicate));
    }
}
