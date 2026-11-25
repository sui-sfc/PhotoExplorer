using System.Buffers;
using System.Security.Cryptography;

namespace PhotoExplorer.Infrastructure;

public static class FileHasher
{
    public const int PartialHashBytes = 64 * 1024;
    private const int FullHashBufferBytes = 256 * 1024;

    public static async Task<string> ComputePartialAsync(string path, CancellationToken ct = default)
    {
        await using var fs = OpenRead(path, PartialHashBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(PartialHashBytes);
        try
        {
            int total = 0;
            while (total < PartialHashBytes)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(total, PartialHashBytes - total), ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, total)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<string> ComputeFullAsync(string path, CancellationToken ct = default)
    {
        await using var fs = OpenRead(path, FullHashBufferBytes);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(FullHashBufferBytes);
        try
        {
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, FullHashBufferBytes), ct).ConfigureAwait(false)) > 0)
                hasher.AppendData(buffer, 0, read);
            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static FileStream OpenRead(string path, int bufferSize = 1 << 18) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
