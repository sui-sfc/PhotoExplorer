using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PhotoExplorer.Infrastructure;

public enum FileOpOutcome
{
    /// <summary>コピー・移動した</summary>
    Done,
    /// <summary>出力先に同一内容のファイルがあったので何もしなかった</summary>
    AlreadyExists,
}

public sealed record FileOpResult(string Path, FileOpOutcome Outcome, string? Hash);

public interface IFileOperator
{
    /// <summary>コピーする。同名ファイルがあれば内容を比べ、同じならスキップ、違えば連番を付ける。</summary>
    Task<FileOpResult> CopyAsync(string sourcePath, string destPath, bool verify,
        string? knownSourceHash = null, CancellationToken cancellationToken = default);

    /// <summary>移動する。同一内容のファイルが出力先にある場合は元ファイルを残して AlreadyExists を返す。</summary>
    Task<FileOpResult> MoveAsync(string sourcePath, string destPath, bool verify,
        string? knownSourceHash = null, CancellationToken cancellationToken = default);

    /// <summary>退避フォルダへ移動する（論理削除）</summary>
    Task<FileOpResult> MoveToTrashAsync(string sourcePath, string trashDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>上書きしないファイル操作の実装</summary>
public sealed class FileOperator : IFileOperator
{
    private const int BufferSize = 1 << 20; // 1 MB
    private readonly ConcurrentDictionary<string, byte> _createdDirs = new(PathUtil.Comparer);

    public Task<FileOpResult> CopyAsync(string sourcePath, string destPath, bool verify,
        string? knownSourceHash = null, CancellationToken cancellationToken = default) =>
        CopyCoreAsync(sourcePath, destPath, verify, knownSourceHash, skipIdentical: true, flushToDisk: false, cancellationToken);

    public async Task<FileOpResult> MoveAsync(string sourcePath, string destPath, bool verify,
        string? knownSourceHash = null, CancellationToken cancellationToken = default) =>
        await MoveCoreAsync(sourcePath, destPath, verify, knownSourceHash, skipIdentical: true, cancellationToken)
            .ConfigureAwait(false);

    public Task<FileOpResult> MoveToTrashAsync(string sourcePath, string trashDirectory,
        CancellationToken cancellationToken = default) =>
        MoveCoreAsync(sourcePath, Path.Combine(trashDirectory, Path.GetFileName(sourcePath)),
            verify: true, knownSourceHash: null, skipIdentical: false, cancellationToken);

    private void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return;
        if (_createdDirs.ContainsKey(dir) && Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        _createdDirs[dir] = 0;
    }

    private async Task<FileOpResult> MoveCoreAsync(string src, string dest, bool verify,
        string? knownSourceHash, bool skipIdentical, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureDirectory(dest);

        if (!PathUtil.SameVolume(src, dest))
        {
            // ドライブをまたぐ移動は元ファイルを消すため「コピー → ディスク同期 → 検証 → 元を削除」で行う
            var copied = await CopyCoreAsync(src, dest, verify: true, knownSourceHash, skipIdentical, flushToDisk: true, ct)
                .ConfigureAwait(false);
            if (copied.Outcome == FileOpOutcome.Done)
                File.Delete(src);
            return copied;
        }

        string? srcHash = knownSourceHash;
        long? srcLength = null;
        for (int n = 0; ; n++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = n == 0 ? dest : Numbered(dest, n);

            if (File.Exists(candidate))
            {
                if (PathUtil.PathEquals(candidate, src))
                    return new FileOpResult(candidate, FileOpOutcome.AlreadyExists, srcHash);
                if (skipIdentical)
                {
                    srcLength ??= new FileInfo(src).Length;
                    if (new FileInfo(candidate).Length == srcLength.Value)
                    {
                        srcHash ??= await FileHasher.ComputeFullAsync(src, ct).ConfigureAwait(false);
                        if (await HasContentAsync(candidate, srcLength.Value, srcHash, ct).ConfigureAwait(false))
                            return new FileOpResult(candidate, FileOpOutcome.AlreadyExists, srcHash);
                    }
                }
                continue;
            }

            try
            {
                File.Move(src, candidate, overwrite: false);
                return new FileOpResult(candidate, FileOpOutcome.Done, srcHash);
            }
            catch (IOException) when (File.Exists(candidate) && File.Exists(src))
            {
                // 確認後に別プロセスが同名ファイルを作った → 次の連番で再試行
            }
        }
    }

    private async Task<FileOpResult> CopyCoreAsync(string src, string dest, bool verify,
        string? knownSourceHash, bool skipIdentical, bool flushToDisk, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureDirectory(dest);
        var srcInfo = new FileInfo(src);
        long srcLength = srcInfo.Length;
        string? srcHash = knownSourceHash;

        for (int n = 0; ; n++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = n == 0 ? dest : Numbered(dest, n);

            if (File.Exists(candidate))
            {
                if (skipIdentical)
                {
                    // サイズが違うならハッシュ計算せずに即座に次の連番へ
                    if (TryGetLength(candidate) == srcLength)
                    {
                        srcHash ??= await FileHasher.ComputeFullAsync(src, ct).ConfigureAwait(false);
                        if (await HasContentAsync(candidate, srcLength, srcHash, ct).ConfigureAwait(false))
                            return new FileOpResult(candidate, FileOpOutcome.AlreadyExists, srcHash);
                    }
                }
                continue;
            }

            FileStream output;
            try
            {
                // CreateNew + PreallocationSize: 上書きを防ぎつつ NTFS の断片化を抑えて高速書き込み
                output = new FileStream(candidate, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous,
                    PreallocationSize = srcLength,
                });
            }
            catch (IOException) when (File.Exists(candidate))
            {
                continue;
            }

            try
            {
                string copiedHash;
                await using (output)
                {
                    copiedHash = await CopyWithHashAsync(src, output, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    if (flushToDisk)
                        output.Flush(flushToDisk: true);
                }

                CopyTimestamps(srcInfo, candidate);

                if (verify)
                {
                    var written = await FileHasher.ComputeFullAsync(candidate, ct).ConfigureAwait(false);
                    if (!string.Equals(written, copiedHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"コピー後の検証に失敗しました（内容が一致しません）: {candidate}");
                    if (knownSourceHash != null && !string.Equals(knownSourceHash, copiedHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"元ファイルが読み取り中に変化しました: {src}");
                }

                return new FileOpResult(candidate, FileOpOutcome.Done, copiedHash);
            }
            catch
            {
                TryDelete(candidate);
                throw;
            }
        }
    }

    private static async Task<string> CopyWithHashAsync(string src, Stream output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = FileHasher.OpenRead(src, BufferSize);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                // 書き込み I/O と SHA-256 計算をオーバーラップさせて CPU 待ち時間を隠蔽する
                var writeTask = output.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                await writeTask.ConfigureAwait(false);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long TryGetLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    private static async Task<bool> HasContentAsync(string path, long size, string hash, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length != size) return false;
            var other = await FileHasher.ComputeFullAsync(path, ct).ConfigureAwait(false);
            return string.Equals(other, hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void CopyTimestamps(FileInfo source, string dest)
    {
        try { File.SetLastWriteTimeUtc(dest, source.LastWriteTimeUtc); } catch { }
        try { File.SetCreationTimeUtc(dest, source.CreationTimeUtc); } catch { }
    }

    internal static string Numbered(string path, int n)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        // "IMG_1.CR3.xmp" のような二重拡張子は本体側の名前の後ろに連番を付ける
        var inner = Path.GetExtension(name);
        if (ext.Equals(".xmp", StringComparison.OrdinalIgnoreCase) && inner.Length is > 1 and <= 5)
            return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_{n:D3}{inner}{ext}");
        return Path.Combine(dir, $"{name}_{n:D3}{ext}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
