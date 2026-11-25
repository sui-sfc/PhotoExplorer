using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Infrastructure;

public sealed record FastExifResult(ExifData Data, bool HasMakerNote, int Orientation)
{
    /// <summary>
    /// exiftool による追加読み取りが必要かどうか。
    /// GPS がある場合は地名変換（Geolocation）のため、LensModel が無く MakerNote がある場合は LensID 解析のために exiftool へ委ねる。
    /// </summary>
    public bool NeedsExifTool => Data.HasGps || (HasMakerNote && Data.LensModel == null);
}

/// <summary>
/// 外部プロセス（exiftool）を起動せずに、JPEG・TIFF ベース RAW（ARW/NEF/CR2/DNG/ORF/PEF/RW2/SRW 等）・RAF から
/// EXIF / XMP / 画像サイズを高速に読み取る純 C# リーダー。
/// 破損・未対応形式は null を返し、exiftool にフォールバックする。
/// </summary>
public static partial class FastExifReader
{
    private const int InitialReadBytes = 128 * 1024;
    private const int MaxSegmentReadBytes = 4 * 1024 * 1024;
    private const int MaxIfdEntries = 512;

    [GeneratedRegex(@"xmp:Rating\s*=\s*[""'](\d)[""']|<xmp:Rating[^>]*>\s*(\d)\s*</xmp:Rating>", RegexOptions.IgnoreCase)]
    private static partial Regex XmpRatingRegex();

    /// <summary>ファイルから EXIF / XMP / 画像サイズを読み取る。未対応または読めなければ null</summary>
    public static FastExifResult? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384, FileOptions.SequentialScan);
            return TryRead(fs);
        }
        catch
        {
            return null;
        }
    }

    public static FastExifResult? TryRead(Stream stream)
    {
        try
        {
            if (!stream.CanSeek || stream.Length < 12) return null;
            stream.Position = 0;

            Span<byte> sig = stackalloc byte[16];
            int sigRead = ReadExactAtMost(stream, sig);
            if (sigRead < 12) return null;

            // JPEG: FF D8
            if (sig[0] == 0xFF && sig[1] == 0xD8)
                return ReadJpeg(stream, 0, stream.Length);

            // Fujifilm RAF: "FUJIFILMCCD-RAW "
            if (sigRead >= 16 && sig[..16].SequenceEqual("FUJIFILMCCD-RAW "u8))
                return ReadFujifilmRaf(stream);

            // TIFF / TIFF-based RAW (II 42, MM 42, Olympus II RO, Panasonic II 0x55)
            if (IsTiffHeader(sig))
                return ReadTiff(stream);

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>EXIF Orientation（1〜8）を高速に読み取る。読めなければ 1</summary>
    public static int ReadOrientation(Stream stream)
    {
        try
        {
            var result = TryRead(stream);
            return result is { Orientation: >= 1 and <= 8 } ? result.Orientation : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static FastExifResult? ReadFujifilmRaf(Stream stream)
    {
        if (stream.Length < 92) return null;
        stream.Position = 84;
        Span<byte> hdr = stackalloc byte[8];
        if (ReadExactAtMost(stream, hdr) < 8) return null;
        uint jpegOffset = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
        uint jpegLength = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..8]);
        if (jpegOffset == 0 || jpegLength < 12 || (long)jpegOffset + jpegLength > stream.Length)
            return null;
        return ReadJpeg(stream, jpegOffset, jpegLength);
    }

    private static FastExifResult? ReadJpeg(Stream stream, long startOffset, long maxLength)
    {
        long endOffset = Math.Min(stream.Length, startOffset + maxLength);
        stream.Position = startOffset + 2;

        var builder = new ExifBuilder();
        int sofWidth = 0, sofHeight = 0;
        bool validJpeg = false;

        Span<byte> sof = stackalloc byte[5];
        while (stream.Position + 2 <= endOffset)
        {
            int b = stream.ReadByte();
            if (b < 0) break;
            if (b != 0xFF) continue;

            // パディングの 0xFF をスキップ
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker < 0) break;

            // 独立マーカー（長さなし）
            if (marker == 0xD9 || marker == 0xDA) // EOI or SOS（画像データ開始）
            {
                validJpeg = true;
                break;
            }
            if (marker == 0x00 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
                continue;

            if (stream.Position + 2 > endOffset) break;
            int hi = stream.ReadByte();
            int lo = stream.ReadByte();
            if (hi < 0 || lo < 0) break;
            int segLen = (hi << 8) | lo;
            if (segLen < 2) break;
            int payloadLen = segLen - 2;
            long payloadStart = stream.Position;
            long nextPos = payloadStart + payloadLen;
            if (nextPos > endOffset) break;

            // APP1: EXIF または XMP
            if (marker == 0xE1 && payloadLen >= 6 && payloadLen <= MaxSegmentReadBytes)
            {
                var buf = ArrayPool<byte>.Shared.Rent(payloadLen);
                try
                {
                    if (ReadExactAtMost(stream, buf.AsSpan(0, payloadLen)) == payloadLen)
                    {
                        var span = buf.AsSpan(0, payloadLen);
                        if (payloadLen > 14 && span[..6].SequenceEqual("Exif\0\0"u8))
                        {
                            ParseTiffSpan(span[6..], builder);
                            validJpeg = true;
                        }
                        else if (payloadLen > 29 && span[..29].SequenceEqual("http://ns.adobe.com/xap/1.0/\0"u8))
                        {
                            ParseXmp(span[29..], builder);
                            validJpeg = true;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
            // SOF0 / SOF1 / SOF2 / SOF3: 画像サイズ（幅・高さ）
            else if ((marker == 0xC0 || marker == 0xC1 || marker == 0xC2 || marker == 0xC3) && payloadLen >= 5)
            {
                if (ReadExactAtMost(stream, sof) == 5)
                {
                    sofHeight = BinaryPrimitives.ReadUInt16BigEndian(sof[1..3]);
                    sofWidth = BinaryPrimitives.ReadUInt16BigEndian(sof[3..5]);
                    validJpeg = true;
                }
            }

            stream.Position = nextPos;
        }

        if (sofWidth > 0 && sofHeight > 0)
        {
            // JPEG の SOF は実ピクセル寸法として最も確実（EXIF の値はトリミング後に古いままのことがある）
            builder.ImageWidth = sofWidth;
            builder.ImageHeight = sofHeight;
        }

        if (!validJpeg && builder.IsEmpty) return null;
        return builder.Build();
    }

    private static FastExifResult? ReadTiff(Stream stream)
    {
        // ほとんどの TIFF ベース RAW は先頭 128KB 以内に IFD0・ExifIFD・GPS IFD がある。
        // オフセットがバッファ外を指す場合だけストリームから直接読む。
        int toRead = (int)Math.Min(stream.Length, InitialReadBytes);
        var buf = ArrayPool<byte>.Shared.Rent(toRead);
        try
        {
            stream.Position = 0;
            int read = ReadExactAtMost(stream, buf.AsSpan(0, toRead));
            var builder = new ExifBuilder();
            if (!ParseTiffStreamOrSpan(stream, buf.AsSpan(0, read), builder))
                return null;
            return builder.Build();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static bool IsTiffHeader(ReadOnlySpan<byte> s)
    {
        if (s.Length < 8) return false;
        if (s[0] == 'I' && s[1] == 'I')
        {
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(s[2..4]);
            return magic is 42 or 0x4F52 or 0x0055; // Standard TIFF, Olympus ORF, Panasonic RW2
        }
        if (s[0] == 'M' && s[1] == 'M')
        {
            ushort magic = BinaryPrimitives.ReadUInt16BigEndian(s[2..4]);
            return magic == 42;
        }
        return false;
    }

    private static bool ParseTiffSpan(ReadOnlySpan<byte> data, ExifBuilder builder)
    {
        if (data.Length < 8 || !IsTiffHeader(data)) return false;
        bool le = data[0] == 'I';
        ushort magic = ReadU16(data, 2, le);
        uint ifd0Offset = ReadU32(data, 4, le);
        if (magic == 0x0055 && ifd0Offset < 8) ifd0Offset = 24u;

        var reader = new TiffReader(null, data, le);
        return ParseTiffCore(in reader, ifd0Offset, builder);
    }

    private static bool ParseTiffStreamOrSpan(Stream stream, ReadOnlySpan<byte> head, ExifBuilder builder)
    {
        if (head.Length < 8 || !IsTiffHeader(head)) return false;
        bool le = head[0] == 'I';
        ushort magic = ReadU16(head, 2, le);
        uint ifd0Offset = ReadU32(head, 4, le);
        if (magic == 0x0055 && ifd0Offset < 8) ifd0Offset = 24u;

        var reader = new TiffReader(stream, head, le);
        return ParseTiffCore(in reader, ifd0Offset, builder);
    }

    private readonly ref struct TiffReader(Stream? stream, ReadOnlySpan<byte> head, bool littleEndian)
    {
        private readonly Stream? _stream = stream;
        private readonly ReadOnlySpan<byte> _head = head;
        public bool LittleEndian => littleEndian;
        public long Length => _stream?.Length ?? _head.Length;

        public bool TryReadBytes(long offset, Span<byte> dest)
        {
            if (offset < 0 || offset + dest.Length > Length) return false;
            if (offset + dest.Length <= _head.Length)
            {
                _head.Slice((int)offset, dest.Length).CopyTo(dest);
                return true;
            }
            if (_stream == null) return false;
            _stream.Position = offset;
            return ReadExactAtMost(_stream, dest) == dest.Length;
        }
    }

    private static bool ParseTiffCore(in TiffReader reader, uint ifd0Offset, ExifBuilder builder)
    {
        if (ifd0Offset == 0 || ifd0Offset >= reader.Length) return false;

        uint exifIfdOffset = 0;
        uint gpsIfdOffset = 0;
        ParseIfd(in reader, ifd0Offset, IfdKind.Ifd0, builder, ref exifIfdOffset, ref gpsIfdOffset);

        if (exifIfdOffset > 0 && exifIfdOffset < reader.Length)
        {
            uint dummy1 = 0, dummy2 = 0;
            ParseIfd(in reader, exifIfdOffset, IfdKind.Exif, builder, ref dummy1, ref dummy2);
        }

        if (gpsIfdOffset > 0 && gpsIfdOffset < reader.Length)
        {
            uint dummy1 = 0, dummy2 = 0;
            ParseIfd(in reader, gpsIfdOffset, IfdKind.Gps, builder, ref dummy1, ref dummy2);
        }

        return true;
    }

    private enum IfdKind { Ifd0, Exif, Gps }

    private static void ParseIfd(
        in TiffReader reader,
        uint ifdOffset,
        IfdKind kind,
        ExifBuilder builder,
        ref uint exifIfdOffset,
        ref uint gpsIfdOffset)
    {
        Span<byte> countBuf = stackalloc byte[2];
        if (!reader.TryReadBytes(ifdOffset, countBuf)) return;
        int count = ReadU16(countBuf, 0, reader.LittleEndian);
        if (count <= 0 || count > MaxIfdEntries) return;

        int bytesNeeded = count * 12;
        byte[]? rented = bytesNeeded > 2048 ? ArrayPool<byte>.Shared.Rent(bytesNeeded) : null;
        Span<byte> entries = rented != null ? rented.AsSpan(0, bytesNeeded) : stackalloc byte[bytesNeeded];
        try
        {
            if (!reader.TryReadBytes(ifdOffset + 2, entries)) return;
            bool le = reader.LittleEndian;

            for (int i = 0; i < count; i++)
            {
                var entry = entries.Slice(i * 12, 12);
                ushort tag = ReadU16(entry, 0, le);
                ushort type = ReadU16(entry, 2, le);
                uint valCount = ReadU32(entry, 4, le);

                switch (kind)
                {
                    case IfdKind.Ifd0:
                        switch (tag)
                        {
                            case 0x0100: // ImageWidth
                                if (TryReadInt(in reader, entry, type, valCount, out int w) && w > 0)
                                    builder.TiffWidth ??= w;
                                break;
                            case 0x0101: // ImageHeight
                                if (TryReadInt(in reader, entry, type, valCount, out int h) && h > 0)
                                    builder.TiffHeight ??= h;
                                break;
                            case 0x010F: // Make
                                builder.Make ??= ReadAscii(in reader, entry, type, valCount);
                                break;
                            case 0x0110: // Model
                                builder.Model ??= ReadAscii(in reader, entry, type, valCount);
                                break;
                            case 0x0112: // Orientation
                                if (TryReadInt(in reader, entry, type, valCount, out int ori) && ori is >= 1 and <= 8)
                                    builder.Orientation = ori;
                                break;
                            case 0x0132: // ModifyDate
                                builder.ModifyDate ??= ExifToolMetadataExtractor.ParseDate(ReadAscii(in reader, entry, type, valCount));
                                break;
                            case 0x02BC: // XMP packet in TIFF
                                if (valCount is > 10 and <= 512 * 1024)
                                {
                                    uint xmpOff = ReadU32(entry, 8, le);
                                    var xmpBuf = ArrayPool<byte>.Shared.Rent((int)valCount);
                                    try
                                    {
                                        if (reader.TryReadBytes(xmpOff, xmpBuf.AsSpan(0, (int)valCount)))
                                            ParseXmp(xmpBuf.AsSpan(0, (int)valCount), builder);
                                    }
                                    finally { ArrayPool<byte>.Shared.Return(xmpBuf); }
                                }
                                break;
                            case 0x4746: // Rating (TIFF/Windows)
                                if (TryReadInt(in reader, entry, type, valCount, out int rat) && rat is >= 0 and <= 5)
                                    builder.Rating ??= rat;
                                break;
                            case 0x8769: // ExifIFD pointer
                                if (TryReadUInt(in reader, entry, type, valCount, out uint eOff))
                                    exifIfdOffset = eOff;
                                break;
                            case 0x8825: // GPS IFD pointer
                                if (TryReadUInt(in reader, entry, type, valCount, out uint gOff))
                                    gpsIfdOffset = gOff;
                                break;
                        }
                        break;

                    case IfdKind.Exif:
                        switch (tag)
                        {
                            case 0x829A: // ExposureTime
                                if (TryReadRational(in reader, entry, type, valCount, out double exp) && exp > 0)
                                    builder.ShutterSpeed ??= exp.ToString("G10", CultureInfo.InvariantCulture);
                                break;
                            case 0x829D: // FNumber
                                if (TryReadRational(in reader, entry, type, valCount, out double fn) && fn > 0)
                                    builder.Aperture ??= Math.Round(fn, 2).ToString("G", CultureInfo.InvariantCulture);
                                break;
                            case 0x8827: // ISOSpeedRatings
                            case 0x8833: // ISOSpeed
                                if (TryReadInt(in reader, entry, type, valCount, out int iso) && iso > 0)
                                    builder.ISO ??= iso.ToString(CultureInfo.InvariantCulture);
                                break;
                            case 0x9003: // DateTimeOriginal
                                builder.DateTimeOriginal ??= ExifToolMetadataExtractor.ParseDate(ReadAscii(in reader, entry, type, valCount));
                                break;
                            case 0x9004: // CreateDate
                                builder.CreateDate ??= ExifToolMetadataExtractor.ParseDate(ReadAscii(in reader, entry, type, valCount));
                                break;
                            case 0x920A: // FocalLength
                                if (TryReadRational(in reader, entry, type, valCount, out double fl) && fl > 0)
                                    builder.FocalLength ??= Math.Round(fl, 1).ToString("G", CultureInfo.InvariantCulture);
                                break;
                            case 0x927C: // MakerNote
                                if (valCount > 0) builder.HasMakerNote = true;
                                break;
                            case 0xA002: // ExifImageWidth
                                if (TryReadInt(in reader, entry, type, valCount, out int ew) && ew > 0)
                                    builder.ImageWidth ??= ew;
                                break;
                            case 0xA003: // ExifImageHeight
                                if (TryReadInt(in reader, entry, type, valCount, out int eh) && eh > 0)
                                    builder.ImageHeight ??= eh;
                                break;
                            case 0xA434: // LensModel
                                builder.LensModel ??= ReadAscii(in reader, entry, type, valCount);
                                break;
                        }
                        break;

                    case IfdKind.Gps:
                        switch (tag)
                        {
                            case 0x0001: // GPSLatitudeRef
                                builder.GpsLatRef = ReadAscii(in reader, entry, type, valCount);
                                break;
                            case 0x0002: // GPSLatitude (3 rationals)
                                if (TryReadGpsCoord(in reader, entry, type, valCount, out double lat))
                                    builder.GpsLatAbs = lat;
                                break;
                            case 0x0003: // GPSLongitudeRef
                                builder.GpsLonRef = ReadAscii(in reader, entry, type, valCount);
                                break;
                            case 0x0004: // GPSLongitude (3 rationals)
                                if (TryReadGpsCoord(in reader, entry, type, valCount, out double lon))
                                    builder.GpsLonAbs = lon;
                                break;
                        }
                        break;
                }
            }
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ParseXmp(ReadOnlySpan<byte> xmpBytes, ExifBuilder builder)
    {
        // "Rating" が含まれていなければ文字列化すらしない
        if (xmpBytes.IndexOf("Rating"u8) < 0) return;
        var text = Encoding.UTF8.GetString(xmpBytes);
        var m = XmpRatingRegex().Match(text);
        if (m.Success)
        {
            var g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (int.TryParse(g, CultureInfo.InvariantCulture, out int r) && r is >= 0 and <= 5)
                builder.Rating = r;
        }
    }

    private static string? ReadAscii(in TiffReader reader, scoped ReadOnlySpan<byte> entry, ushort type, uint count)
    {
        // 2 = ASCII, 7 = UNDEFINED, 1 = BYTE
        if (type is not (2 or 7 or 1) || count == 0 || count > 256) return null;
        Span<byte> buf = stackalloc byte[(int)count];
        if (count <= 4)
        {
            entry.Slice(8, (int)count).CopyTo(buf);
        }
        else
        {
            uint offset = ReadU32(entry, 8, reader.LittleEndian);
            if (!reader.TryReadBytes(offset, buf)) return null;
        }

        int nullIdx = buf.IndexOf((byte)0);
        if (nullIdx >= 0) buf = buf[..nullIdx];
        if (buf.IsEmpty) return null;

        var str = Encoding.UTF8.GetString(buf).Trim();
        return string.IsNullOrWhiteSpace(str) ? null : str;
    }

    private static bool TryReadInt(in TiffReader reader, scoped ReadOnlySpan<byte> entry, ushort type, uint count, out int value)
    {
        value = 0;
        if (count == 0) return false;
        bool le = reader.LittleEndian;
        switch (type)
        {
            case 3: // SHORT (uint16)
                value = ReadU16(entry, 8, le);
                return true;
            case 4: // LONG (uint32)
                value = (int)ReadU32(entry, 8, le);
                return true;
            case 8: // SSHORT (int16)
                value = (short)ReadU16(entry, 8, le);
                return true;
            case 9: // SLONG (int32)
                value = (int)ReadU32(entry, 8, le);
                return true;
            case 1: // BYTE
                value = entry[8];
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadUInt(in TiffReader reader, scoped ReadOnlySpan<byte> entry, ushort type, uint count, out uint value)
    {
        value = 0;
        if (count == 0) return false;
        bool le = reader.LittleEndian;
        if (type is 4 or 13) // LONG or IFD
        {
            value = ReadU32(entry, 8, le);
            return true;
        }
        if (type == 3)
        {
            value = ReadU16(entry, 8, le);
            return true;
        }
        return false;
    }

    private static bool TryReadRational(in TiffReader reader, scoped ReadOnlySpan<byte> entry, ushort type, uint count, out double value)
    {
        value = 0;
        if (count == 0 || (type != 5 && type != 10)) return false;
        bool le = reader.LittleEndian;
        uint offset = ReadU32(entry, 8, le);
        Span<byte> buf = stackalloc byte[8];
        if (!reader.TryReadBytes(offset, buf)) return false;

        if (type == 5) // RATIONAL (unsigned)
        {
            uint num = ReadU32(buf, 0, le);
            uint den = ReadU32(buf, 4, le);
            if (den == 0) return false;
            value = (double)num / den;
            return true;
        }
        else // SRATIONAL (signed)
        {
            int num = (int)ReadU32(buf, 0, le);
            int den = (int)ReadU32(buf, 4, le);
            if (den == 0) return false;
            value = (double)num / den;
            return true;
        }
    }

    private static bool TryReadGpsCoord(in TiffReader reader, scoped ReadOnlySpan<byte> entry, ushort type, uint count, out double value)
    {
        value = 0;
        if (type != 5 || count < 3) return false;
        bool le = reader.LittleEndian;
        uint offset = ReadU32(entry, 8, le);
        Span<byte> buf = stackalloc byte[24];
        if (!reader.TryReadBytes(offset, buf)) return false;

        uint dNum = ReadU32(buf, 0, le), dDen = ReadU32(buf, 4, le);
        uint mNum = ReadU32(buf, 8, le), mDen = ReadU32(buf, 12, le);
        uint sNum = ReadU32(buf, 16, le), sDen = ReadU32(buf, 20, le);
        if (dDen == 0 || mDen == 0 || sDen == 0) return false;

        double deg = (double)dNum / dDen;
        double min = (double)mNum / mDen;
        double sec = (double)sNum / sDen;
        double coord = deg + min / 60.0 + sec / 3600.0;
        if (double.IsNaN(coord) || double.IsInfinity(coord) || coord > 180.0) return false;
        value = coord;
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> s, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4));

    private static int ReadExactAtMost(Stream s, Span<byte> dest)
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

    private sealed class ExifBuilder
    {
        public DateTime? DateTimeOriginal;
        public DateTime? CreateDate;
        public DateTime? ModifyDate;
        public string? Make;
        public string? Model;
        public string? LensModel;
        public string? FocalLength;
        public string? Aperture;
        public string? ShutterSpeed;
        public string? ISO;
        public int? ImageWidth;
        public int? ImageHeight;
        public int? TiffWidth;
        public int? TiffHeight;
        public int? Rating;
        public int Orientation = 1;
        public bool HasMakerNote;

        public string? GpsLatRef;
        public double? GpsLatAbs;
        public string? GpsLonRef;
        public double? GpsLonAbs;

        public bool IsEmpty =>
            DateTimeOriginal == null && CreateDate == null && ModifyDate == null
            && Make == null && Model == null && LensModel == null
            && ImageWidth == null && TiffWidth == null && GpsLatAbs == null;

        public FastExifResult Build()
        {
            double? lat = GpsLatAbs.HasValue
                ? (string.Equals(GpsLatRef, "S", StringComparison.OrdinalIgnoreCase) ? -GpsLatAbs.Value : GpsLatAbs.Value)
                : null;
            double? lon = GpsLonAbs.HasValue
                ? (string.Equals(GpsLonRef, "W", StringComparison.OrdinalIgnoreCase) ? -GpsLonAbs.Value : GpsLonAbs.Value)
                : null;

            string? usedField = DateTimeOriginal.HasValue ? "DateTimeOriginal"
                : CreateDate.HasValue ? "CreateDate"
                : ModifyDate.HasValue ? "ModifyDate"
                : null;

            var data = new ExifData
            {
                DateTimeOriginal = DateTimeOriginal,
                CreateDate = CreateDate,
                ModifyDate = ModifyDate,
                Make = Make,
                Model = Model,
                LensModel = LensModel,
                FocalLength = FocalLength,
                Aperture = Aperture,
                ShutterSpeed = ShutterSpeed,
                ISO = ISO,
                // TIFF 系 RAW の IFD0 の寸法はサムネイルのことが多いので使わない（ExifImageWidth/Height のみ）
                ImageWidth = ImageWidth,
                ImageHeight = ImageHeight,
                Rating = Rating,
                Orientation = Orientation is >= 1 and <= 8 ? Orientation : null,
                GPSLatitude = lat,
                GPSLongitude = lon,
                UsedDateField = usedField,
            };
            return new FastExifResult(data, HasMakerNote, Orientation);
        }
    }
}
