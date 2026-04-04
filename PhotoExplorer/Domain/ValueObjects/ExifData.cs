namespace PhotoExplorer.Domain.ValueObjects;

/// <summary>EXIFメタデータを保持するValue Object</summary>
public sealed record ExifData
{
    public DateTime? DateTimeOriginal { get; init; }
    public DateTime? CreateDate { get; init; }
    public DateTime? ModifyDate { get; init; }

    /// <summary>カメラメーカー（正規化前）</summary>
    public string? Make { get; init; }
    /// <summary>カメラモデル（正規化前）</summary>
    public string? Model { get; init; }
    /// <summary>レンズモデル（正規化前）</summary>
    public string? LensModel { get; init; }

    public string? FocalLength { get; init; }
    public string? Aperture { get; init; }
    public string? ShutterSpeed { get; init; }
    public string? ISO { get; init; }

    /// <summary>EXIF抽出に使用した日時フィールド名</summary>
    public string? UsedDateField { get; init; }

    /// <summary>優先順位に従って有効な撮影日時を返す</summary>
    public DateTime? EffectiveDate =>
        DateTimeOriginal ?? CreateDate ?? ModifyDate;

    /// <summary>カメラ名（Make + Model を結合）</summary>
    public string? CameraName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model)) return null;
            if (string.IsNullOrWhiteSpace(Make)) return Model;
            // Model が Make を含む場合は Model のみ返す
            return Model.StartsWith(Make, StringComparison.OrdinalIgnoreCase)
                ? Model
                : $"{Make} {Model}";
        }
    }
}
