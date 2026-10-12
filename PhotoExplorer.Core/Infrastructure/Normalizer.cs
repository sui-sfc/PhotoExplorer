using System.Text.RegularExpressions;
using PhotoExplorer.Domain.Services;

namespace PhotoExplorer.Infrastructure;

/// <summary>カメラ・レンズ名の表記ゆれを正規化する</summary>
public sealed partial class Normalizer : INormalizer
{
    private readonly MappingConfigProvider _mappings;

    private static readonly Dictionary<string, string> BuiltinCameraMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Canon EOS R5"]          = "Canon_EOS_R5",
        ["Canon EOS R6"]          = "Canon_EOS_R6",
        ["Canon EOS R6m2"]        = "Canon_EOS_R6_MarkII",
        ["Canon EOS R6 Mark II"]  = "Canon_EOS_R6_MarkII",
        ["Canon EOS 5D Mark IV"]  = "Canon_5D_MarkIV",
        ["Canon EOS 5D Mark III"] = "Canon_5D_MarkIII",
        ["NIKON Z 6_2"]           = "Nikon_Z6II",
        ["NIKON Z 6II"]           = "Nikon_Z6II",
        ["NIKON Z 7_2"]           = "Nikon_Z7II",
        ["NIKON Z 7II"]           = "Nikon_Z7II",
        ["NIKON Z f"]             = "Nikon_Zf",
        ["NIKON D850"]            = "Nikon_D850",
        ["NIKON D750"]            = "Nikon_D750",
        ["ILCE-7M4"]              = "Sony_A7IV",
        ["ILCE-7M3"]              = "Sony_A7III",
        ["ILCE-7RM5"]             = "Sony_A7RV",
        ["ILCE-7RM4"]             = "Sony_A7RIV",
        ["ILCE-7CM2"]             = "Sony_A7CII",
        ["ILCE-7C"]               = "Sony_A7C",
        ["ILCE-1"]                = "Sony_A1",
        ["RICOH GR IIIx"]         = "RICOH_GR_IIIx",
        ["RICOH GR III"]          = "RICOH_GR_III",
    };

    private static readonly Dictionary<string, string> BuiltinLensMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RF24-105mm F4 L IS USM"]   = "Canon_RF24-105_F4L",
        ["RF50mm F1.8 STM"]          = "Canon_RF50_F1.8",
        ["NIKKOR Z 24-70mm f/2.8 S"] = "Nikkor_Z24-70_F2.8S",
        ["NIKKOR Z 50mm f/1.8 S"]    = "Nikkor_Z50_F1.8S",
        ["FE 24-70mm F2.8 GM II"]    = "Sony_FE24-70_GM2",
        ["FE 50mm F1.2 GM"]          = "Sony_FE50_F1.2GM",
    };

    // 部分一致は長いキーから試す（"RICOH GR III" が "RICOH GR IIIx" を横取りしないように）
    private static readonly KeyValuePair<string, string>[] BuiltinCameraByLength =
        BuiltinCameraMap.OrderByDescending(p => p.Key.Length).ToArray();
    private static readonly KeyValuePair<string, string>[] BuiltinLensByLength =
        BuiltinLensMap.OrderByDescending(p => p.Key.Length).ToArray();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int MaxSegmentLength = 100;

    [GeneratedRegex(@"[\\/:*?""<>|,\s\x00-\x1F]+")]
    private static partial Regex InvalidCharsWithSpaceRegex();

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1F]+")]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex(@"_{2,}")]
    private static partial Regex MultipleUnderscoresRegex();

    public Normalizer(MappingConfigProvider mappings) => _mappings = mappings;

    public Normalizer(MappingConfig? mappings = null)
        : this(MappingConfigProvider.Fixed(mappings ?? new MappingConfig())) { }

    public string NormalizeCamera(params string?[] candidates)
    {
        var names = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return "Unknown";

        var custom = _mappings.Current.Camera;
        return Lookup(names, custom, BuiltinCameraMap, BuiltinCameraByLength) ?? SanitizeForPath(names[0]);
    }

    public string NormalizeLens(string? rawLens)
    {
        if (string.IsNullOrWhiteSpace(rawLens)) return "Unknown";
        var names = new List<string> { rawLens.Trim() };
        var custom = _mappings.Current.Lens;
        return Lookup(names, custom, BuiltinLensMap, BuiltinLensByLength) ?? SanitizeForPath(names[0]);
    }

    private string? Lookup(
        List<string> names,
        Dictionary<string, string> custom,
        Dictionary<string, string> builtin,
        KeyValuePair<string, string>[] builtinByLength)
    {
        foreach (var n in names)
            if (custom.TryGetValue(n, out var v)) return SanitizeForPath(v);

        var customByLength = custom.Where(p => !string.IsNullOrWhiteSpace(p.Key))
                                   .OrderByDescending(p => p.Key.Length).ToArray();
        foreach (var n in names)
            foreach (var (key, value) in customByLength)
                if (n.Contains(key, StringComparison.OrdinalIgnoreCase)) return SanitizeForPath(value);

        foreach (var n in names)
            if (builtin.TryGetValue(n, out var v)) return v;
        foreach (var n in names)
            foreach (var (key, value) in builtinByLength)
                if (n.Contains(key, StringComparison.OrdinalIgnoreCase)) return value;

        return null;
    }

    public string SanitizeForPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var s = InvalidCharsWithSpaceRegex().Replace(value, "_");
        s = MultipleUnderscoresRegex().Replace(s, "_").Trim('_');
        return FinishSegment(s);
    }

    /// <summary>
    /// テンプレート中の固定文字列用。空白はそのまま残し、使えない文字だけを置き換える。
    /// </summary>
    public static string SanitizeLiteralSegment(string value)
    {
        var s = InvalidCharsRegex().Replace(value, "_");
        return FinishSegment(s.Trim());
    }

    private static string FinishSegment(string s)
    {
        // Windows では末尾のドット・空白は使えない
        s = s.TrimEnd('.', ' ');
        if (s.Length > MaxSegmentLength) s = s[..MaxSegmentLength].TrimEnd('.', ' ', '_');
        if (s.Length == 0 || s.All(c => c == '.')) return "Unknown";
        var stem = s.Split('.')[0];
        if (ReservedNames.Contains(stem)) s = "_" + s;
        return s;
    }
}
