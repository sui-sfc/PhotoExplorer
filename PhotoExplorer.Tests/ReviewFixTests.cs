using PhotoExplorer.Application;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Tests;

/// <summary>PR #5 のレビューで見つかった問題の再発防止</summary>
public class ReviewFixTests
{
    [Theory]
    [InlineData(6000, 4000, null, "Landscape")]
    [InlineData(6000, 4000, 1, "Landscape")]
    [InlineData(6000, 4000, 6, "Portrait")] // カメラの縦位置写真は横長ピクセル + Orientation 6/8
    [InlineData(6000, 4000, 8, "Portrait")]
    [InlineData(4000, 6000, 6, "Landscape")]
    [InlineData(4000, 4000, 6, "Square")]
    public void Orientation_UsesExifRotation(int w, int h, int? orientation, string expected) =>
        Assert.Equal(expected, PathResolver.FormatOrientation(w, h, orientation));

    [Fact]
    public void PreviewDetection_AcceptsBaselineJpeg_RejectsLosslessRawData()
    {
        // ベースライン（SOF0）: FF D8 / APP0 / SOF0
        byte[] baseline = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08];
        // CR2 の RAW 本体のようなロスレス JPEG（SOF3）
        byte[] lossless = [0xFF, 0xD8, 0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC3, 0x00, 0x11, 0x08];

        using var a = new MemoryStream(baseline.Concat(new byte[64]).ToArray());
        using var b = new MemoryStream(lossless.Concat(new byte[64]).ToArray());
        Assert.True(EmbeddedPreviewExtractor.IsDisplayableJpeg(a, 0, a.Length));
        Assert.False(EmbeddedPreviewExtractor.IsDisplayableJpeg(b, 0, b.Length));
    }

    [ExifToolFact]
    public async Task RunWithoutExifTool_DoesNotCacheGpsFilesAsComplete()
    {
        using var t = new TempDir();
        TestImages.CreateJpeg(t["in/gps.jpg"], "Canon", "Canon EOS R5", new DateTime(2024, 7, 1), lat: 35.0116, lon: 135.7681);
        TestImages.CreateJpeg(t["in/plain.jpg"], "Canon", "Canon EOS R5", new DateTime(2024, 7, 2));

        using var repo = new SqliteAssetRepository(t["db/a.db"]);
        using var history = new StatsHistoryRepository(t["db/h.db"]);
        using var journal = new OperationJournal(t["db/h.db"]);
        var normalizer = new Normalizer();
        var useCase = new OrganizeUseCase(new FileScanner(), new ExifToolMetadataExtractor(), normalizer,
            new PathResolver(normalizer), new DuplicateDetector(), new FileOperator(), repo, journal, history);

        var settings = new AppSettings
        {
            InputDirectory = t["in"], Mode = OperationMode.DryRun, FolderTemplate = "{yyyy}",
            ExifToolPath = t["no-such-exiftool"],
        };
        var r = await useCase.ExecuteAsync(settings);
        Assert.Null(r.ExifToolVersion);

        var saved = await repo.LoadByPathsAsync([t["in/gps.jpg"], t["in/plain.jpg"]]);
        // GPS 付きは exiftool で地名を読む必要があるので「読み取り完了」として記録しない
        Assert.False(saved[t["in/gps.jpg"]].MetadataExtracted);
        Assert.True(saved[t["in/plain.jpg"]].MetadataExtracted);
        // それでも今回の整理には内蔵リーダーの撮影日を使う
        Assert.Equal(new DateTime(2024, 7, 1), saved[t["in/gps.jpg"]].Exif?.EffectiveDate);
    }
}
