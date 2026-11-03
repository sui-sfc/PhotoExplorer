using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoExplorer.Domain;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Presentation.Services;

/// <summary>
/// サムネイルをバックグラウンドで読み込む。
/// ・一度作ったサムネイルは %LocalAppData%\PhotoExplorer\thumbs に保存し、次回から再利用する
/// ・メモリ上には最近使ったものだけを置く（数万枚でもメモリを食いつぶさない）
/// ・EXIF の回転情報に従って縦位置の写真を正しい向きにする
/// JPEG はそのまま、RAW / HEIC は Windows のコーデック（Raw 画像拡張機能・HEIF 画像拡張機能）があれば表示できる。
/// </summary>
public static class ThumbnailLoader
{
    private const int MemoryCacheLimit = 400;

    private static readonly ConcurrentDictionary<string, ImageSource?> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> MemoryOrder = new();
    private static readonly SemaphoreSlim Gate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    public static string CacheDirectory => Path.Combine(AppPaths.DataDirectory, "thumbs");

    /// <summary>表示サイズに近い決まった幅に丸める（キャッシュが効くように）</summary>
    public static int BucketWidth(double displayWidth) => displayWidth switch
    {
        <= 170 => 200,
        <= 260 => 300,
        <= 400 => 440,
        _ => 1000,
    };

    public static async Task<ImageSource?> LoadAsync(string path, int decodeWidth = 200, CancellationToken ct = default)
    {
        var key = $"{decodeWidth}|{path}";
        if (Memory.TryGetValue(key, out var cached)) return cached;
        if (MediaFormats.GetKind(path) == MediaKind.Video) return null;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Memory.TryGetValue(key, out cached)) return cached;
            var image = await Task.Run(() => LoadOrCreate(path, decodeWidth), ct).ConfigureAwait(false);
            Remember(key, image);
            return image;
        }
        catch (OperationCanceledException) { return null; }
        finally { Gate.Release(); }
    }

    private static void Remember(string key, ImageSource? image)
    {
        if (Memory.TryAdd(key, image)) MemoryOrder.Enqueue(key);
        while (Memory.Count > MemoryCacheLimit && MemoryOrder.TryDequeue(out var old))
            Memory.TryRemove(old, out _);
    }

    public static void ClearDiskCache()
    {
        Memory.Clear();
        while (MemoryOrder.TryDequeue(out _)) { }
        if (Directory.Exists(CacheDirectory)) Directory.Delete(CacheDirectory, recursive: true);
    }

    public static long DiskCacheBytes()
    {
        try
        {
            return Directory.Exists(CacheDirectory)
                ? new DirectoryInfo(CacheDirectory).EnumerateFiles().Sum(f => f.Length)
                : 0;
        }
        catch { return 0; }
    }

    private static ImageSource? LoadOrCreate(string path, int decodeWidth)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            // ファイルが変わったら別のキャッシュになるよう、パス・更新日時・サイズ・幅から名前を作る
            var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
                $"{info.FullName.ToUpperInvariant()}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{decodeWidth}")));
            var cacheFile = Path.Combine(CacheDirectory, id + ".jpg");

            if (File.Exists(cacheFile))
            {
                var fromCache = Decode(cacheFile, 0, applyOrientation: false);
                if (fromCache != null) return fromCache;
            }

            var image = Decode(path, decodeWidth, applyOrientation: true);
            if (image != null) TrySave(image, cacheFile);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? Decode(string path, int decodeWidth, bool applyOrientation)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int orientation = applyOrientation ? ReadOrientation(stream) : 1;
            stream.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0)
            {
                // 縦位置（90°回転）の写真は、保存上の「高さ」が表示上の幅になる
                if (orientation is 5 or 6 or 7 or 8) bmp.DecodePixelHeight = decodeWidth;
                else bmp.DecodePixelWidth = decodeWidth;
            }
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();

            BitmapSource result = bmp;
            var rotation = orientation switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 };
            if (rotation != 0)
            {
                var rotated = new TransformedBitmap(bmp, new RotateTransform(rotation));
                rotated.Freeze();
                result = rotated;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>EXIF Orientation（1〜8）。読めなければ 1</summary>
    private static int ReadOrientation(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is BitmapMetadata meta
                && meta.GetQuery("System.Photo.Orientation") is ushort o)
                return o;
        }
        catch { }
        return 1;
    }

    private static void TrySave(BitmapSource image, string cacheFile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var tmp = cacheFile + ".tmp";
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(image));
            using (var fs = File.Create(tmp)) enc.Save(fs);
            File.Move(tmp, cacheFile, overwrite: true);
        }
        catch { }
    }
}
