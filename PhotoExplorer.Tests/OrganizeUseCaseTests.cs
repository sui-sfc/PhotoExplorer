using PhotoExplorer.Application;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Tests;

public class OrganizeUseCaseTests
{
    private sealed class Harness : IDisposable
    {
        public TempDir Dir { get; } = new();
        public SqliteAssetRepository Repo { get; }
        public StatsHistoryRepository History { get; }
        public OperationJournal Journal { get; }
        public OrganizeUseCase UseCase { get; }
        public UndoService Undo { get; }

        public Harness()
        {
            Repo = new SqliteAssetRepository(Dir["db/assets.db"]);
            History = new StatsHistoryRepository(Dir["db/history.db"]);
            Journal = new OperationJournal(Dir["db/history.db"]);
            var normalizer = new Normalizer();
            UseCase = new OrganizeUseCase(
                new FileScanner(), new ExifToolMetadataExtractor(), normalizer, new PathResolver(normalizer),
                new DuplicateDetector(), new FileOperator(), Repo, Journal, History);
            Undo = new UndoService(Journal, History);
        }

        public AppSettings Settings(OperationMode mode) => new()
        {
            InputDirectory = Dir["in"],
            OutputDirectory = Dir["out"],
            TrashDirectory = Dir["trash"],
            FolderTemplate = "{yyyy}/{mm}/{camera}",
            Mode = mode,
            MaxDegreeOfParallelism = 2,
            ExifToolPath = "exiftool",
        };

        /// <summary>カメラ2台・重複1組・サイドカー・EXIFなしのファイルを含むサンプル</summary>
        public void CreateSamples()
        {
            TestImages.CreateJpeg(Dir["in/card1/R0001.JPG"], "RICOH IMAGING COMPANY, LTD.", "RICOH GR IIIx", new DateTime(2024, 7, 15, 9, 5, 42));
            TestImages.CreateJpeg(Dir["in/card1/R0002.JPG"], "RICOH IMAGING COMPANY, LTD.", "RICOH GR IIIx", new DateTime(2024, 7, 16, 18, 0, 0), rating: 5);
            File.WriteAllText(Dir["in/card1/R0002.xmp"], "<x:xmpmeta/>");
            TestImages.CreateJpeg(Dir["in/card2/DSC0001.JPG"], "SONY", "ILCE-7M4", new DateTime(2023, 12, 31, 23, 59, 0));
            File.Copy(Dir["in/card1/R0001.JPG"], Dir["in/backup/R0001 (1).JPG"].Also(Directory.CreateDirectory));
            TestImages.CreateJpeg(Dir["in/写真 フォルダ/ねこ.jpg"], "Canon", "Canon EOS R5", new DateTime(2022, 2, 22, 22, 22, 22));
            var noExif = TestImages.CreateJpeg(Dir["in/misc/scan.jpg"]);
            File.AppendAllText(noExif, "unique"); // 他と内容を変える
            File.SetLastWriteTime(noExif, new DateTime(2010, 10, 10));
            File.SetCreationTime(noExif, new DateTime(2010, 10, 10));
        }

        public void Dispose()
        {
            Repo.Dispose();
            History.Dispose();
            Journal.Dispose();
            Dir.Dispose();
        }
    }

    [ExifToolFact]
    public async Task DryRun_PlansEverything_ButTouchesNothing()
    {
        using var h = new Harness();
        h.CreateSamples();
        var before = Directory.GetFiles(h.Dir["in"], "*", SearchOption.AllDirectories).Length;

        var r = await h.UseCase.ExecuteAsync(h.Settings(OperationMode.DryRun));

        Assert.False(Directory.Exists(h.Dir["out"]));
        Assert.Equal(before, Directory.GetFiles(h.Dir["in"], "*", SearchOption.AllDirectories).Length);
        Assert.Equal(6, r.TotalScanned);
        Assert.Equal(1, r.TotalDuplicates);
        Assert.Equal(0, r.TotalErrors);
        Assert.Single(r.DuplicateGroups);
        Assert.Equal("R0001.JPG", r.DuplicateGroups[0].Files[0].FileName); // "(1)" 付きではなく元の名前を残す

        var gr = r.Assets.First(a => a.FileName == "R0001.JPG");
        Assert.Equal(Path.Combine(h.Dir["out"], "2024", "07", "RICOH_GR_IIIx", "R0001.JPG"), gr.OutputPath);
        var neko = r.Assets.First(a => a.FileName == "ねこ.jpg");
        Assert.Equal("Canon_EOS_R5", neko.NormalizedCamera); // 日本語パスでも EXIF を読める
        var scan = r.Assets.First(a => a.FileName == "scan.jpg");
        Assert.Equal(AssetStatus.PartialMetadata, scan.Status); // 画像サイズはあるが撮影日はない
        Assert.False(scan.HasExifDate);
        Assert.Contains(Path.Combine("2010", "10"), scan.OutputPath);

        Assert.Equal(("RICOH_GR_IIIx", 3), r.Statistics.CameraBreakdown[0]);
        Assert.Equal(2, r.Statistics.HourBreakdown.Single(x => x.Label == "9").Count); // 重複コピーも同時刻
        Assert.Contains(r.Log, l => l.Message.Contains("DRY_RUN"));
    }

    [ExifToolFact]
    public async Task Copy_OrganizesFiles_TrashesDuplicates_AndSecondRunSkipsEverything()
    {
        using var h = new Harness();
        h.CreateSamples();

        var r1 = await h.UseCase.ExecuteAsync(h.Settings(OperationMode.Copy));
        Assert.Equal(0, r1.TotalErrors);
        Assert.Equal(5, r1.TotalProcessed);
        Assert.True(File.Exists(Path.Combine(h.Dir["out"], "2024", "07", "RICOH_GR_IIIx", "R0002.JPG")));
        Assert.True(File.Exists(Path.Combine(h.Dir["out"], "2024", "07", "RICOH_GR_IIIx", "R0002.xmp")));
        Assert.True(File.Exists(Path.Combine(h.Dir["out"], "2023", "12", "Sony_A7IV", "DSC0001.JPG")));
        Assert.True(File.Exists(Path.Combine(h.Dir["trash"], "R0001 (1).JPG")));
        Assert.True(File.Exists(h.Dir["in/card1/R0001.JPG"])); // コピーなので元は残る

        // 2 回目: メタデータはキャッシュから、ファイルは全てスキップ
        var r2 = await h.UseCase.ExecuteAsync(h.Settings(OperationMode.Copy));
        Assert.Equal(0, r2.TotalProcessed);
        Assert.Equal(5, r2.TotalSkipped);
        Assert.Contains(r2.Log, l => l.Message.Contains("前回の結果を再利用"));
        Assert.False(File.Exists(Path.Combine(h.Dir["out"], "2024", "07", "RICOH_GR_IIIx", "R0002_001.JPG")));
    }

    [ExifToolFact]
    public async Task Move_ThenUndo_RestoresEveryFile()
    {
        using var h = new Harness();
        h.CreateSamples();
        var original = Directory.GetFiles(h.Dir["in"], "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);

        var r = await h.UseCase.ExecuteAsync(h.Settings(OperationMode.Move));
        Assert.Equal(0, r.TotalErrors);
        Assert.Empty(Directory.GetFiles(h.Dir["in"], "*", SearchOption.AllDirectories));

        var history = h.History.LoadHistory()[0];
        Assert.Equal(r.RunId, history.RunId);
        Assert.Equal(7, history.OperationCount); // 5 本体 + サイドカー 1 + 退避 1

        var undo = await h.Undo.UndoAsync(r.RunId, h.Dir["out"]);
        Assert.Equal(7, undo.Restored);
        Assert.Equal(0, undo.Failed);
        foreach (var (path, bytes) in original)
            Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFileSystemEntries(h.Dir["out"])); // 空フォルダも片付く
        Assert.NotNull(h.History.LoadHistory()[0].UndoneAt);
    }

    [ExifToolFact]
    public async Task DateFilter_CountsAsFiltered_NotErrors()
    {
        using var h = new Harness();
        h.CreateSamples();
        var s = h.Settings(OperationMode.DryRun);
        s.FilterStartDate = new DateTime(2024, 1, 1);

        var r = await h.UseCase.ExecuteAsync(s);

        Assert.Equal(0, r.TotalErrors);
        Assert.Equal(3, r.TotalFiltered); // 2023, 2022, 2010
        Assert.Equal(3, r.Statistics.Filtered);
        Assert.All(r.Assets.Where(a => a.Status == AssetStatus.Filtered), a => Assert.True(a.BestDate.Year < 2024));
    }

    [Fact]
    public async Task MissingExifTool_FallsBackToFileDates()
    {
        using var h = new Harness();
        var p = h.Dir.Write("in/a.jpg", "abc");
        File.SetLastWriteTime(p, new DateTime(2015, 6, 7));
        File.SetCreationTime(p, new DateTime(2015, 6, 7));
        var s = h.Settings(OperationMode.Copy);
        s.ExifToolPath = h.Dir["no-such-exiftool"];

        var r = await h.UseCase.ExecuteAsync(s);

        Assert.Null(r.ExifToolVersion);
        Assert.Contains(r.Log, l => l.Level == LogLevel.Warning && l.Message.Contains("exiftool"));
        Assert.True(File.Exists(Path.Combine(h.Dir["out"], "2015", "06", "Unknown", "a.jpg")));
    }

    [Fact]
    public async Task Cancellation_ReturnsPartialResult()
    {
        using var h = new Harness();
        for (int i = 0; i < 50; i++) h.Dir.Write($"in/{i:D3}.jpg", $"file {i}");
        var s = h.Settings(OperationMode.Copy);
        s.ExifToolPath = h.Dir["no-such-exiftool"];
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<OrganizeProgress>(p =>
        {
            if (p.Phase == RunPhase.Transferring && p.Done >= 5) cts.Cancel();
        });

        var r = await h.UseCase.ExecuteAsync(s, progress, null, cts.Token);

        Assert.True(r.Cancelled);
        Assert.InRange(r.TotalProcessed, 1, 49);
        Assert.Contains(r.Log, l => l.Message.Contains("中断"));
    }

    [Fact]
    public void SidecarDestination_FollowsRenamedMainFile()
    {
        Assert.Equal("/o/20240101_IMG.CR3.xmp",
            OrganizeUseCase.SidecarDestination("/i/IMG.CR3.xmp", "/i/IMG.CR3", "/o/20240101_IMG.CR3"));
        Assert.Equal("/o/20240101_IMG.xmp",
            OrganizeUseCase.SidecarDestination("/i/IMG.xmp", "/i/IMG.CR3", "/o/20240101_IMG.CR3"));
    }

    /// <summary>コールバックを同期実行する IProgress（テスト用）</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

internal static class TestExtensions
{
    public static string Also(this string path, Func<string, DirectoryInfo> action)
    {
        action(Path.GetDirectoryName(path)!);
        return path;
    }
}
