using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoExplorer.Domain;
using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Presentation.Services;

/// <summary>
/// サムネイルをバックグラウンドで読み込む。
/// JPEG はそのまま、RAW / HEIC は Windows のコーデック（Raw 画像拡張機能・HEIF 画像拡張機能）があれば表示できる。
/// </summary>
public static class ThumbnailLoader
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(4);

    public static async Task<ImageSource?> LoadAsync(string path, int decodeWidth = 200)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        if (MediaFormats.GetKind(path) == MediaKind.Video) return null;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var image = await Task.Run(() => Decode(path, decodeWidth)).ConfigureAwait(false);
            Cache[path] = image;
            return image;
        }
        finally { Gate.Release(); }
    }

    private static ImageSource? Decode(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
