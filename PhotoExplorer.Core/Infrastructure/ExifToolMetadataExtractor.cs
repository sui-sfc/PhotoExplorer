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
/// 常駐モード（-stay_open）の exiftool に複数ファイルずつ渡すことで、起動コストを最小限にする。
/// </summary>
public sealed class ExifToolMetadataExtractor : IMetadataExtractor
{
    private const int BatchSize = 50;
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

        var batches = new ConcurrentQueue<string[]>(filePaths.Chunk(BatchSize));
        int workers = Math.Clamp(maxDegreeOfParallelism, 1, Math.Max(1, batches.Count));
        int done = 0;

        // exiftool を「常駐モード」（-stay_open）で workers 個だけ起動し、バッチを順に流し込む。
        // バッチごとに起動し直さないので、起動の遅い Windows 版 exiftool でも速い。
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            StayOpenSession? session = null;
            try
            {
                while (batches.TryDequeue(out var batch))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    session ??= StayOpenSession.TryStart(this);
                    var batchResult = session == null
                        ? Failed(batch, "exiftool を起動できませんでした")
                        : await session.ExtractAsync(batch, cancellationToken).ConfigureAwait(false);

                    // タイムアウトなどで壊れたセッションは捨てて、次のバッチで起動し直す
                    if (session is { IsBroken: true })
                    {
                        session.Dispose();
                        session = null;
                    }

                    foreach (var (path, r) in batchResult) results[path] = r;
                    completedCount?.Report(Interlocked.Add(ref done, batch.Length));
                }
            }
            finally
            {
                session?.Dispose();
            }
        }, cancellationToken)).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static Dictionary<string, MetadataResult> Failed(IEnumerable<string> paths, string message) =>
        paths.ToDictionary(p => p, _ => new MetadataResult(null, message), PathUtil.Comparer);

    /// <summary>常駐させた exiftool 1 プロセス。引数は標準入力から UTF-8 で渡す（日本語パス対策）</summary>
    private sealed class StayOpenSession : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stderr = new();
        private readonly Task _stderrPump;
        private int _commandId;

        private StayOpenSession(Process process)
        {
            _process = process;
            _process.StandardInput.AutoFlush = false;
            // stderr は常に読み続ける（溜まるとバッファが詰まって exiftool が止まる）
            _stderrPump = Task.Run(async () =>
            {
                var buffer = new char[4096];
                int n;
                try
                {
                    while ((n = await _process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        lock (_stderr) _stderr.Append(buffer, 0, n);
                }
                catch { }
            });
        }

        public bool IsBroken { get; private set; }

        public static StayOpenSession? TryStart(ExifToolMetadataExtractor owner)
        {
            try
            {
                var args = new List<string> { "-stay_open", "True", "-@", "-", "-common_args", "-charset", "filename=utf8" };
                args.AddRange(CommonArgs);
                return new StayOpenSession(owner.StartProcess(args));
            }
            catch
            {
                return null;
            }
        }

        public async Task<Dictionary<string, MetadataResult>> ExtractAsync(string[] paths, CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, MetadataResult>(PathUtil.Comparer);
            int id = ++_commandId;
            lock (_stderr) _stderr.Clear();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60 + paths.Length * 3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                var input = _process.StandardInput;
                foreach (var path in paths) await input.WriteLineAsync(path.AsMemory(), linked.Token).ConfigureAwait(false);
                await input.WriteLineAsync($"-execute{id}".AsMemory(), linked.Token).ConfigureAwait(false);
                await input.FlushAsync(linked.Token).ConfigureAwait(false);

                // 「{readyN}」の行が出るまでが、このバッチの出力
                var output = new StringBuilder();
                var marker = $"{{ready{id}}}";
                while (true)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false)
                               ?? throw new IOException("exiftool が終了しました");
                    if (line.Trim() == marker) break;
                    output.AppendLine(line);
                }

                ParseOutput(output.ToString(), results);
            }
            catch (OperationCanceledException)
            {
                IsBroken = true;
                if (cancellationToken.IsCancellationRequested) throw;
                return Failed(paths, "exiftool がタイムアウトしました");
            }
            catch (Exception ex)
            {
                IsBroken = true;
                return Failed(paths, $"exiftool を実行できませんでした: {ex.Message}");
            }

            string stderr;
            lock (_stderr) stderr = _stderr.ToString();
            var firstError = FirstErrorLine(stderr);
            foreach (var p in paths)
                if (!results.ContainsKey(p))
                    results[p] = new MetadataResult(null, firstError ?? "exiftool から結果が返りませんでした");
            return results;
        }

        public void Dispose()
        {
            try
            {
                if (!IsBroken && !_process.HasExited)
                {
                    _process.StandardInput.WriteLine("-stay_open");
                    _process.StandardInput.WriteLine("False");
                    _process.StandardInput.Flush();
                }
                _process.StandardInput.Close(); // exiftool(-k).exe の「キー入力待ち」もここで抜ける
                if (!_process.WaitForExit(3000)) KillQuietly(_process);
            }
            catch
            {
                KillQuietly(_process);
            }
            _process.Dispose();
        }
    }

    private static readonly string[] CommonArgs =
        new[] { "-json", "-n", "-fast", "-q", "-api", "QuickTimeUTC=1", "-api", "Geolocation=1" }.Concat(Tags).ToArray();

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
