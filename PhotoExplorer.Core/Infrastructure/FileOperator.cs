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
    private const int BufferSize = 1 << 20;

    public Task<FileOpResult> CopyAsync(string sourcePath, string destPath, bool verify,
        string? knownSourceHash = null, CancellationToken cancellationToken = default) =>
        CopyCoreAsync(sourcePath, destPath, verify, knownSourceHash, skipIdentical: true, cancellationToken);

    public async Task<FileOpResult> MoveAsync(string sourcePath, string destPath, bool verify,
        string? knownSourceHash = null, CancellationToken cancellationToken = default) =>
        await MoveCoreAsync(sourcePath, destPath, verify, knownSourceHash, skipIdentical: true, cancellationToken)
            .ConfigureAwait(false);

    public Task<FileOpResult> MoveToTrashAsync(string sourcePath, string trashDirectory,
        CancellationToken cancellationToken = default) =>
        MoveCoreAsync(sourcePath, Path.Combine(trashDirectory, Path.GetFileName(sourcePath)),
            verify: true, knownSourceHash: null, skipIdentical: false, cancellationToken);

    private async Task<FileOpResult> MoveCoreAsync(string src, string dest, bool verify,
        string? knownSourceHash, bool skipIdentical, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        if (!PathUtil.SameVolume(src, dest))
        {
            // ドライブをまたぐ移動は「コピー → 検証 → 元を削除」で行う
            var copied = await CopyCoreAsync(src, dest, verify: true, knownSourceHash, skipIdentical, ct)
                .ConfigureAwait(false);
            if (copied.Outcome == FileOpOutcome.Done)
                File.Delete(src);
            return copied;
        }

        string? srcHash = knownSourceHash;
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
                    srcHash ??= await FileHasher.ComputeFullAsync(src, ct).ConfigureAwait(false);
                    if (await HasContentAsync(candidate, new FileInfo(src).Length, srcHash, ct).ConfigureAwait(false))
                        return new FileOpResult(candidate, FileOpOutcome.AlreadyExists, srcHash);
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
        string? knownSourceHash, bool skipIdentical, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var srcInfo = new FileInfo(src);
        string? srcHash = knownSourceHash;

        for (int n = 0; ; n++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = n == 0 ? dest : Numbered(dest, n);

            if (File.Exists(candidate))
            {
                if (skipIdentical)
                {
                    srcHash ??= await FileHasher.ComputeFullAsync(src, ct).ConfigureAwait(false);
                    if (await HasContentAsync(candidate, srcInfo.Length, srcHash, ct).ConfigureAwait(false))
                        return new FileOpResult(candidate, FileOpOutcome.AlreadyExists, srcHash);
                }
                continue;
            }

            FileStream output;
            try
            {
                // CreateNew: 存在確認から作成までの間に他者が作っても上書きしない
                output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous);
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
        await using var input = FileHasher.OpenRead(src);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
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
