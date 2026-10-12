namespace PhotoExplorer.Infrastructure;

public static class PathUtil
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool PathEquals(string a, string b) =>
        string.Equals(NormalizeDirectory(a), NormalizeDirectory(b), Comparison);

    /// <summary>child が parent と同じか、その配下にあるか</summary>
    public static bool IsSameOrUnder(string child, string parent)
    {
        var c = NormalizeDirectory(child);
        var p = NormalizeDirectory(parent);
        if (string.Equals(c, p, Comparison)) return true;
        return c.StartsWith(p + Path.DirectorySeparatorChar, Comparison);
    }

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>空になったフォルダを stopAt の手前まで削除する</summary>
    public static void DeleteEmptyParents(string? directory, string stopAt)
    {
        try
        {
            while (!string.IsNullOrEmpty(directory)
                   && IsSameOrUnder(directory, stopAt)
                   && !PathEquals(directory, stopAt)
                   && Directory.Exists(directory)
                   && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
