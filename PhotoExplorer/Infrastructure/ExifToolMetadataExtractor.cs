using System.Diagnostics;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Infrastructure;

/// <summary>exiftoolを外部プロセスとして呼び出してEXIFを抽出する</summary>
public sealed class ExifToolMetadataExtractor : IMetadataExtractor
{
    private readonly string _exifToolPath;

    // exiftool の日時フォーマット
    private static readonly string[] DateFormats =
    [
        "yyyy:MM:dd HH:mm:ss",
        "yyyy:MM:dd HH:mm:sszzz",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:sszzz",
    ];

    public ExifToolMetadataExtractor(string exifToolPath = "exiftool.exe")
    {
        _exifToolPath = exifToolPath;
    }

    public async Task<ExifData?> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var args = $"-json -DateTimeOriginal -CreateDate -ModifyDate -Make -Model -LensModel -FocalLength -Aperture -ShutterSpeed -ISO \"{filePath}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _exifToolPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            }
        };

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(output)) return null;

            var array = JArray.Parse(output);
            if (array.Count == 0) return null;
            var obj = array[0] as JObject;
            if (obj == null) return null;

            DateTime? dateOriginal = ParseDate(obj["DateTimeOriginal"]?.ToString());
            DateTime? createDate = ParseDate(obj["CreateDate"]?.ToString());
            DateTime? modifyDate = ParseDate(obj["ModifyDate"]?.ToString());

            string? usedField = dateOriginal.HasValue ? "DateTimeOriginal"
                : createDate.HasValue ? "CreateDate"
                : modifyDate.HasValue ? "ModifyDate"
                : null;

            return new ExifData
            {
                DateTimeOriginal = dateOriginal,
                CreateDate = createDate,
                ModifyDate = modifyDate,
                Make = obj["Make"]?.ToString(),
                Model = obj["Model"]?.ToString(),
                LensModel = obj["LensModel"]?.ToString(),
                FocalLength = obj["FocalLength"]?.ToString(),
                Aperture = obj["Aperture"]?.ToString(),
                ShutterSpeed = obj["ShutterSpeed"]?.ToString(),
                ISO = obj["ISO"]?.ToString(),
                UsedDateField = usedField,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // exiftool が見つからない場合や解析失敗時は null を返す
            return null;
        }
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value, DateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var dt))
            return dt;
        return null;
    }
}
