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

    /// <summary>GPS緯度（10進度）</summary>
    public double? GPSLatitude { get; init; }
    /// <summary>GPS経度（10進度）</summary>
    public double? GPSLongitude { get; init; }

    /// <summary>exiftool Geolocation: 都市名（exiftool 13+ 必要）</summary>
    public string? GeolocationCity { get; init; }
    /// <summary>exiftool Geolocation: 都道府県・州・地域</summary>
    public string? GeolocationRegion { get; init; }
    /// <summary>exiftool Geolocation: 国名</summary>
    public string? GeolocationCountry { get; init; }
    /// <summary>exiftool Geolocation: 国コード（JP, US など）</summary>
    public string? GeolocationCountryCode { get; init; }

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
            return Model.StartsWith(Make, StringComparison.OrdinalIgnoreCase)
                ? Model
                : $"{Make} {Model}";
        }
    }

    /// <summary>GPS座標が有効かどうか</summary>
    public bool HasGps => GPSLatitude.HasValue && GPSLongitude.HasValue;
}
