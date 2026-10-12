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

    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    /// <summary>XMP/EXIF のレーティング（0〜5）</summary>
    public int? Rating { get; init; }

    /// <summary>GPS緯度（10進度）</summary>
    public double? GPSLatitude { get; init; }
    /// <summary>GPS経度（10進度）</summary>
    public double? GPSLongitude { get; init; }

    /// <summary>exiftool Geolocation: 都市名（exiftool 12.78+ 必要）</summary>
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

    /// <summary>
    /// メーカー名の社名部分を省いたカメラ名。
    /// 例: Make="RICOH IMAGING COMPANY, LTD." Model="RICOH GR IIIx" → "RICOH GR IIIx"
    /// </summary>
    public string? CameraName
    {
        get
        {
            var model = Model?.Trim();
            if (string.IsNullOrWhiteSpace(model)) return null;
            var brand = Brand;
            if (brand == null || model.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
                return model;
            return $"{brand} {model}";
        }
    }

    /// <summary>Make と Model をそのまま結合した名前（旧形式のマッピングキー互換用）</summary>
    public string? CameraNameFull
    {
        get
        {
            var model = Model?.Trim();
            var make = Make?.Trim();
            if (string.IsNullOrWhiteSpace(model)) return null;
            if (string.IsNullOrWhiteSpace(make)) return model;
            return model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
        }
    }

    /// <summary>メーカー名の先頭語（"NIKON CORPORATION" → "NIKON"）</summary>
    public string? Brand
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Make)) return null;
            var first = Make.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(first) ? null : first;
        }
    }

    /// <summary>GPS座標が有効かどうか</summary>
    public bool HasGps => GPSLatitude.HasValue && GPSLongitude.HasValue;

    public bool IsEmpty =>
        EffectiveDate == null && Make == null && Model == null && LensModel == null
        && !HasGps && GeolocationCity == null && ImageWidth == null;
}
