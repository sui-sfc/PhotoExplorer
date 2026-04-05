using System.IO;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Services;

namespace PhotoExplorer.Infrastructure;

/// <summary>テンプレートを展開して出力先パスを生成する</summary>
public sealed class PathResolver : IPathResolver
{
    private readonly INormalizer _normalizer;

    public PathResolver(INormalizer normalizer)
    {
        _normalizer = normalizer;
    }

    public string Resolve(ImageAsset asset, string outputRoot, string template)
    {
        var date = asset.Exif?.EffectiveDate ?? asset.FileCreatedAt;
        var camera = asset.NormalizedCamera ?? "Unknown";
        var lens = asset.NormalizedLens ?? "Unknown";

        // テンプレート変数を展開
        var relativePath = template
            .Replace("{yyyy}", date.Year.ToString("D4"))
            .Replace("{mm}", date.Month.ToString("D2"))
            .Replace("{dd}", date.Day.ToString("D2"))
            .Replace("{camera}", _normalizer.SanitizeForPath(camera))
            .Replace("{lens}", _normalizer.SanitizeForPath(lens));

        // 空セグメントを除去（取得できなかった要素をスキップ）
        var segments = relativePath
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "Unknown" || template.Contains("{camera}") || template.Contains("{lens}"))
            .ToArray();

        // Unknown セグメントを除去しない場合もあるので、シンプルに結合
        var cleanSegments = relativePath
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        var relativeDir = Path.Combine(cleanSegments);
        return Path.Combine(outputRoot, relativeDir, asset.FileName);
    }
}
