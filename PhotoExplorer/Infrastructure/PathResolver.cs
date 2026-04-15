using System.IO;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Services;

namespace PhotoExplorer.Infrastructure;

/// <summary>テンプレートを展開して出力先パスを生成する</summary>
public sealed class PathResolver : IPathResolver
{
    private readonly INormalizer _normalizer;

    private static readonly HashSet<string> JpegExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg" };

    public PathResolver(INormalizer normalizer)
    {
        _normalizer = normalizer;
    }

    public string Resolve(ImageAsset asset, string outputRoot, string template)
    {
        var date    = asset.Exif?.EffectiveDate ?? asset.FileCreatedAt;
        var camera  = asset.NormalizedCamera ?? "Unknown";
        var lens    = asset.NormalizedLens   ?? "Unknown";
        var city    = _normalizer.SanitizeForPath(asset.Exif?.GeolocationCity    ?? "Unknown");
        var region  = _normalizer.SanitizeForPath(asset.Exif?.GeolocationRegion  ?? "Unknown");
        var country = _normalizer.SanitizeForPath(asset.Exif?.GeolocationCountry ?? "Unknown");
        var countryCode = _normalizer.SanitizeForPath(asset.Exif?.GeolocationCountryCode ?? "Unknown");

        // 拡張子（先頭の . を除いた小文字）と種別
        var ext  = asset.Extension.TrimStart('.').ToLowerInvariant();
        var type = JpegExtensions.Contains(asset.Extension) ? "JPEG" : "RAW";

        var relativePath = template
            .Replace("{yyyy}",        date.Year.ToString("D4"))
            .Replace("{mm}",          date.Month.ToString("D2"))
            .Replace("{dd}",          date.Day.ToString("D2"))
            .Replace("{camera}",      _normalizer.SanitizeForPath(camera))
            .Replace("{lens}",        _normalizer.SanitizeForPath(lens))
            .Replace("{city}",        city)
            .Replace("{region}",      region)
            .Replace("{country}",     country)
            .Replace("{countrycode}", countryCode)
            .Replace("{type}",        type)
            .Replace("{ext}",         ext);

        var segments = relativePath
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        return Path.Combine(outputRoot, Path.Combine(segments), asset.FileName);
    }
}
