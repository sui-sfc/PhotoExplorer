using System.Diagnostics;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Infrastructure;

/// <summary>exiftoolを外部プロセスとして呼び出してEXIFを抽出する</summary>
public sealed class ExifToolMetadataExtractor : IMetadataExtractor
{
    private readonly string _exifToolPath;
    private bool? _isAvailable;

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

    public bool IsAvailable()
    {
        if (_isAvailable.HasValue) return _isAvailable.Value;
        _isAvailable = GetVersion() != null;
        return _isAvailable.Value;
    }

    public string? GetVersion()
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exifToolPath,
                    Arguments = "-ver",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                }
            };
            p.Start();
            var ver = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return string.IsNullOrWhiteSpace(ver) ? null : ver;
        }
        catch { return null; }
    }

    public async Task<ExifData?> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        // -n: 数値出力  -charset UTF8: 文字化け防止
        // Geolocation* タグは exiftool 13+ で利用可能
        var args = "-json -n -charset UTF8 " +
                   "-DateTimeOriginal -CreateDate -ModifyDate " +
                   "-Make -Model -LensModel -LensID -Lens " +
                   "-FocalLength -Aperture -ShutterSpeed -ISO " +
                   "-GPSLatitude -GPSLongitude " +
                   "-GeolocationCity -GeolocationRegion " +
                   "-GeolocationCountry -GeolocationCountryCode " +
                   $"\"{filePath}\"";

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
            DateTime? createDate   = ParseDate(obj["CreateDate"]?.ToString());
            DateTime? modifyDate   = ParseDate(obj["ModifyDate"]?.ToString());

            string? usedField = dateOriginal.HasValue ? "DateTimeOriginal"
                : createDate.HasValue ? "CreateDate"
                : modifyDate.HasValue ? "ModifyDate"
                : null;

            var lensModel = obj["LensModel"]?.ToString()
                         ?? obj["LensID"]?.ToString()
                         ?? obj["Lens"]?.ToString();

            double? lat = ParseDouble(obj["GPSLatitude"]?.ToString());
            double? lon = ParseDouble(obj["GPSLongitude"]?.ToString());

            return new ExifData
            {
                DateTimeOriginal    = dateOriginal,
                CreateDate          = createDate,
                ModifyDate          = modifyDate,
                Make                = obj["Make"]?.ToString(),
                Model               = obj["Model"]?.ToString(),
                LensModel           = lensModel,
                FocalLength         = obj["FocalLength"]?.ToString(),
                Aperture            = obj["Aperture"]?.ToString(),
                ShutterSpeed        = obj["ShutterSpeed"]?.ToString(),
                ISO                 = obj["ISO"]?.ToString(),
                GPSLatitude         = lat,
                GPSLongitude        = lon,
                GeolocationCity     = NullIfEmpty(obj["GeolocationCity"]?.ToString()),
                GeolocationRegion   = NullIfEmpty(obj["GeolocationRegion"]?.ToString()),
                GeolocationCountry  = NullIfEmpty(obj["GeolocationCountry"]?.ToString()),
                GeolocationCountryCode = NullIfEmpty(obj["GeolocationCountryCode"]?.ToString()),
                UsedDateField       = usedField,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
