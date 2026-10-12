using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Infrastructure;

public sealed record MetadataResult(ExifData? Data, string? Error);

public interface IMetadataExtractor
{
    /// <summary>使用する exiftool のパス</summary>
    string ToolPath { get; set; }

    /// <summary>exiftool のバージョン。起動できなければ null</summary>
    string? GetVersion();

    /// <summary>複数ファイルのメタデータをまとめて読み取る（キーはフルパス）</summary>
    Task<IReadOnlyDictionary<string, MetadataResult>> ExtractAsync(
        IReadOnlyList<string> filePaths,
        int maxDegreeOfParallelism = 4,
        IProgress<int>? completedCount = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// exiftool を外部プロセスとして呼び出してメタデータを抽出する。
/// 1 プロセスで複数ファイルを処理することで起動コストを抑える。
/// </summary>
public sealed class ExifToolMetadataExtractor : IMetadataExtractor
{
    private const int BatchSize = 40;
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] DateFormats =
    [
        "yyyy:MM:dd HH:mm:ss",
        "yyyy:MM:dd HH:mm:ss.FFFFFFF",
        "yyyy:MM:dd HH:mm:sszzz",
        "yyyy:MM:dd HH:mm:ss.FFFFFFFzzz",
        "yyyy:MM:dd HH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:sszzz",
    ];

    private static readonly string[] Tags =
    [
        "-DateTimeOriginal", "-CreateDate", "-ModifyDate",
        "-Make", "-Model", "-LensModel", "-LensID", "-Lens",
        "-FocalLength", "-Aperture", "-ShutterSpeed", "-ISO",
        "-ImageWidth", "-ImageHeight", "-Rating",
        "-GPSLatitude", "-GPSLongitude",
        "-GeolocationCity", "-GeolocationRegion",
        "-GeolocationCountry", "-GeolocationCountryCode",
    ];

    public ExifToolMetadataExtractor(string toolPath = "exiftool") => ToolPath = toolPath;

    public string ToolPath { get; set; }

    public string? GetVersion()
    {
        try
        {
            using var p = StartProcess(["-ver"]);
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(VersionTimeout))
            {
                KillQuietly(p);
                return null;
            }
            var ver = outTask.GetAwaiter().GetResult().Trim();
            _ = errTask.GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(ver) ? null : ver;
        }
        catch { return null; }
    }

    public async Task<IReadOnlyDictionary<string, MetadataResult>> ExtractAsync(
        IReadOnlyList<string> filePaths,
        int maxDegreeOfParallelism = 4,
        IProgress<int>? completedCount = null,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<string, MetadataResult>(PathUtil.Comparer);
        if (filePaths.Count == 0) return results;

        var batches = filePaths.Chunk(BatchSize).ToList();
        int done = 0;

        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            },
            async (batch, ct) =>
            {
                var batchResult = await ExtractBatchAsync(batch, ct).ConfigureAwait(false);
                foreach (var (path, r) in batchResult) results[path] = r;
                completedCount?.Report(Interlocked.Add(ref done, batch.Length));
            }).ConfigureAwait(false);

        return results;
    }

    private async Task<Dictionary<string, MetadataResult>> ExtractBatchAsync(
        IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, MetadataResult>(PathUtil.Comparer);

        try
        {
            // 引数（ファイル名を含む）は標準入力から UTF-8 で渡す（-@ -）。
            // コマンドラインや一時ファイルのパスに日本語が含まれると Windows 版 exiftool が扱えないため。
            var lines = new List<string> { "-json", "-n", "-fast", "-q", "-api", "QuickTimeUTC=1", "-api", "Geolocation=1" };
            lines.AddRange(Tags);
            lines.AddRange(paths);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60 + paths.Count * 3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            using var process = StartProcess(["-charset", "filename=utf8", "-@", "-"]);

            // stdout と stderr を同時に読まないと、片方のバッファが詰まってデッドロックする
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

            string stdout, stderr;
            try
            {
                foreach (var line in lines)
                    await process.StandardInput.WriteLineAsync(line.AsMemory(), linked.Token).ConfigureAwait(false);
                process.StandardInput.Close(); // exiftool(-k).exe の「キー入力待ち」もここで抜ける

                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                stdout = await stdoutTask.ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                if (cancellationToken.IsCancellationRequested) throw;
                foreach (var p in paths)
                    results[p] = new MetadataResult(null, "exiftool がタイムアウトしました");
                return results;
            }

            ParseOutput(stdout, results);

            var firstError = FirstErrorLine(stderr);
            foreach (var p in paths)
            {
                if (!results.ContainsKey(p))
                    results[p] = new MetadataResult(null, firstError ?? "exiftool から結果が返りませんでした");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            foreach (var p in paths)
                results.TryAdd(p, new MetadataResult(null, $"exiftool を実行できませんでした: {ex.Message}"));
        }

        return results;
    }

    private Process StartProcess(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("exiftool を起動できませんでした");
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static string? FirstErrorLine(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .FirstOrDefault(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase));

    internal static void ParseOutput(string stdout, Dictionary<string, MetadataResult> results)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return;
        JArray array;
        try { array = JArray.Parse(stdout); }
        catch { return; }

        foreach (var token in array)
        {
            if (token is not JObject obj) continue;
            var source = obj["SourceFile"]?.ToString();
            if (string.IsNullOrEmpty(source)) continue;

            string key;
            try { key = Path.GetFullPath(source); }
            catch { key = source; }

            var error = obj["Error"]?.ToString();
            var data = ParseObject(obj);
            results[key] = data.IsEmpty && error != null
                ? new MetadataResult(null, error)
                : new MetadataResult(data, null);
        }
    }

    internal static ExifData ParseObject(JObject obj)
    {
        DateTime? dateOriginal = ParseDate(Str(obj, "DateTimeOriginal"));
        DateTime? createDate   = ParseDate(Str(obj, "CreateDate"));
        DateTime? modifyDate   = ParseDate(Str(obj, "ModifyDate"));

        string? usedField = dateOriginal.HasValue ? "DateTimeOriginal"
            : createDate.HasValue ? "CreateDate"
            : modifyDate.HasValue ? "ModifyDate"
            : null;

        return new ExifData
        {
            DateTimeOriginal       = dateOriginal,
            CreateDate             = createDate,
            ModifyDate             = modifyDate,
            Make                   = Str(obj, "Make"),
            Model                  = Str(obj, "Model"),
            LensModel              = Str(obj, "LensModel") ?? Str(obj, "LensID") ?? Str(obj, "Lens"),
            FocalLength            = Str(obj, "FocalLength"),
            Aperture               = Str(obj, "Aperture"),
            ShutterSpeed           = Str(obj, "ShutterSpeed"),
            ISO                    = Str(obj, "ISO"),
            ImageWidth             = ParseInt(Str(obj, "ImageWidth")),
            ImageHeight            = ParseInt(Str(obj, "ImageHeight")),
            Rating                 = ParseInt(Str(obj, "Rating")),
            GPSLatitude            = ParseDouble(Str(obj, "GPSLatitude")),
            GPSLongitude           = ParseDouble(Str(obj, "GPSLongitude")),
            GeolocationCity        = Str(obj, "GeolocationCity"),
            GeolocationRegion      = Str(obj, "GeolocationRegion"),
            GeolocationCountry     = Str(obj, "GeolocationCountry"),
            GeolocationCountryCode = Str(obj, "GeolocationCountryCode"),
            UsedDateField          = usedField,
        };
    }

    private static string? Str(JObject obj, string name)
    {
        var v = obj[name]?.ToString();
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    internal static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // "0000:00:00 00:00:00" のような空の日付は無視する
        if (value.StartsWith("0000", StringComparison.Ordinal)) return null;

        if (DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var dto))
        {
            // タイムゾーン付きの値は撮影地の現地時刻（表記どおり）を使う
            return dto.DateTime;
        }
        return null;
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? ParseInt(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : null;
}

/// <summary>exiftool の実行ファイルを探す</summary>
public static class ExifToolLocator
{
    private static readonly string[] CandidateNames = OperatingSystem.IsWindows()
        ? ["exiftool.exe", "exiftool(-k).exe"]
        : ["exiftool"];

    /// <summary>
    /// 指定パス → アプリ同梱の Resources フォルダ → PATH の順で探す。
    /// </summary>
    public static string Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var p = configuredPath.Trim().Trim('"');
            if (Directory.Exists(p))
            {
                foreach (var name in CandidateNames)
                {
                    var inDir = Path.Combine(p, name);
                    if (File.Exists(inDir)) return inDir;
                }
            }
            return p;
        }

        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { Path.Combine(baseDir, "Resources"), baseDir })
            foreach (var name in CandidateNames)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }

        return "exiftool";
    }
}
