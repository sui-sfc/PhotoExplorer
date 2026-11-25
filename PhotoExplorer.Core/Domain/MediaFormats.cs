using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Domain;

/// <summary>対応する拡張子と種別の一覧</summary>
public static class MediaFormats
{
    private static readonly Dictionary<string, MediaKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MediaKind.Jpeg, [".jpeg"] = MediaKind.Jpeg,

        [".heic"] = MediaKind.Heif, [".heif"] = MediaKind.Heif, [".hif"] = MediaKind.Heif,

        [".cr2"] = MediaKind.Raw, [".cr3"] = MediaKind.Raw, [".crw"] = MediaKind.Raw,   // Canon
        [".nef"] = MediaKind.Raw, [".nrw"] = MediaKind.Raw,                            // Nikon
        [".arw"] = MediaKind.Raw, [".srf"] = MediaKind.Raw, [".sr2"] = MediaKind.Raw,  // Sony
        [".raf"] = MediaKind.Raw,                                                      // Fujifilm
        [".orf"] = MediaKind.Raw, [".ori"] = MediaKind.Raw,                            // Olympus / OM System
        [".rw2"] = MediaKind.Raw, [".rwl"] = MediaKind.Raw,                            // Panasonic / Leica
        [".pef"] = MediaKind.Raw,                                                      // Pentax
        [".srw"] = MediaKind.Raw,                                                      // Samsung
        [".3fr"] = MediaKind.Raw, [".fff"] = MediaKind.Raw,                            // Hasselblad
        [".iiq"] = MediaKind.Raw,                                                      // Phase One
        [".x3f"] = MediaKind.Raw,                                                      // Sigma
        [".dng"] = MediaKind.Raw,                                                      // Adobe DNG

        [".mp4"] = MediaKind.Video, [".mov"] = MediaKind.Video, [".m4v"] = MediaKind.Video,
        [".mts"] = MediaKind.Video, [".m2ts"] = MediaKind.Video, [".avi"] = MediaKind.Video,
        [".3gp"] = MediaKind.Video,
    };

    public static MediaKind? GetKind(string pathOrExtension)
    {
        var ext = pathOrExtension.StartsWith('.') ? pathOrExtension : Path.GetExtension(pathOrExtension);
        return Map.TryGetValue(ext, out var kind) ? kind : null;
    }

    /// <summary>文字列を割り当てずに拡張子の対応有無を判定する（高速スキャン用）</summary>
    public static bool IsSupportedExtension(ReadOnlySpan<char> ext, bool includeVideos)
    {
        if (ext.Length < 4 || ext.Length > 5 || ext[0] != '.') return false;
        // 頻出拡張子を最速パスで判定
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cr3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cr2", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".arw", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".nef", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".raf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dng", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".orf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".rw2", StringComparison.OrdinalIgnoreCase))
            return true;

        var kind = GetKind(ext.ToString());
        return kind != null && (includeVideos || kind != MediaKind.Video);
    }

    public static bool IsSupported(string path, bool includeVideos)
    {
        var kind = GetKind(path);
        return kind != null && (includeVideos || kind != MediaKind.Video);
    }

    public static string KindLabel(MediaKind kind) => kind switch
    {
        MediaKind.Jpeg  => "JPEG",
        MediaKind.Raw   => "RAW",
        MediaKind.Heif  => "HEIF",
        MediaKind.Video => "VIDEO",
        _ => "OTHER",
    };

    public static IEnumerable<string> Extensions(MediaKind kind) =>
        Map.Where(p => p.Value == kind).Select(p => p.Key);
}
