using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Presentation.Converters;

namespace PhotoExplorer.Presentation.ViewModels;

/// <summary>「ファイル一覧」タブの1行</summary>
public sealed class ImageAssetViewModel
{
    public ImageAssetViewModel(ImageAsset asset) => Asset = asset;

    public ImageAsset Asset { get; }

    public string FileName => Asset.FileName;
    public string SourcePath => Asset.SourcePath;
    public string? OutputPath => Asset.OutputPath;
    public long FileSize => Asset.FileSize;
    public string FileSizeText => FileSizeConverter.Format(Asset.FileSize);
    public AssetStatus Status => Asset.Status;
    public string StatusDisplay => StatusText(Asset.Status);
    public string? Message => Asset.ErrorMessage;
    public string Camera => Asset.NormalizedCamera ?? "";
    public string Lens => Asset.NormalizedLens ?? "";
    public DateTime ShotAt => Asset.BestDate;
    public string ShotAtDisplay => Asset.HasExifDate
        ? Asset.BestDate.ToString("yyyy/MM/dd HH:mm:ss")
        : Asset.BestDate.ToString("yyyy/MM/dd HH:mm:ss") + "（ファイル日時）";
    public string Resolution => Asset.Exif?.ImageWidth is int w && Asset.Exif?.ImageHeight is int h ? $"{w}×{h}" : "";
    public string Location => string.Join(" / ", new[] { Asset.Exif?.GeolocationCountry, Asset.Exif?.GeolocationCity }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool Matches(string search) =>
        SourcePath.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (OutputPath?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || Camera.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Lens.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Message?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    public static string StatusText(AssetStatus s) => s switch
    {
        AssetStatus.Scanned         => "未処理",
        AssetStatus.MetadataOk      => "整理予定",
        AssetStatus.PartialMetadata => "整理予定（撮影日なし）",
        AssetStatus.NoMetadata      => "整理予定（EXIFなし）",
        AssetStatus.Duplicate       => "重複",
        AssetStatus.Processed       => "完了",
        AssetStatus.Skipped         => "スキップ（既にあり）",
        AssetStatus.Filtered        => "対象外（日付）",
        AssetStatus.Error           => "エラー",
        _ => s.ToString(),
    };
}

public sealed record StatusFilterOption(string Label, Func<AssetStatus, bool> Predicate)
{
    public static readonly IReadOnlyList<StatusFilterOption> All =
    [
        new("すべて",               _ => true),
        new("完了・整理予定",       s => s is AssetStatus.Processed or AssetStatus.MetadataOk or AssetStatus.PartialMetadata or AssetStatus.NoMetadata),
        new("撮影日なし・EXIFなし", s => s is AssetStatus.PartialMetadata or AssetStatus.NoMetadata),
        new("スキップ",             s => s == AssetStatus.Skipped),
        new("重複",                 s => s == AssetStatus.Duplicate),
        new("対象外（日付）",       s => s == AssetStatus.Filtered),
        new("エラー",               s => s == AssetStatus.Error),
    ];

    public override string ToString() => Label;
}
