using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Domain.ValueObjects;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Tests;

public class NormalizerTests
{
    [Fact]
    public void CameraName_DropsCompanySuffixFromMake()
    {
        var e = new ExifData { Make = "RICOH IMAGING COMPANY, LTD.", Model = "RICOH GR IIIx" };
        Assert.Equal("RICOH GR IIIx", e.CameraName);
        Assert.Equal("RICOH IMAGING COMPANY, LTD. RICOH GR IIIx", e.CameraNameFull);

        Assert.Equal("SONY ILCE-7M4", new ExifData { Make = "SONY", Model = "ILCE-7M4" }.CameraName);
        Assert.Equal("Canon EOS R5", new ExifData { Make = "Canon", Model = "Canon EOS R5" }.CameraName);
        Assert.Null(new ExifData { Make = "Canon" }.CameraName);
    }

    [Fact]
    public void PartialMatch_PrefersLongestKey()
    {
        var n = new Normalizer();
        // "RICOH GR III" が "RICOH GR IIIx" に部分一致して先に選ばれてはいけない
        Assert.Equal("RICOH_GR_IIIx", n.NormalizeCamera("RICOH IMAGING COMPANY, LTD. RICOH GR IIIx"));
        Assert.Equal("RICOH_GR_III", n.NormalizeCamera("RICOH GR III"));
    }

    [Fact]
    public void CustomMapping_WinsOverBuiltin_AndMatchesAnyCandidate()
    {
        var cfg = new MappingConfig();
        cfg.Camera["ricoh imaging company, ltd. ricoh gr iiix"] = "My GR";
        var n = new Normalizer(cfg);
        // 旧形式（Make+Model）のキーでも一致する
        Assert.Equal("My_GR", n.NormalizeCamera("RICOH GR IIIx", "RICOH IMAGING COMPANY, LTD. RICOH GR IIIx"));
    }

    [Fact]
    public void Unknown_WhenNoName()
    {
        var n = new Normalizer();
        Assert.Equal("Unknown", n.NormalizeCamera(null, " "));
        Assert.Equal("Unknown", n.NormalizeLens(null));
    }

    [Theory]
    [InlineData("Canon EOS R5", "Canon_EOS_R5")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("..", "Unknown")]
    [InlineData("name.", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("  ", "Unknown")]
    public void Sanitize(string input, string expected) =>
        Assert.Equal(expected, new Normalizer().SanitizeForPath(input));

    [Fact]
    public void MappingProvider_ReloadsWhenFileChanges_AndKeepsLastGoodConfigOnError()
    {
        using var tmp = new TempDir();
        var path = tmp["mappings.json"];
        var provider = new MappingConfigProvider(path);
        Assert.True(File.Exists(path));

        File.WriteAllText(path, """{ "Camera": { "X100V": "Fuji_X100V" }, "Lens": {} }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        var n = new Normalizer(provider);
        Assert.Equal("Fuji_X100V", n.NormalizeCamera("FUJIFILM X100V"));

        File.WriteAllText(path, "{ broken json");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
        Assert.Equal("Fuji_X100V", n.NormalizeCamera("FUJIFILM X100V"));
        Assert.NotNull(provider.LastError);
    }
}

public class PathResolverTests
{
    private static readonly PathResolver Resolver = new(new Normalizer());
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "out");

    private static ImageAsset Sample(string file = "IMG_0001.CR3", ExifData? exif = null)
    {
        var a = TestImages.Asset(Path.Combine(Path.GetTempPath(), "in", file), exif ?? new ExifData
        {
            DateTimeOriginal = new DateTime(2024, 7, 15, 9, 5, 42),
            Make = "Canon", Model = "Canon EOS R5",
            GeolocationCity = "Shibuya", GeolocationCountry = "Japan", GeolocationCountryCode = "JP",
            Rating = 4,
        });
        a.NormalizedCamera = "Canon_EOS_R5";
        a.NormalizedLens = "Unknown";
        return a;
    }

    [Fact]
    public void ExpandsAllVariables()
    {
        var p = Resolver.Resolve(Sample(), new PathOptions(Root,
            "{yyyy}/{yy}-{mm}-{MMM}-{dd}/{HH}{min}{ss}/{camera}/{make}/{type}-{ext}/{rating}/{countrycode}-{country}-{city}-{region}/{name}"));
        var expected = Path.Combine(Root, "2024", "24-07-Jul-15", "090542", "Canon_EOS_R5", "Canon", "RAW-cr3", "4",
            "JP-Japan-Shibuya-Unknown", "IMG_0001", "IMG_0001.CR3");
        Assert.Equal(expected, p);
    }

    [Fact]
    public void FileNameTemplate_KeepsExtension()
    {
        var p = Resolver.Resolve(Sample("IMG_0001.JPG"), new PathOptions(Root, "{yyyy}", "{yyyy}{mm}{dd}_{HH}{min}{ss}_{name}"));
        Assert.Equal(Path.Combine(Root, "2024", "20240715_090542_IMG_0001.JPG"), p);
    }

    [Fact]
    public void NoDateFolder_UsedOnlyWhenExifDateMissing()
    {
        var noDate = Sample("a.jpg", new ExifData { Make = "Canon", Model = "X" });
        Assert.Equal(Path.Combine(Root, "_NoDate", "a.jpg"),
            Resolver.Resolve(noDate, new PathOptions(Root, "{yyyy}/{mm}", "", "_NoDate")));

        // フォルダ名未指定ならファイルの日時を使う
        Assert.Equal(Path.Combine(Root, "2020", "01", "a.jpg"),
            Resolver.Resolve(noDate, new PathOptions(Root, "{yyyy}/{mm}")));
    }

    [Fact]
    public void TemplateCannotEscapeOutputRoot()
    {
        var p = Resolver.Resolve(Sample(), new PathOptions(Root, "../../{yyyy}/./x"));
        Assert.StartsWith(Root + Path.DirectorySeparatorChar, p);
        Assert.DoesNotContain("..", p.Substring(Root.Length));
    }

    [Fact]
    public void LiteralTextKeepsSpaces()
    {
        var p = Resolver.Resolve(Sample(), new PathOptions(Root, "My Photos/{yyyy}"));
        Assert.Equal(Path.Combine(Root, "My Photos", "2024", "IMG_0001.CR3"), p);
    }

    [Fact]
    public void FindsUnknownVariables()
    {
        Assert.Equal(["{month}", "{foo}"], Resolver.FindUnknownVariables("{yyyy}/{month}/{foo}/{camera}"));
        Assert.Empty(Resolver.FindUnknownVariables("{yyyy}/{mm}/{dd}"));
    }

    [Fact]
    public void BestDate_UsesOlderOfCreatedAndModifiedWhenNoExif()
    {
        var a = new ImageAsset
        {
            SourcePath = "x.jpg",
            FileCreatedAt = new DateTime(2025, 1, 1),   // コピーした日
            FileModifiedAt = new DateTime(2019, 5, 5),  // 撮影日に近い
        };
        Assert.Equal(new DateTime(2019, 5, 5), a.BestDate);
    }
}
