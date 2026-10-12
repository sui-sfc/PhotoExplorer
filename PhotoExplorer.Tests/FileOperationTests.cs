using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Tests;

public class FileOperatorTests
{
    private readonly FileOperator _op = new();

    [Fact]
    public async Task Copy_VerifiesAndKeepsTimestamp()
    {
        using var t = new TempDir();
        var src = t.Write("in/a.jpg", "hello");
        var when = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, when);

        var r = await _op.CopyAsync(src, t["out/2019/a.jpg"], verify: true);

        Assert.Equal(FileOpOutcome.Done, r.Outcome);
        Assert.Equal("hello", File.ReadAllText(r.Path));
        Assert.Equal(when, File.GetLastWriteTimeUtc(r.Path));
        Assert.NotNull(r.Hash);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task Copy_SkipsWhenIdenticalFileExists()
    {
        using var t = new TempDir();
        var src = t.Write("in/a.jpg", "same");
        t.Write("out/a.jpg", "same");

        var r = await _op.CopyAsync(src, t["out/a.jpg"], verify: true);

        Assert.Equal(FileOpOutcome.AlreadyExists, r.Outcome);
        Assert.False(File.Exists(t["out/a_001.jpg"]));
    }

    [Fact]
    public async Task Copy_RenamesWhenDifferentFileExists_AndRecognisesPreviousNumberedCopy()
    {
        using var t = new TempDir();
        var src = t.Write("in/a.jpg", "new");
        t.Write("out/a.jpg", "old");

        var r1 = await _op.CopyAsync(src, t["out/a.jpg"], verify: true);
        Assert.Equal(t["out/a_001.jpg"], r1.Path);
        Assert.Equal("old", File.ReadAllText(t["out/a.jpg"]));

        // 2 回目の実行では _001 が同一内容なのでスキップ
        var r2 = await _op.CopyAsync(src, t["out/a.jpg"], verify: true);
        Assert.Equal(FileOpOutcome.AlreadyExists, r2.Outcome);
        Assert.Equal(t["out/a_001.jpg"], r2.Path);
    }

    [Fact]
    public async Task Copy_FailsAndCleansUp_WhenSourceHashDoesNotMatch()
    {
        using var t = new TempDir();
        var src = t.Write("in/a.jpg", "content");

        await Assert.ThrowsAsync<IOException>(() =>
            _op.CopyAsync(src, t["out/a.jpg"], verify: true, knownSourceHash: new string('0', 64)));
        Assert.False(File.Exists(t["out/a.jpg"]));
    }

    [Fact]
    public async Task Move_LeavesSourceWhenIdenticalExists()
    {
        using var t = new TempDir();
        var src = t.Write("in/a.jpg", "same");
        t.Write("out/a.jpg", "same");

        var r = await _op.MoveAsync(src, t["out/a.jpg"], verify: true);

        Assert.Equal(FileOpOutcome.AlreadyExists, r.Outcome);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task Move_RemovesSource()
    {
        using var t = new TempDir();
        var src = t.Write("in/a.jpg", "x");
        var r = await _op.MoveAsync(src, t["out/sub/a.jpg"], verify: true);
        Assert.Equal(FileOpOutcome.Done, r.Outcome);
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(t["out/sub/a.jpg"]));
    }

    [Fact]
    public async Task Trash_NeverSkipsAndNumbers()
    {
        using var t = new TempDir();
        var a = t.Write("in/1/a.jpg", "same");
        var b = t.Write("in/2/a.jpg", "same");
        var r1 = await _op.MoveToTrashAsync(a, t["trash"]);
        var r2 = await _op.MoveToTrashAsync(b, t["trash"]);
        Assert.Equal(t["trash/a.jpg"], r1.Path);
        Assert.Equal(t["trash/a_001.jpg"], r2.Path);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Theory]
    [InlineData("/x/IMG_1.jpg", "/x/IMG_1_002.jpg")]
    [InlineData("/x/IMG_1.CR3.xmp", "/x/IMG_1_002.CR3.xmp")]
    [InlineData("/x/IMG_1.xmp", "/x/IMG_1_002.xmp")]
    public void Numbered(string input, string expected) =>
        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar),
            FileOperator.Numbered(input.Replace('/', Path.DirectorySeparatorChar), 2));
}

public class DuplicateDetectorTests
{
    [Fact]
    public async Task GroupsIdenticalFiles_KeepsOriginalOverCopyLikeName()
    {
        using var t = new TempDir();
        var big = new string('x', 100_000);
        var a = t.Write("a/IMG_0001.jpg", big);
        var b = t.Write("b/IMG_0001 (1).jpg", big);
        var c = t.Write("c/IMG_0002.jpg", big[..^1] + "y"); // 同サイズ・先頭も同じだが末尾が違う
        var assets = new[] { a, b, c }.Select(p => TestImages.Asset(p, size: new FileInfo(p).Length)).ToList();

        await new DuplicateDetector().DetectDuplicatesAsync(assets);

        Assert.Equal(assets[0].DuplicateGroupId, assets[1].DuplicateGroupId);
        Assert.NotNull(assets[0].DuplicateGroupId);
        Assert.True(assets[0].IsKeptInDuplicateGroup);
        Assert.Equal(AssetStatus.Duplicate, assets[1].Status);
        Assert.Null(assets[2].DuplicateGroupId);
    }

    [Fact]
    public async Task UnreadableFiles_AreNotTreatedAsDuplicatesOfEachOther()
    {
        using var t = new TempDir();
        // 存在しないファイル（ハッシュ計算に失敗する）を同サイズで 2 件
        var assets = new[] { t["gone1.jpg"], t["gone2.jpg"] }.Select(p => TestImages.Asset(p, size: 500)).ToList();

        await new DuplicateDetector().DetectDuplicatesAsync(assets);

        Assert.All(assets, a => Assert.Null(a.DuplicateGroupId));
        Assert.All(assets, a => Assert.NotEqual(AssetStatus.Duplicate, a.Status));
    }
}

public class FileScannerTests
{
    [Fact]
    public async Task FindsSupportedFiles_SkipsExcludedDirs_AndPairsSidecars()
    {
        using var t = new TempDir();
        t.Write("in/IMG_1.CR3", "raw");
        t.Write("in/IMG_1.JPG", "jpg");
        t.Write("in/IMG_1.xmp", "xmp");
        t.Write("in/IMG_2.heic", "heic");
        t.Write("in/clip.mp4", "mp4");
        t.Write("in/notes.txt", "txt");
        t.Write("in/out/already.jpg", "x");

        var scanner = new FileScanner();
        var files = await scanner.ScanAsync(t["in"], new ScanOptions(false, [t["in/out"]]));

        var names = files.Select(f => Path.GetFileName(f.FullPath)).ToList();
        Assert.Equal(["IMG_1.CR3", "IMG_1.JPG", "IMG_2.heic"], names);
        Assert.Equal(t["in/IMG_1.xmp"], files.Single(f => f.FullPath.EndsWith(".CR3")).SidecarPath);
        Assert.Null(files.Single(f => f.FullPath.EndsWith(".JPG")).SidecarPath);

        var withVideo = await scanner.ScanAsync(t["in"], new ScanOptions(true, [t["in/out"]]));
        Assert.Contains(withVideo, f => f.FullPath.EndsWith("clip.mp4"));
    }

    [Fact]
    public async Task MissingDirectory_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new FileScanner().ScanAsync("/definitely/not/here", new ScanOptions()));
    }
}
