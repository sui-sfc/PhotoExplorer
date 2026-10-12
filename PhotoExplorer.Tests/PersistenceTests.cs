using PhotoExplorer.Application;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.ValueObjects;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task AssetRepository_RoundTrips_IncludingDateOnlyExif()
    {
        using var t = new TempDir();
        var repo = new SqliteAssetRepository(t["assets.db"]);
        var a = new ImageAsset
        {
            SourcePath = "/x/a.jpg",
            FileSize = 123,
            FileCreatedAt = new DateTime(2020, 1, 1, 1, 1, 1),
            FileModifiedAt = new DateTime(2020, 2, 2, 2, 2, 2),
            FullHash = "ABC",
            MetadataExtracted = true,
            // 日付だけの EXIF も失われないこと
            Exif = new ExifData { DateTimeOriginal = new DateTime(2018, 5, 6, 7, 8, 9), Rating = 3, ImageWidth = 6000 },
            Status = AssetStatus.Processed,
        };
        await repo.SaveAllAsync([a]);
        a.Status = AssetStatus.Skipped;
        await repo.SaveAllAsync([a]); // UPSERT

        var loaded = await repo.LoadByPathsAsync(["/x/a.jpg", "/x/none.jpg"]);
        var b = Assert.Single(loaded).Value;
        Assert.Equal(new DateTime(2018, 5, 6, 7, 8, 9), b.Exif!.EffectiveDate);
        Assert.Equal(3, b.Exif.Rating);
        Assert.Equal(6000, b.Exif.ImageWidth);
        Assert.Equal(a.FileModifiedAt, b.FileModifiedAt);
        Assert.True(b.MetadataExtracted);
        Assert.Equal(AssetStatus.Skipped, b.Status);
        repo.Dispose();
    }

    [Fact]
    public void StatsHistory_SavesRunWithAllBreakdownTables_AndJournalCounts()
    {
        using var t = new TempDir();
        var db = t["history.db"];
        using var history = new StatsHistoryRepository(db);
        using var journal = new OperationJournal(db);

        var stats = new ScanStatistics
        {
            TotalFiles = 3, JpegCount = 2, RawCount = 1,
            CameraBreakdown = [("Canon_EOS_R5", 2), ("Sony_A7IV", 1)],
            ExtBreakdown = [("jpg", 2), ("arw", 1)],
            LensBreakdown = [("RF24-105", 2)],
        };
        history.SaveRun(new RunSummary("run1", DateTime.Now, OperationMode.Copy, "/in", "/out", stats, 3, 0, 0, 0, 1.5, false));
        journal.Record("run1", OperationKind.Copy, "/in/a", "/out/a", "H", 1);
        journal.Record("run1", OperationKind.Copy, "/in/b", "/out/b", "H", 1);

        var h = Assert.Single(history.LoadHistory());
        Assert.Equal("run1", h.RunId);
        Assert.Equal(2, h.OperationCount);
        Assert.Equal("Copy", h.Mode);
        Assert.Equal(2, history.LoadCameraTotals().First(c => c.Label == "Canon_EOS_R5").Count);

        history.MarkUndone("run1", DateTime.Now);
        Assert.NotNull(history.LoadHistory()[0].UndoneAt);

        history.ClearHistory();
        Assert.Empty(history.LoadHistory());
        Assert.Empty(journal.GetOperations("run1"));
    }

    [Fact]
    public void UserPreferences_SaveAndLoad_AndSurviveCorruptFile()
    {
        using var t = new TempDir();
        var path = t["prefs.json"];
        var p = new UserPreferences { InputDirectory = "/photos", FolderTemplate = "{yyyy}", VerifyCopies = false };
        Assert.True(p.Save(path));
        var loaded = UserPreferences.Load(path);
        Assert.Equal("/photos", loaded.InputDirectory);
        Assert.False(loaded.VerifyCopies);

        File.WriteAllText(path, "{ not json");
        var fallback = UserPreferences.Load(path);
        Assert.Equal(string.Empty, fallback.InputDirectory);
        Assert.True(File.Exists(path + ".bak"));
    }
}
