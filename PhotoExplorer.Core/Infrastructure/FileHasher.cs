using System.Security.Cryptography;

namespace PhotoExplorer.Infrastructure;

public static class FileHasher
{
    public const int PartialHashBytes = 64 * 1024;

    public static async Task<string> ComputePartialAsync(string path, CancellationToken ct = default)
    {
        await using var fs = OpenRead(path);
        var buffer = new byte[PartialHashBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await fs.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, total)));
    }

    public static async Task<string> ComputeFullAsync(string path, CancellationToken ct = default)
    {
        await using var fs = OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
