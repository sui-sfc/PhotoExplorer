using System.Diagnostics;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Tests;

/// <summary>テストごとの一時フォルダ</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "pe_tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] =>
        System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Write(string relative, string content)
    {
        var p = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

public static class TestImages
{
    // 1x1 の最小 JPEG
    private static readonly byte[] TinyJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");

    private static readonly Lazy<bool> ExifToolPresent = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("exiftool", "-ver")
            { RedirectStandardOutput = true, UseShellExecute = false })!;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    });

    public static bool HasExifTool => ExifToolPresent.Value;

    /// <summary>JPEG を作り、exiftool でタグを書き込む（exiftool が無ければタグなし）</summary>
    public static string CreateJpeg(string path, string? make = null, string? model = null,
        DateTime? date = null, string? lens = null, double? lat = null, double? lon = null, int? rating = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TinyJpeg);
        if (!HasExifTool) return path;

        var args = new List<string> { "-q", "-overwrite_original" };
        if (make != null) args.Add($"-Make={make}");
        if (model != null) args.Add($"-Model={model}");
        if (date != null) args.Add($"-DateTimeOriginal={date:yyyy:MM:dd HH:mm:ss}");
        if (lens != null) args.Add($"-LensModel={lens}");
        if (lat != null) { args.Add($"-GPSLatitude={Math.Abs(lat.Value)}"); args.Add($"-GPSLatitudeRef={(lat >= 0 ? "N" : "S")}"); }
        if (lon != null) { args.Add($"-GPSLongitude={Math.Abs(lon.Value)}"); args.Add($"-GPSLongitudeRef={(lon >= 0 ? "E" : "W")}"); }
        if (rating != null) args.Add($"-XMP:Rating={rating}");
        if (args.Count == 2) return path;
        args.Add(path);

        // Windows の exiftool はコマンドライン上の日本語パスを扱えないので、標準入力から UTF-8 で渡す
        var psi = new ProcessStartInfo("exiftool")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            RedirectStandardInput = true, StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var a in new[] { "-charset", "filename=utf8", "-@", "-" }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
        {
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            foreach (var a in args) p.StandardInput.WriteLine(a);
            p.StandardInput.Close();
            outp.Wait();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"exiftool でタグを書き込めません: {path}\n{err.Result}");
        }
        return path;
    }

    public static ImageAsset Asset(string path, ExifData? exif = null, long size = 100) => new()
    {
        SourcePath = path,
        FileSize = size,
        FileCreatedAt = new DateTime(2020, 1, 2, 3, 4, 5),
        FileModifiedAt = new DateTime(2020, 1, 2, 3, 4, 5),
        Exif = exif,
    };
}

/// <summary>exiftool がない環境ではスキップされる Fact</summary>
public sealed class ExifToolFactAttribute : FactAttribute
{
    public ExifToolFactAttribute()
    {
        // CI では PE_REQUIRE_EXIFTOOL=1 を設定し、exiftool がなければスキップではなく失敗させる
        if (!TestImages.HasExifTool && Environment.GetEnvironmentVariable("PE_REQUIRE_EXIFTOOL") != "1")
            Skip = "exiftool がインストールされていません";
    }
}
