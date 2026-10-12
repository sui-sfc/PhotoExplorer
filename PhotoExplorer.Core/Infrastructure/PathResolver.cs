using System.Globalization;
using System.Text.RegularExpressions;
using PhotoExplorer.Domain;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Services;

namespace PhotoExplorer.Infrastructure;

/// <summary>テンプレートを展開して出力先パスを生成する</summary>
public sealed partial class PathResolver : IPathResolver
{
    private readonly INormalizer _normalizer;

    private static readonly HashSet<string> KnownTokens =
        TemplateVariables.All.Select(v => v.Token).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TokenRegex();

    public PathResolver(INormalizer normalizer) => _normalizer = normalizer;

    public IReadOnlyList<string> FindUnknownVariables(string template) =>
        TokenRegex().Matches(template ?? string.Empty)
            .Select(m => m.Value)
            .Where(t => !KnownTokens.Contains(t))
            .Distinct()
            .ToList();

    public string Resolve(ImageAsset asset, PathOptions options)
    {
        var values = BuildValues(asset);
        var fileName = BuildFileName(asset, options.FileNameTemplate, values);

        if (!asset.HasExifDate && !string.IsNullOrWhiteSpace(options.NoDateFolderName))
        {
            var folder = Normalizer.SanitizeLiteralSegment(options.NoDateFolderName);
            return Path.Combine(options.OutputRoot, folder, fileName);
        }

        var segments = ExpandSegments(options.FolderTemplate, values);
        return Path.Combine([options.OutputRoot, .. segments, fileName]);
    }

    private Dictionary<string, string> BuildValues(ImageAsset asset)
    {
        var d = asset.BestDate;
        var exif = asset.Exif;
        string S(string? v) => _normalizer.SanitizeForPath(v ?? "Unknown");

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{yyyy}"]        = d.Year.ToString("D4", CultureInfo.InvariantCulture),
            ["{yy}"]          = (d.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
            ["{mm}"]          = d.Month.ToString("D2", CultureInfo.InvariantCulture),
            ["{MMM}"]         = d.ToString("MMM", CultureInfo.InvariantCulture),
            ["{dd}"]          = d.Day.ToString("D2", CultureInfo.InvariantCulture),
            ["{HH}"]          = d.Hour.ToString("D2", CultureInfo.InvariantCulture),
            ["{min}"]         = d.Minute.ToString("D2", CultureInfo.InvariantCulture),
            ["{ss}"]          = d.Second.ToString("D2", CultureInfo.InvariantCulture),
            ["{camera}"]      = S(asset.NormalizedCamera),
            ["{make}"]        = S(exif?.Brand),
            ["{lens}"]        = S(asset.NormalizedLens),
            ["{type}"]        = MediaFormats.KindLabel(asset.Kind),
            ["{ext}"]         = asset.Extension.TrimStart('.'),
            ["{rating}"]      = Math.Clamp(exif?.Rating ?? 0, 0, 5).ToString(CultureInfo.InvariantCulture),
            ["{city}"]        = S(exif?.GeolocationCity),
            ["{region}"]      = S(exif?.GeolocationRegion),
            ["{country}"]     = S(exif?.GeolocationCountry),
            ["{countrycode}"] = S(exif?.GeolocationCountryCode),
            ["{name}"]        = Path.GetFileNameWithoutExtension(asset.SourcePath),
        };
    }

    private static string Expand(string text, Dictionary<string, string> values) =>
        TokenRegex().Replace(text, m => values.TryGetValue(m.Value, out var v) ? v : m.Value);

    private static List<string> ExpandSegments(string template, Dictionary<string, string> values)
    {
        // セグメント単位で展開することで、値に含まれる文字でフォルダ階層が増えたり
        // ".." で出力先の外へ出たりしないようにする
        return (template ?? string.Empty)
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => Normalizer.SanitizeLiteralSegment(Expand(seg, values)))
            .ToList();
    }

    private static string BuildFileName(ImageAsset asset, string? template, Dictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(template)) return asset.FileName;
        var stem = Normalizer.SanitizeLiteralSegment(Expand(template.Replace('/', '_').Replace('\\', '_'), values));
        return stem + Path.GetExtension(asset.SourcePath);
    }
}
