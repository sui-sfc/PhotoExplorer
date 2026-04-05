using System.IO;
using PhotoExplorer.Domain.Services;
using System.Text.RegularExpressions;

namespace PhotoExplorer.Infrastructure;

/// <summary>カメラ・レンズ名の表記ゆれを正規化する</summary>
public sealed partial class Normalizer : INormalizer
{
    private readonly MappingConfig _custom;

    // 組み込みカメラ名マッピング（カスタムで上書き可能）
    private static readonly Dictionary<string, string> BuiltinCameraMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Canon EOS R5"]          = "Canon_EOS_R5",
        ["Canon EOS R6"]          = "Canon_EOS_R6",
        ["Canon EOS R6 Mark II"]  = "Canon_EOS_R6_MarkII",
        ["Canon EOS 5D Mark IV"]  = "Canon_5D_MarkIV",
        ["Canon EOS 5D Mark III"] = "Canon_5D_MarkIII",
        ["NIKON Z 6II"]           = "Nikon_Z6II",
        ["NIKON Z 7II"]           = "Nikon_Z7II",
        ["NIKON D850"]            = "Nikon_D850",
        ["NIKON D750"]            = "Nikon_D750",
        ["ILCE-7M4"]              = "Sony_A7IV",
        ["ILCE-7M3"]              = "Sony_A7III",
        ["ILCE-7RM5"]             = "Sony_A7RV",
        ["ILCE-7RM4"]             = "Sony_A7RIV",
        ["ILCE-7C"]               = "Sony_A7C",
        ["RICOH GR IIIx"]        = "RICOH_GR_IIIx",
        ["RICOH GR III"]          = "RICOH_GR_III",
    };

    // 組み込みレンズ名マッピング
    private static readonly Dictionary<string, string> BuiltinLensMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RF24-105mm F4 L IS USM"]      = "Canon_RF24-105_F4L",
        ["RF50mm F1.8 STM"]             = "Canon_RF50_F1.8",
        ["NIKKOR Z 24-70mm f/2.8 S"]    = "Nikkor_Z24-70_F2.8S",
        ["NIKKOR Z 50mm f/1.8 S"]       = "Nikkor_Z50_F1.8S",
        ["FE 24-70mm F2.8 GM II"]       = "Sony_FE24-70_GM2",
        ["FE 50mm F1.2 GM"]             = "Sony_FE50_F1.2GM",
    };

    // ファイルパスに使用できない文字（スペースも含む）
    [GeneratedRegex(@"[\\/:*?""<>|,\s]+")]
    private static partial Regex InvalidPathCharsRegex();

    // 連続アンダースコアを1つに
    [GeneratedRegex(@"_{2,}")]
    private static partial Regex MultipleUnderscoresRegex();

    public Normalizer(MappingConfig? customMappings = null)
    {
        _custom = customMappings ?? new MappingConfig();
    }

    public string NormalizeCamera(string? rawCamera)
    {
        if (string.IsNullOrWhiteSpace(rawCamera)) return "Unknown";
        var trimmed = rawCamera.Trim();

        // 1. カスタムマッピング（完全一致）
        if (_custom.Camera.TryGetValue(trimmed, out var customExact)) return customExact;
        // 2. カスタムマッピング（部分一致）
        foreach (var (key, value) in _custom.Camera)
            if (trimmed.Contains(key, StringComparison.OrdinalIgnoreCase)) return value;

        // 3. 組み込みマッピング（完全一致）
        if (BuiltinCameraMap.TryGetValue(trimmed, out var builtinExact)) return builtinExact;
        // 4. 組み込みマッピング（部分一致）
        foreach (var (key, value) in BuiltinCameraMap)
            if (trimmed.Contains(key, StringComparison.OrdinalIgnoreCase)) return value;

        // 5. マッチなし → スペースをアンダースコアに変換して返す
        return SanitizeForPath(trimmed);
    }

    public string NormalizeLens(string? rawLens)
    {
        if (string.IsNullOrWhiteSpace(rawLens)) return "Unknown";
        var trimmed = rawLens.Trim();

        if (_custom.Lens.TryGetValue(trimmed, out var customExact)) return customExact;
        foreach (var (key, value) in _custom.Lens)
            if (trimmed.Contains(key, StringComparison.OrdinalIgnoreCase)) return value;

        if (BuiltinLensMap.TryGetValue(trimmed, out var builtinExact)) return builtinExact;
        foreach (var (key, value) in BuiltinLensMap)
            if (trimmed.Contains(key, StringComparison.OrdinalIgnoreCase)) return value;

        return SanitizeForPath(trimmed);
    }

    public string SanitizeForPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        // スペース・無効文字をアンダースコアに置換
        var sanitized = InvalidPathCharsRegex().Replace(value, "_");
        // 連続アンダースコアを1つに
        sanitized = MultipleUnderscoresRegex().Replace(sanitized, "_");
        return sanitized.Trim('_');
    }
}
