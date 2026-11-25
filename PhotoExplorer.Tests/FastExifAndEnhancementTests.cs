using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Domain.ValueObjects;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Tests;

public class FastExifAndEnhancementTests
{
    private static readonly byte[] MinimalJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");

    [ExifToolFact]
    public void FastExifReader_ReadsJpegExifAndXmpRating_WithoutExifTool()
    {
        using var t = new TempDir();
        var path = TestImages.CreateJpeg(
            t["sample.jpg"],
            make: "Canon",
            model: "Canon EOS R5",
            date: new DateTime(2024, 7, 15, 9, 5, 42),
            lens: "RF24-105mm F4 L IS USM",
            rating: 4);

        var res = FastExifReader.TryRead(path);

        Assert.NotNull(res);
        Assert.False(res.NeedsExifTool); // GPS なし・LensModel ありなので exiftool 不要
        Assert.Equal("Canon", res.Data.Make);
        Assert.Equal("Canon EOS R5", res.Data.Model);
        Assert.Equal("RF24-105mm F4 L IS USM", res.Data.LensModel);
        Assert.Equal(new DateTime(2024, 7, 15, 9, 5, 42), res.Data.DateTimeOriginal);
        Assert.Equal("DateTimeOriginal", res.Data.UsedDateField);
        Assert.Equal(4, res.Data.Rating);
        Assert.Equal(1, res.Data.ImageWidth);
        Assert.Equal(1, res.Data.ImageHeight);
    }

    [ExifToolFact]
    public void FastExifReader_DetectsGps_AndFlagsNeedsExifToolForGeolocation()
    {
        using var t = new TempDir();
        var path = TestImages.CreateJpeg(
            t["gps.jpg"],
            make: "Apple",
            model: "iPhone 15 Pro",
            date: new DateTime(2024, 8, 1, 12, 0, 0),
            lat: 35.6595,
            lon: 139.7005);

        var res = FastExifReader.TryRead(path);

        Assert.NotNull(res);
        Assert.True(res.Data.HasGps);
        Assert.InRange(res.Data.GPSLatitude!.Value, 35.65, 35.67);
        Assert.InRange(res.Data.GPSLongitude!.Value, 139.69, 139.71);
        Assert.True(res.NeedsExifTool); // GPS 地名変換のため exiftool に回す
    }

    [Fact]
    public void FastExifReader_ReadsPlainJpegDimensions_AndReturnsNullOnCorruptFiles()
    {
        using var t = new TempDir();
        var jpg = t["plain.jpg"];
        File.WriteAllBytes(jpg, MinimalJpeg);
        var res = FastExifReader.TryRead(jpg);
        Assert.NotNull(res);
        Assert.Equal(1, res.Data.ImageWidth);
        Assert.Equal(1, res.Data.ImageHeight);
        Assert.Null(res.Data.EffectiveDate);

        var bad = t.Write("corrupt.jpg", "not a jpeg");
        Assert.Null(FastExifReader.TryRead(bad));
    }

    [Fact]
    public void FastExifReader_ReadsTiffRawAndFujifilmRaf()
    {
        using var t = new TempDir();
        // 最小 TIFF (Little-Endian) を構築: Make="SONY", Model="ILCE-7M4", Orientation=6, ExifIFD(FocalLength=50/1, ISO=400, DateTimeOriginal)
        var tiffPath = t["photo.arw"];
        File.WriteAllBytes(tiffPath, BuildSyntheticTiff(previewSize: 0, orientation: 6));

        var res = FastExifReader.TryRead(tiffPath);
        Assert.NotNull(res);
        Assert.Equal("SONY", res.Data.Make);
        Assert.Equal("ILCE-7M4", res.Data.Model);
        Assert.Equal("50", res.Data.FocalLength);
        Assert.Equal("400", res.Data.ISO);
        Assert.Equal(new DateTime(2024, 5, 10, 14, 30, 0), res.Data.DateTimeOriginal);
        Assert.Equal(6, res.Orientation);

        // Fujifilm RAF ヘッダ（offset=128 に埋め込み JPEG）
        var rafPath = t["photo.raf"];
        File.WriteAllBytes(rafPath, BuildSyntheticRaf(MinimalJpeg));
        var rafRes = FastExifReader.TryRead(rafPath);
        Assert.NotNull(rafRes);
        Assert.Equal(1, rafRes.Data.ImageWidth);
    }

    [Fact]
    public void EmbeddedPreviewExtractor_ExtractsFromTiffRaw_Raf_AndCr3()
    {
        using var t = new TempDir();
        var fakeJpeg = CreatePaddedJpeg(2048);

        // TIFF RAW
        var arw = t["sample.arw"];
        File.WriteAllBytes(arw, BuildSyntheticTiff(previewSize: fakeJpeg.Length, orientation: 6, previewBytes: fakeJpeg));
        var p1 = EmbeddedPreviewExtractor.TryExtract(arw);
        Assert.NotNull(p1);
        Assert.Equal(fakeJpeg, p1.JpegBytes);
        Assert.Equal(6, p1.Orientation);

        // Fujifilm RAF
        var raf = t["sample.raf"];
        File.WriteAllBytes(raf, BuildSyntheticRaf(fakeJpeg));
        var p2 = EmbeddedPreviewExtractor.TryExtract(raf);
        Assert.NotNull(p2);
        Assert.Equal(fakeJpeg, p2.JpegBytes);

        // Canon CR3
        var cr3 = t["sample.cr3"];
        File.WriteAllBytes(cr3, BuildSyntheticCr3(fakeJpeg));
        var p3 = EmbeddedPreviewExtractor.TryExtract(cr3);
        Assert.NotNull(p3);
        Assert.Equal(fakeJpeg, p3.JpegBytes);

        // 通常の JPEG はそのまま WIC でデコードするため null
        var jpg = t["normal.jpg"];
        File.WriteAllBytes(jpg, fakeJpeg);
        Assert.Null(EmbeddedPreviewExtractor.TryExtract(jpg));
    }

    [Fact]
    public void PathResolver_ExpandsNewVariables_DddFocalIsoOrientation()
    {
        var resolver = new PathResolver(new Normalizer());
        var root = Path.Combine(Path.GetTempPath(), "pe_newvars");

        var landscape = TestImages.Asset(Path.Combine(root, "in", "a.jpg"), new ExifData
        {
            DateTimeOriginal = new DateTime(2024, 7, 15, 9, 5, 42), // Monday
            FocalLength = "50.2",
            ISO = "400",
            ImageWidth = 6000,
            ImageHeight = 4000,
        });
        var portrait = TestImages.Asset(Path.Combine(root, "in", "b.jpg"), new ExifData
        {
            DateTimeOriginal = new DateTime(2024, 7, 15, 9, 5, 42),
            ImageWidth = 4000,
            ImageHeight = 6000,
        });
        var square = TestImages.Asset(Path.Combine(root, "in", "c.jpg"), new ExifData
        {
            DateTimeOriginal = new DateTime(2024, 7, 15, 9, 5, 42),
            ImageWidth = 3000,
            ImageHeight = 3000,
        });

        Assert.Equal(Path.Combine(root, "Mon", "50mm", "ISO400", "Landscape", "a.jpg"),
            resolver.Resolve(landscape, new PathOptions(root, "{ddd}/{focal}/{iso}/{orientation}")));
        Assert.Equal(Path.Combine(root, "Portrait", "b.jpg"),
            resolver.Resolve(portrait, new PathOptions(root, "{orientation}")));
        Assert.Equal(Path.Combine(root, "Square", "c.jpg"),
            resolver.Resolve(square, new PathOptions(root, "{orientation}")));
        Assert.Empty(resolver.FindUnknownVariables("{ddd}/{focal}/{iso}/{orientation}"));
    }

    [Fact]
    public async Task OrganizeUseCase_SortsRealJpegWithFastReader_EvenWithoutExifTool()
    {
        using var t = new TempDir();
        // バイナリ合成した有効な TIFF-RAW を入力に置く（exiftool が無くても内蔵リーダーで読める）
        var rawPath = t["in/DSC0001.ARW"];
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
        File.WriteAllBytes(rawPath, BuildSyntheticTiff(previewSize: 0, orientation: 1));

        using var repo = new SqliteAssetRepository(t["db/assets.db"]);
        using var hist = new StatsHistoryRepository(t["db/history.db"]);
        using var journal = new OperationJournal(t["db/history.db"]);
        var norm = new Normalizer();
        var uc = new OrganizeUseCase(new FileScanner(), new ExifToolMetadataExtractor(), norm, new PathResolver(norm),
            new DuplicateDetector(), new FileOperator(), repo, journal, hist);

        var result = await uc.ExecuteAsync(new AppSettings
        {
            InputDirectory = t["in"],
            OutputDirectory = t["out"],
            FolderTemplate = "{yyyy}/{mm}/{camera}/{focal}",
            Mode = OperationMode.Copy,
            ExifToolPath = t["missing-exiftool"],
        });

        Assert.Null(result.ExifToolVersion);
        Assert.Equal(1, result.TotalProcessed);
        Assert.True(File.Exists(Path.Combine(t["out"], "2024", "05", "Sony_A7IV", "50mm", "DSC0001.ARW")));
    }

    [ExifToolFact]
    public async Task OrganizeUseCase_MinRatingFilter_AndCleanEmptySourceFolders()
    {
        using var t = new TempDir();
        TestImages.CreateJpeg(t["in/sub1/star5.jpg"], "Canon", "Canon EOS R5", new DateTime(2024, 7, 1), rating: 5);
        TestImages.CreateJpeg(t["in/sub2/star2.jpg"], "Canon", "Canon EOS R5", new DateTime(2024, 7, 2), rating: 2);
        // 中身を別にして重複扱いにしない
        File.AppendAllText(t["in/sub2/star2.jpg"], "diff");

        using var repo = new SqliteAssetRepository(t["db/assets.db"]);
        using var hist = new StatsHistoryRepository(t["db/history.db"]);
        using var journal = new OperationJournal(t["db/history.db"]);
        var norm = new Normalizer();
        var uc = new OrganizeUseCase(new FileScanner(), new ExifToolMetadataExtractor(), norm, new PathResolver(norm),
            new DuplicateDetector(), new FileOperator(), repo, journal, hist);

        var result = await uc.ExecuteAsync(new AppSettings
        {
            InputDirectory = t["in"],
            OutputDirectory = t["out"],
            FolderTemplate = "{yyyy}/{mm}",
            Mode = OperationMode.Move,
            MinRating = 4,
            CleanEmptySourceFolders = true,
            ExifToolPath = "exiftool",
        });

        Assert.Equal(1, result.TotalProcessed);
        Assert.Equal(1, result.TotalFiltered);
        Assert.True(File.Exists(Path.Combine(t["out"], "2024", "07", "star5.jpg")));
        Assert.False(Directory.Exists(t["in/sub1"])); // 空になった sub1 は削除される
        Assert.True(Directory.Exists(t["in/sub2"]));  // star2.jpg が残る sub2 は残る
        Assert.True(Directory.Exists(t["in"]));       // 入力ルート自体は残る
    }

    [Fact]
    public async Task DuplicateDetector_PrefersSidecarAndNonBackupFolder()
    {
        using var t = new TempDir();
        var content = new string('A', 2000);
        var backupFile = t.Write("backup/IMG_0001.ARW", content);
        var mainFile = t.Write("shoot/IMG_0001.ARW", content);
        var sidecar = t.Write("shoot/IMG_0001.xmp", "<xmp/>");

        var aBackup = TestImages.Asset(backupFile, size: content.Length);
        var aMain = TestImages.Asset(mainFile, size: content.Length);
        aMain.SidecarPath = sidecar;

        await new DuplicateDetector().DetectDuplicatesAsync([aBackup, aMain]);

        Assert.True(aMain.IsKeptInDuplicateGroup);
        Assert.False(aBackup.IsKeptInDuplicateGroup);
        Assert.Equal(AssetStatus.Duplicate, aBackup.Status);
    }

    [Fact]
    public void LibraryService_SupportsNewSorts_AndSearchesCameraLensLocation()
    {
        var norm = new Normalizer();
        var assets = new[]
        {
            new ImageAsset
            {
                SourcePath = "/p/a.jpg", FileSize = 100, FileModifiedAt = new DateTime(2024, 1, 1),
                Status = AssetStatus.MetadataOk,
                Exif = new ExifData { Make = "Canon", Model = "Canon EOS R5", LensModel = "RF50mm F1.8 STM", Rating = 2, GeolocationCity = "Kyoto", GeolocationCountry = "Japan" },
            },
            new ImageAsset
            {
                SourcePath = "/p/b.jpg", FileSize = 500, FileModifiedAt = new DateTime(2024, 1, 2),
                Status = AssetStatus.MetadataOk,
                Exif = new ExifData { Make = "SONY", Model = "ILCE-7M4", LensModel = "FE 24-70mm F2.8 GM II", Rating = 5, GeolocationCity = "Paris", GeolocationCountry = "France" },
            },
        };
        var photos = LibraryService.Build(assets, norm, _ => true);

        Assert.Equal(["b.jpg", "a.jpg"], LibraryService.Filter(photos, new LibraryCriteria { Sort = LibrarySort.RatingDesc }).Select(p => p.FileName));
        Assert.Equal(["b.jpg", "a.jpg"], LibraryService.Filter(photos, new LibraryCriteria { Sort = LibrarySort.FileSizeDesc }).Select(p => p.FileName));
        Assert.Equal(["a.jpg"], LibraryService.Filter(photos, new LibraryCriteria { Search = "Kyoto" }).Select(p => p.FileName));
        Assert.Equal(["b.jpg"], LibraryService.Filter(photos, new LibraryCriteria { Search = "GM2" }).Select(p => p.FileName));
    }

    [Fact]
    public void ScanStatistics_BuildsFocalLengthAndIsoBreakdowns()
    {
        var assets = new[]
        {
            TestImages.Asset("/a.jpg", new ExifData { FocalLength = "50", ISO = "400" }),
            TestImages.Asset("/b.jpg", new ExifData { FocalLength = "50.1", ISO = "400" }),
            TestImages.Asset("/c.jpg", new ExifData { FocalLength = "24", ISO = "1600" }),
        };
        var stats = ScanStatistics.Build(assets, 0);
        Assert.Equal([("50mm", 2), ("24mm", 1)], stats.FocalLengthBreakdown);
        Assert.Equal([("ISO400", 2), ("ISO1600", 1)], stats.IsoBreakdown);
    }

    [Fact]
    public async Task SqliteAssetRepository_IndexedChunkedLoadByPaths_HandlesOver1200Rows()
    {
        using var t = new TempDir();
        using var repo = new SqliteAssetRepository(t["assets.db"]);
        var all = Enumerable.Range(0, 1250).Select(i => new ImageAsset
        {
            SourcePath = $"/photos/dir_{i / 50}/IMG_{i:D4}.JPG",
            FileSize = 1000 + i,
            FileCreatedAt = new DateTime(2024, 1, 1),
            FileModifiedAt = new DateTime(2024, 1, 2),
            MetadataExtracted = true,
            Exif = new ExifData { Make = "Canon", Model = "Canon EOS R5", DateTimeOriginal = new DateTime(2024, 1, 1).AddMinutes(i) },
            Status = AssetStatus.Processed,
        }).ToList();

        await repo.SaveAllAsync(all);

        // 500 件バッチを跨ぐ 700 件を検索
        var queryPaths = all.Where((_, idx) => idx % 2 == 0).Select(a => a.SourcePath).Append("/photos/missing.jpg").ToList();
        var loaded = await repo.LoadByPathsAsync(queryPaths);

        Assert.Equal(625, loaded.Count);
        Assert.Equal(1004, loaded["/photos/dir_0/IMG_0004.JPG"].FileSize);
        Assert.Equal("Canon EOS R5", loaded["/photos/dir_24/IMG_1248.JPG"].Exif!.Model);
    }

    [Fact]
    public void FastExifReader_Processes500FilesQuickly()
    {
        using var t = new TempDir();
        var tiffBytes = BuildSyntheticTiff(previewSize: 0, orientation: 1);
        var paths = Enumerable.Range(0, 500).Select(i =>
        {
            var p = t[$"img_{i:D4}.arw"];
            File.WriteAllBytes(p, tiffBytes);
            return p;
        }).ToList();

        var sw = Stopwatch.StartNew();
        int ok = 0;
        Parallel.ForEach(paths, p =>
        {
            if (FastExifReader.TryRead(p)?.Data.Model == "ILCE-7M4")
                Interlocked.Increment(ref ok);
        });
        sw.Stop();

        Assert.Equal(500, ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Expected < 30s, took {sw.Elapsed}");
    }

    // ===================== テスト用バイナリ合成ヘルパー =====================

    private static byte[] CreatePaddedJpeg(int targetSize)
    {
        var buf = new byte[Math.Max(targetSize, MinimalJpeg.Length)];
        MinimalJpeg.CopyTo(buf, 0);
        // 末尾に EOI (FF D9) を置く
        buf[^2] = 0xFF;
        buf[^1] = 0xD9;
        return buf;
    }

    private static byte[] BuildSyntheticRaf(byte[] embeddedJpeg)
    {
        const uint offset = 128;
        var buf = new byte[offset + embeddedJpeg.Length];
        "FUJIFILMCCD-RAW "u8.CopyTo(buf);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(84, 4), offset);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(88, 4), (uint)embeddedJpeg.Length);
        embeddedJpeg.CopyTo(buf, (int)offset);
        return buf;
    }

    private static byte[] BuildSyntheticCr3(byte[] embeddedJpeg)
    {
        using var ms = new MemoryStream();
        void WriteU32BE(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            ms.Write(b);
        }

        // ftyp box (20 bytes): size=20, "ftyp", "crx ", 0, "crx "
        WriteU32BE(20);
        ms.Write("ftypcrx "u8);
        WriteU32BE(0);
        ms.Write("crx "u8);

        // PRVW sub-box: 24-byte header + jpeg
        uint prvwSize = (uint)(24 + embeddedJpeg.Length);
        // uuid box: 8-byte header + 16-byte uuid + prvwSize
        uint uuidSize = 8 + 16 + prvwSize;
        WriteU32BE(uuidSize);
        ms.Write("uuid"u8);
        ms.Write([0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16]);

        WriteU32BE(prvwSize);
        ms.Write("PRVW"u8);
        ms.Write(new byte[12]); // flags/width/height/unknown
        WriteU32BE((uint)embeddedJpeg.Length);
        ms.Write(embeddedJpeg);
        return ms.ToArray();
    }

    private static byte[] BuildSyntheticTiff(int previewSize, ushort orientation, byte[]? previewBytes = null)
    {
        // Little-endian TIFF with IFD0 + ExifIFD (+ optional embedded JPEG preview)
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Header (8 bytes)
        bw.Write((byte)'I'); bw.Write((byte)'I');
        bw.Write((ushort)42);
        bw.Write((uint)8); // IFD0 offset = 8

        bool hasPreview = previewBytes != null && previewSize > 0;
        ushort ifd0Count = (ushort)(hasPreview ? 6 : 4);
        long ifd0Size = 2 + ifd0Count * 12 + 4;
        long dataOffset = 8 + ifd0Size;

        // Data area layout:
        // makeBytes ("SONY\0") -> 6 bytes (padded)
        // modelBytes ("ILCE-7M4\0") -> 10 bytes (padded)
        // exifIfd offset
        var makeBytes = "SONY\0\0"u8.ToArray();
        var modelBytes = "ILCE-7M4\0\0"u8.ToArray();
        var dateBytes = "2024:05:10 14:30:00\0"u8.ToArray();

        uint makeOff = (uint)dataOffset;
        uint modelOff = makeOff + (uint)makeBytes.Length;
        uint exifIfdOff = modelOff + (uint)modelBytes.Length;
        // ExifIFD has 3 entries: DateTimeOriginal, FocalLength (rational), ISO (short)
        uint exifIfdSize = 2 + 3 * 12 + 4;
        uint dateOff = exifIfdOff + exifIfdSize;
        uint focalOff = dateOff + (uint)dateBytes.Length;
        uint previewOff = focalOff + 8;

        // Write IFD0
        bw.Write(ifd0Count);
        void Entry(ushort tag, ushort type, uint count, uint valOrOff)
        {
            bw.Write(tag); bw.Write(type); bw.Write(count); bw.Write(valOrOff);
        }
        Entry(0x010F, 2, 5, makeOff);
        Entry(0x0110, 2, 9, modelOff);
        Entry(0x0112, 3, 1, orientation);
        if (hasPreview)
        {
            Entry(0x0201, 4, 1, previewOff);
            Entry(0x0202, 4, 1, (uint)previewBytes!.Length);
        }
        Entry(0x8769, 4, 1, exifIfdOff);
        bw.Write((uint)0); // next IFD = 0

        // Strings
        bw.Write(makeBytes);
        bw.Write(modelBytes);

        // ExifIFD
        bw.Write((ushort)3);
        Entry(0x8827, 3, 1, 400);       // ISO = 400
        Entry(0x9003, 2, 20, dateOff);  // DateTimeOriginal
        Entry(0x920A, 5, 1, focalOff);  // FocalLength = 50/1
        bw.Write((uint)0);

        bw.Write(dateBytes);
        bw.Write((uint)50); bw.Write((uint)1); // FocalLength rational 50/1

        if (hasPreview)
            bw.Write(previewBytes!);

        bw.Flush();
        return ms.ToArray();
    }
}
