using System.Buffers;
using System.Buffers.Binary;

namespace PhotoExplorer.Infrastructure;

public sealed record EmbeddedPreview(byte[] JpegBytes, int Orientation);

/// <summary>
/// RAW ファイル（ARW / NEF / CR2 / CR3 / DNG / RAF / ORF / RW2 / PEF / SRW 等）に埋め込まれた
/// JPEG プレビュー画像を外部コーデックなしで高速に抽出する。
/// </summary>
public static class EmbeddedPreviewExtractor
{
    private const int MinPreviewBytes = 1024;
    private const int MaxPreviewBytes = 25 * 1024 * 1024;
    private const int MaxIfdChain = 16;
    private const int MaxIfdEntries = 512;

    // Canon CR3 のプレビュー格納用 UUID (eaf42b5e-1c98-4b88-b9fb-b7dc406e4d16)
    private static ReadOnlySpan<byte> Cr3PreviewUuid =>
    [
        0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88,
        0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16,
    ];

    public static EmbeddedPreview? TryExtract(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384, FileOptions.RandomAccess);
            return TryExtract(fs);
        }
        catch
        {
            return null;
        }
    }

    public static EmbeddedPreview? TryExtract(Stream stream)
    {
        try
        {
            if (!stream.CanSeek || stream.Length < 16) return null;
            stream.Position = 0;

            Span<byte> sig = stackalloc byte[16];
            if (ReadExact(stream, sig) < 16) return null;

            // すでに JPEG ならそのまま通常デコーダへ任せる
            if (sig[0] == 0xFF && sig[1] == 0xD8)
                return null;

            // Fujifilm RAF: "FUJIFILMCCD-RAW "
            if (sig.SequenceEqual("FUJIFILMCCD-RAW "u8))
                return ExtractFujifilmRaf(stream);

            // Canon CR3: ISOBMFF (bytes 4..7 == "ftyp" && 8..11 == "crx ")
            if (sig[4..8].SequenceEqual("ftyp"u8) && sig[8..12].SequenceEqual("crx "u8))
                return ExtractCanonCr3(stream);

            // TIFF ベース RAW (CR2 / NEF / ARW / DNG / ORF / RW2 / PEF / SRW 等)
            if (IsTiffHeader(sig))
                return ExtractTiffPreview(stream, sig);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static EmbeddedPreview? ExtractFujifilmRaf(Stream stream)
    {
        if (stream.Length < 92) return null;
        stream.Position = 84;
        Span<byte> hdr = stackalloc byte[8];
        if (ReadExact(stream, hdr) < 8) return null;
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..8]);
        var bytes = ReadJpegSlice(stream, offset, length);
        if (bytes == null) return null;
        using var ms = new MemoryStream(bytes, writable: false);
        return new EmbeddedPreview(bytes, FastExifReader.ReadOrientation(ms));
    }

    private static EmbeddedPreview? ExtractCanonCr3(Stream stream)
    {
        long pos = 0;
        long fileLen = stream.Length;
        Span<byte> boxHdr = stackalloc byte[16];
        Span<byte> uuidBuf = stackalloc byte[16];
        int orientation = 1;

        while (pos + 8 <= fileLen)
        {
            stream.Position = pos;
            if (ReadExact(stream, boxHdr[..8]) < 8) break;
            long boxSize = BinaryPrimitives.ReadUInt32BigEndian(boxHdr[..4]);
            int headerSize = 8;
            if (boxSize == 1)
            {
                if (pos + 16 > fileLen || ReadExact(stream, boxHdr[8..16]) < 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(boxHdr[8..16]);
                headerSize = 16;
            }
            else if (boxSize == 0)
            {
                boxSize = fileLen - pos;
            }
            if (boxSize < headerSize || pos + boxSize > fileLen) break;

            if (boxHdr[4..8].SequenceEqual("uuid"u8) && boxSize >= headerSize + 16 + 24)
            {
                if (ReadExact(stream, uuidBuf) == 16 && uuidBuf.SequenceEqual(Cr3PreviewUuid))
                {
                    var preview = FindPrvwInUuid(stream, pos + headerSize + 16, pos + boxSize);
                    if (preview != null) return new EmbeddedPreview(preview, orientation);
                }
            }

            pos += boxSize;
        }
        return null;
    }

    private static byte[]? FindPrvwInUuid(Stream stream, long start, long end)
    {
        // UUID 内の子ボックスを順に走査して PRVW ボックスを探す
        // PRVW 構造: [4:size][4:"PRVW"][6:unknown][2:width][2:height][2:unknown][4:jpegSize][jpegData]
        Span<byte> hdr = stackalloc byte[24];
        long pos = start;
        // 先頭 8 バイトのパディング/ヘッダがある場合も考慮して 0 と 8 の両方を試す
        for (int attempt = 0; attempt < 2; attempt++)
        {
            pos = start + attempt * 8;
            while (pos + 24 <= end)
            {
                stream.Position = pos;
                if (ReadExact(stream, hdr[..8]) < 8) break;
                long size = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
                if (size < 8 || pos + size > end) break;

                if (hdr[4..8].SequenceEqual("PRVW"u8) && size > 24)
                {
                    if (ReadExact(stream, hdr[8..24]) == 16)
                    {
                        uint jpegSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[20..24]);
                        long jpegOffset = pos + 24;
                        var bytes = ReadJpegSlice(stream, jpegOffset, jpegSize);
                        if (bytes != null) return bytes;
                    }
                }
                pos += size;
            }
        }
        return null;
    }

    private static EmbeddedPreview? ExtractTiffPreview(Stream stream, ReadOnlySpan<byte> sig)
    {
        bool le = sig[0] == 'I';
        ushort magic = ReadU16(sig, 2, le);
        uint ifd0 = ReadU32(sig, 4, le);
        if (magic == 0x0055 && ifd0 < 8) ifd0 = 24u;

        var visited = new HashSet<long>();
        var queue = new Queue<long>();
        if (ifd0 > 0 && ifd0 < stream.Length) queue.Enqueue(ifd0);

        long bestOffset = 0;
        long bestLength = 0;
        int orientation = 1;
        bool orientationRead = false;
        int inspected = 0;
        Span<byte> cntBuf = stackalloc byte[2];

        while (queue.Count > 0 && inspected < MaxIfdChain)
        {
            long ifdOffset = queue.Dequeue();
            if (!visited.Add(ifdOffset) || ifdOffset <= 0 || ifdOffset + 2 > stream.Length)
                continue;
            inspected++;

            stream.Position = ifdOffset;
            if (ReadExact(stream, cntBuf) < 2) continue;
            int count = ReadU16(cntBuf, 0, le);
            if (count <= 0 || count > MaxIfdEntries) continue;

            int bytesNeeded = count * 12 + 4;
            var buf = ArrayPool<byte>.Shared.Rent(bytesNeeded);
            try
            {
                int read = ReadExact(stream, buf.AsSpan(0, bytesNeeded));
                if (read < count * 12) continue;

                long jpgOffset = 0, jpgLen = 0;
                long stripOffset = 0, stripLen = 0;
                long rawJpgOffset = 0, rawJpgLen = 0;

                for (int i = 0; i < count; i++)
                {
                    var entry = buf.AsSpan(i * 12, 12);
                    ushort tag = ReadU16(entry, 0, le);
                    ushort type = ReadU16(entry, 2, le);
                    uint valCount = ReadU32(entry, 4, le);

                    switch (tag)
                    {
                        case 0x0112 when !orientationRead: // Orientation
                            int ori = type == 3 ? ReadU16(entry, 8, le) : (int)ReadU32(entry, 8, le);
                            if (ori is >= 1 and <= 8) { orientation = ori; orientationRead = true; }
                            break;
                        case 0x002E: // Panasonic RW2 JpgFromRaw
                            if (valCount >= MinPreviewBytes && valCount <= MaxPreviewBytes)
                            {
                                rawJpgOffset = ReadU32(entry, 8, le);
                                rawJpgLen = valCount;
                            }
                            break;
                        case 0x0201: // JPEGInterchangeFormat
                            jpgOffset = ReadSingleU32(entry, type, le);
                            break;
                        case 0x0202: // JPEGInterchangeFormatLength
                            jpgLen = ReadSingleU32(entry, type, le);
                            break;
                        case 0x0111: // StripOffsets (only single-strip JPEG previews)
                            if (valCount == 1) stripOffset = ReadSingleU32(entry, type, le);
                            break;
                        case 0x0117: // StripByteCounts
                            if (valCount == 1) stripLen = ReadSingleU32(entry, type, le);
                            break;
                        case 0x014A: // SubIFDs
                            EnqueueSubIfds(stream, entry, type, valCount, le, queue);
                            break;
                    }
                }

                ConsiderCandidate(stream, rawJpgOffset, rawJpgLen, ref bestOffset, ref bestLength);
                ConsiderCandidate(stream, jpgOffset, jpgLen, ref bestOffset, ref bestLength);
                ConsiderCandidate(stream, stripOffset, stripLen, ref bestOffset, ref bestLength);

                // Next IFD
                if (read >= count * 12 + 4)
                {
                    uint nextIfd = ReadU32(buf.AsSpan(count * 12, 4), 0, le);
                    if (nextIfd > 0 && nextIfd < stream.Length) queue.Enqueue(nextIfd);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        if (bestOffset > 0 && bestLength >= MinPreviewBytes)
        {
            var bytes = ReadJpegSlice(stream, bestOffset, bestLength);
            if (bytes != null) return new EmbeddedPreview(bytes, orientation);
        }

        return null;
    }

    private static void EnqueueSubIfds(Stream stream, ReadOnlySpan<byte> entry, ushort type, uint count, bool le, Queue<long> queue)
    {
        if (count == 0 || count > 8 || (type != 4 && type != 13)) return;
        if (count == 1)
        {
            uint off = ReadU32(entry, 8, le);
            if (off > 0 && off < stream.Length) queue.Enqueue(off);
            return;
        }
        uint arrOff = ReadU32(entry, 8, le);
        if (arrOff == 0 || arrOff + count * 4 > stream.Length) return;
        Span<byte> buf = stackalloc byte[(int)count * 4];
        stream.Position = arrOff;
        if (ReadExact(stream, buf) < buf.Length) return;
        for (int i = 0; i < count; i++)
        {
            uint off = ReadU32(buf, i * 4, le);
            if (off > 0 && off < stream.Length) queue.Enqueue(off);
        }
    }

    private static void ConsiderCandidate(Stream stream, long offset, long length, ref long bestOffset, ref long bestLength)
    {
        if (offset <= 0 || length < MinPreviewBytes || length > MaxPreviewBytes) return;
        if (length <= bestLength || offset + length > stream.Length) return;
        // 先頭 2 バイトが JPEG マジック (FF D8) か確認する
        Span<byte> head = stackalloc byte[2];
        stream.Position = offset;
        if (ReadExact(stream, head) == 2 && head[0] == 0xFF && head[1] == 0xD8)
        {
            bestOffset = offset;
            bestLength = length;
        }
    }

    private static byte[]? ReadJpegSlice(Stream stream, long offset, long length)
    {
        if (offset <= 0 || length < MinPreviewBytes || length > MaxPreviewBytes || offset + length > stream.Length)
            return null;
        var bytes = new byte[length];
        stream.Position = offset;
        if (ReadExact(stream, bytes) != length) return null;
        return bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
    }

    private static bool IsTiffHeader(ReadOnlySpan<byte> s)
    {
        if (s[0] == 'I' && s[1] == 'I')
            return BinaryPrimitives.ReadUInt16LittleEndian(s[2..4]) is 42 or 0x4F52 or 0x0055;
        if (s[0] == 'M' && s[1] == 'M')
            return BinaryPrimitives.ReadUInt16BigEndian(s[2..4]) == 42;
        return false;
    }

    private static long ReadSingleU32(ReadOnlySpan<byte> entry, ushort type, bool le) =>
        type == 3 ? ReadU16(entry, 8, le) : ReadU32(entry, 8, le);

    private static ushort ReadU16(ReadOnlySpan<byte> s, int offset, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(offset, 2))
           : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> s, int offset, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset, 4))
           : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4));

    private static int ReadExact(Stream s, Span<byte> dest)
    {
        int total = 0;
        while (total < dest.Length)
        {
            int n = s.Read(dest[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
