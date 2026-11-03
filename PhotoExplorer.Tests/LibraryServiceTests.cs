using PhotoExplorer.Application;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.ValueObjects;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Tests;

public class LibraryServiceTests
{
    private static readonly Normalizer Normalizer = new();

    private static ImageAsset Photo(string source, string? output, AssetStatus status, string? model = null,
        DateTime? date = null, int? rating = null, string? country = null, string? city = null) => new()
    {
        SourcePath = source,
        OutputPath = output,
        Status = status,
        FileSize = 10,
        FileModifiedAt = new DateTime(2020, 1, 1),
        Exif = model == null && date == null ? null : new ExifData
        {
            Make = model == null ? null : "RICOH IMAGING COMPANY, LTD.",
            Model = model,
            DateTimeOriginal = date,
            Rating = rating,
            GeolocationCountry = country,
            GeolocationCity = city,
        },
    };

    [Fact]
    public void CurrentPath_PrefersOutputAfterProcessing_AndFallsBackToSource()
    {
        var existing = new HashSet<string> { "/out/a.jpg", "/in/b.jpg" };
        bool Exists(string p) => existing.Contains(p);

        var moved = Photo("/in/a.jpg", "/out/a.jpg", AssetStatus.Processed);
        Assert.Equal(("/out/a.jpg", true), LibraryService.ResolveCurrentPath(moved, Exists));

        // DRY_RUN の予定だけの出力先は使わない
        var planned = Photo("/in/b.jpg", "/out/b.jpg", AssetStatus.MetadataOk);
        Assert.Equal(("/in/b.jpg", true), LibraryService.ResolveCurrentPath(planned, Exists));

        var gone = Photo("/in/c.jpg", "/out/c.jpg", AssetStatus.Processed);
        Assert.Equal(("/out/c.jpg", false), LibraryService.ResolveCurrentPath(gone, Exists));
    }

    [Fact]
    public void Build_MergesRecordsPointingToTheSameFile_AndNormalizesCameraNames()
    {
        var assets = new[]
        {
            Photo("/in/R0001.JPG", "/out/R0001.JPG", AssetStatus.Processed, "RICOH GR IIIx", new DateTime(2024, 7, 1)),
            // 出力フォルダを別の実行で読み込んだ記録（同じファイル）
            Photo("/out/R0001.JPG", null, AssetStatus.MetadataOk, "RICOH GR IIIx", new DateTime(2024, 7, 1)),
        };
        var photos = LibraryService.Build(assets, Normalizer, _ => true);

        var p = Assert.Single(photos);
        Assert.Equal("/out/R0001.JPG", p.CurrentPath);
        Assert.Equal("RICOH_GR_IIIx", p.Camera);
    }

    [Fact]
    public void Filter_ByCameraRatingLocationDateAndSearch_AndSorts()
    {
        var assets = new[]
        {
            Photo("/p/gr1.jpg", null, AssetStatus.MetadataOk, "RICOH GR IIIx", new DateTime(2024, 7, 1), 5, "Japan", "Kyoto"),
            Photo("/p/gr2.jpg", null, AssetStatus.MetadataOk, "RICOH GR IIIx", new DateTime(2024, 8, 1), 2, "Japan", "Osaka"),
            Photo("/p/gr3.jpg", null, AssetStatus.Duplicate,  "RICOH GR IIIx", new DateTime(2024, 9, 1)),
            Photo("/p/sony.jpg", null, AssetStatus.MetadataOk, "ILCE-7M4", new DateTime(2023, 1, 1), 5, "France", "Paris"),
        };
        var missing = new HashSet<string> { "/p/gone.jpg" };
        var photos = LibraryService.Build(
            assets.Append(Photo("/p/gone.jpg", null, AssetStatus.MetadataOk, "RICOH GR IIIx", new DateTime(2024, 1, 1))),
            Normalizer, p => !missing.Contains(p));

        // GR だけ（重複と見つからないファイルは既定で除く）、新しい順
        var gr = LibraryService.Filter(photos, new LibraryCriteria { Camera = "RICOH_GR_IIIx" });
        Assert.Equal(["gr2.jpg", "gr1.jpg"], gr.Select(p => p.FileName));

        var withAll = LibraryService.Filter(photos, new LibraryCriteria
        {
            Camera = "RICOH_GR_IIIx", IncludeDuplicates = true, IncludeMissing = true, Sort = LibrarySort.OldestFirst,
        });
        Assert.Equal(["gone.jpg", "gr1.jpg", "gr2.jpg", "gr3.jpg"], withAll.Select(p => p.FileName));

        Assert.Equal(["sony.jpg", "gr1.jpg"],
            LibraryService.Filter(photos, new LibraryCriteria { MinRating = 5 }).Select(p => p.FileName).OrderByDescending(n => n));
        Assert.Equal(["gr2.jpg", "gr1.jpg"],
            LibraryService.Filter(photos, new LibraryCriteria { Location = "Japan" }).Select(p => p.FileName));
        Assert.Equal(["gr2.jpg"],
            LibraryService.Filter(photos, new LibraryCriteria { Location = "Japan / Osaka" }).Select(p => p.FileName));
        Assert.Equal(["gr1.jpg"], LibraryService.Filter(photos, new LibraryCriteria
        {
            From = new DateTime(2024, 6, 1), To = new DateTime(2024, 7, 1),
        }).Select(p => p.FileName));
        Assert.Equal(["sony.jpg"],
            LibraryService.Filter(photos, new LibraryCriteria { Search = "SONY" }).Select(p => p.FileName));
    }

    [Fact]
    public void Facet_CountsValues_MostCommonFirst_SkippingUnknown()
    {
        var photos = LibraryService.Build(new[]
        {
            Photo("/a.jpg", null, AssetStatus.MetadataOk, "RICOH GR IIIx"),
            Photo("/b.jpg", null, AssetStatus.MetadataOk, "RICOH GR IIIx"),
            Photo("/c.jpg", null, AssetStatus.MetadataOk, "ILCE-7M4"),
            Photo("/d.jpg", null, AssetStatus.NoMetadata),
        }, Normalizer, _ => true);

        Assert.Equal([("RICOH_GR_IIIx", 2), ("Sony_A7IV", 1)], LibraryService.Facet(photos, p => p.Camera));
    }

    [Fact]
    public async Task LoadAsync_ReadsWhatTheRepositorySaved()
    {
        using var t = new TempDir();
        var file = t.Write("in/x.jpg", "x");
        using var repo = new SqliteAssetRepository(t["db.sqlite"]);
        await repo.SaveAllAsync([Photo(file, null, AssetStatus.MetadataOk, "RICOH GR IIIx", new DateTime(2024, 1, 2))]);

        var photos = await new LibraryService(repo, Normalizer).LoadAsync();

        var p = Assert.Single(photos);
        Assert.True(p.Exists);
        Assert.Equal(new DateTime(2024, 1, 2), p.Date);
    }
}
