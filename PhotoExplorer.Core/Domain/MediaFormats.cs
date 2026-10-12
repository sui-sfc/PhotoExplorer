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
