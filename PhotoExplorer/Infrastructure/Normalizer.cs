using PhotoExplorer.Domain.Services;
using System.Text.RegularExpressions;

namespace PhotoExplorer.Infrastructure;

/// <summary>カメラ・レンズ名の表記ゆれを正規化する</summary>
public sealed partial class Normalizer : INormalizer
{
    // カメラ名マッピング（表記ゆれ → 正規名）
    private static readonly Dictionary<string, string> CameraMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Canon
        ["Canon EOS R5"] = "Canon EOS R5",
        ["Canon EOS R6"] = "Canon EOS R6",
        ["Canon EOS R6 Mark II"] = "Canon EOS R6 Mark II",
        ["Canon EOS 5D Mark IV"] = "Canon EOS 5D Mark IV",
        ["Canon EOS 5D Mark III"] = "Canon EOS 5D Mark III",
        // Nikon
        ["NIKON Z 6II"] = "Nikon Z6 II",
        ["NIKON Z 7II"] = "Nikon Z7 II",
        ["NIKON D850"] = "Nikon D850",
        ["NIKON D750"] = "Nikon D750",
        // Sony
        ["ILCE-7M4"] = "Sony α7 IV",
        ["ILCE-7M3"] = "Sony α7 III",
        ["ILCE-7RM5"] = "Sony α7R V",
        ["ILCE-7RM4"] = "Sony α7R IV",
        ["ILCE-7C"] = "Sony α7C",
    };

    // レンズ名マッピング
    private static readonly Dictionary<string, string> LensMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RF24-105mm F4 L IS USM"] = "Canon RF 24-105mm F4L IS USM",
        ["RF50mm F1.8 STM"] = "Canon RF 50mm F1.8 STM",
        ["NIKKOR Z 24-70mm f/2.8 S"] = "Nikkor Z 24-70mm f/2.8 S",
        ["NIKKOR Z 50mm f/1.8 S"] = "Nikkor Z 50mm f/1.8 S",
        ["FE 24-70mm F2.8 GM II"] = "Sony FE 24-70mm F2.8 GM II",
        ["FE 50mm F1.2 GM"] = "Sony FE 50mm F1.2 GM",
    };

    // ファイルパスに使用できない文字
    [GeneratedRegex(@"[\\/:*?""<>|]")]
    private static partial Regex InvalidPathCharsRegex();

    // 連続スペースを1つに
    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpacesRegex();

    public string NormalizeCamera(string? rawCamera)
    {
        if (string.IsNullOrWhiteSpace(rawCamera)) return "Unknown";

        var trimmed = rawCamera.Trim();
        if (CameraMap.TryGetValue(trimmed, out var mapped)) return mapped;

        // 部分一致を試みる
        foreach (var (key, value) in CameraMap)
        {
            if (trimmed.Contains(key, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return trimmed;
    }

    public string NormalizeLens(string? rawLens)
    {
        if (string.IsNullOrWhiteSpace(rawLens)) return "Unknown";

        var trimmed = rawLens.Trim();
        if (LensMap.TryGetValue(trimmed, out var mapped)) return mapped;

        foreach (var (key, value) in LensMap)
        {
            if (trimmed.Contains(key, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return trimmed;
    }

    public string SanitizeForPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var sanitized = InvalidPathCharsRegex().Replace(value, "_");
        sanitized = MultipleSpacesRegex().Replace(sanitized, " ");
        return sanitized.Trim();
    }
}
