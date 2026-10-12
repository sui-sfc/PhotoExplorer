using Newtonsoft.Json.Linq;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Tests;

public class SettingsValidatorTests
{
    private static readonly PathResolver Resolver = new(new Normalizer());

    [Fact]
    public void RejectsSameInputAndOutput_ForCopy()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t["in"]);
        var r = SettingsValidator.Validate(new AppSettings
        {
            InputDirectory = t["in"], OutputDirectory = t["in"] + Path.DirectorySeparatorChar, Mode = OperationMode.Copy,
        }, Resolver);
        Assert.Contains(r.Errors, e => e.Contains("同じ"));
    }

    [Fact]
    public void DryRun_DoesNotRequireOutput()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t["in"]);
        var r = SettingsValidator.Validate(new AppSettings { InputDirectory = t["in"], Mode = OperationMode.DryRun }, Resolver);
        Assert.True(r.IsValid);
    }

    [Fact]
    public void ReportsUnknownVariables_ReversedDates_AndMissingInput()
    {
        var r = SettingsValidator.Validate(new AppSettings
        {
            InputDirectory = "/no/such/dir",
            FolderTemplate = "{yyyy}/{month}",
            FileNameTemplate = "{date}",
            FilterStartDate = new DateTime(2024, 2, 1),
            FilterEndDate = new DateTime(2024, 1, 1),
        }, Resolver);
        Assert.Contains(r.Errors, e => e.Contains("見つかりません"));
        Assert.Contains(r.Errors, e => e.Contains("{month}"));
        Assert.Contains(r.Errors, e => e.Contains("{date}"));
        Assert.Contains(r.Errors, e => e.Contains("開始日"));
    }

    [Fact]
    public void WarnsWhenOutputInsideInput()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t["in"]);
        var r = SettingsValidator.Validate(new AppSettings
        {
            InputDirectory = t["in"], OutputDirectory = t["in/sorted"], Mode = OperationMode.Copy,
        }, Resolver);
        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains("中にあります"));
    }
}

public class ExifToolTests
{
    [Fact]
    public void ParsesJsonOutput_IncludingGeolocationAndTimezones()
    {
        var json = """
        [{"SourceFile":"/p/a.jpg","Make":"NIKON CORPORATION","Model":"NIKON Z 6_2",
          "DateTimeOriginal":"2024:03:01 10:20:30+09:00","LensID":"NIKKOR Z 24-70mm f/2.8 S",
          "ImageWidth":6048,"ImageHeight":4024,"Rating":2,"GPSLatitude":35.1,"GPSLongitude":139.2,
          "GeolocationCity":"Yokohama","GeolocationCountry":"Japan","GeolocationCountryCode":"JP"},
         {"SourceFile":"/p/bad.jpg","Error":"File format error"},
         {"SourceFile":"/p/zero.jpg","DateTimeOriginal":"0000:00:00 00:00:00","ModifyDate":"2020:01:01 00:00:00"}]
        """;
        var results = new Dictionary<string, MetadataResult>();
        ExifToolMetadataExtractor.ParseOutput(json, results);

        var a = results[Path.GetFullPath("/p/a.jpg")].Data!;
        Assert.Equal(new DateTime(2024, 3, 1, 10, 20, 30), a.DateTimeOriginal);
        Assert.Equal("NIKKOR Z 24-70mm f/2.8 S", a.LensModel);
        Assert.Equal("NIKON Z 6_2", a.CameraName);
        Assert.Equal(6048, a.ImageWidth);
        Assert.Equal("Yokohama", a.GeolocationCity);
        Assert.True(a.HasGps);

        Assert.Equal("File format error", results[Path.GetFullPath("/p/bad.jpg")].Error);

        var z = results[Path.GetFullPath("/p/zero.jpg")].Data!;
        Assert.Null(z.DateTimeOriginal);
        Assert.Equal("ModifyDate", z.UsedDateField);
    }

    [ExifToolFact]
    public async Task ReadsManyFilesInBatches_WithUnicodeNames_AndReportsMissingOnes()
    {
        using var t = new TempDir();
        var paths = Enumerable.Range(0, 95)
            .Select(i => TestImages.CreateJpeg(t[$"写真 {i:D3}.jpg"], "Canon", "Canon EOS R6", new DateTime(2021, 1, 1).AddHours(i)))
            .ToList();
        paths.Add(t["missing.jpg"]);

        var ex = new ExifToolMetadataExtractor("exiftool");
        Assert.NotNull(ex.GetVersion());
        int lastProgress = 0;
        var results = await ex.ExtractAsync(paths, 3, new Progress<int>(n => lastProgress = Math.Max(lastProgress, n)));

        Assert.Equal(96, results.Count);
        Assert.Equal(new DateTime(2021, 1, 1).AddHours(42), results[paths[42]].Data!.EffectiveDate);
        Assert.NotNull(results[t["missing.jpg"]].Error);
    }

    [Fact]
    public void GetVersion_ReturnsNull_WhenToolMissing()
    {
        Assert.Null(new ExifToolMetadataExtractor("/no/such/exiftool").GetVersion());
    }
}

public class CsvExporterTests
{
    [Fact]
    public void EscapesCommasQuotesAndFormulas()
    {
        Assert.Equal("\"a,b\"", CsvExporter.Escape("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExporter.Escape("say \"hi\""));
        Assert.Equal("'=SUM(A1)", CsvExporter.Escape("=SUM(A1)"));
        Assert.Equal("", CsvExporter.Escape(null));
    }

    [Fact]
    public void WritesHeaderAndOneRowPerAsset()
    {
        var a = TestImages.Asset("/p/x, y.jpg", new Domain.ValueObjects.ExifData
        {
            DateTimeOriginal = new DateTime(2024, 1, 2, 3, 4, 5), UsedDateField = "DateTimeOriginal",
        });
        a.NormalizedCamera = "Canon_EOS_R5";
        var sw = new StringWriter();
        CsvExporter.Write(sw, [a, TestImages.Asset("/p/b.jpg")], _ => "完了");
        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("状態,元のパス", lines[0]);
        Assert.Contains("\"/p/x, y.jpg\"", lines[1]);
        Assert.Contains("2024-01-02 03:04:05", lines[1]);
        Assert.Contains("ファイル日時", lines[2]);
    }
}
