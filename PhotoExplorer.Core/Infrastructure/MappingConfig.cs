using Newtonsoft.Json;

namespace PhotoExplorer.Infrastructure;

/// <summary>カメラ・レンズ名のカスタムマッピング設定（mappings.json）</summary>
public sealed class MappingConfig
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "mappings.json");

    /// <summary>カメラ名マッピング: 元の名前 → 表示名</summary>
    public Dictionary<string, string> Camera { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>レンズ名マッピング: 元の名前 → 表示名</summary>
    public Dictionary<string, string> Lens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>JSON を読み込む。構文エラーなどは例外になる。</summary>
    public static MappingConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<MappingConfig>(json) ?? new MappingConfig();
        // JSON から作られた辞書は大文字小文字を区別するので作り直す
        return new MappingConfig
        {
            Camera = new Dictionary<string, string>(loaded.Camera ?? [], StringComparer.OrdinalIgnoreCase),
            Lens   = new Dictionary<string, string>(loaded.Lens   ?? [], StringComparer.OrdinalIgnoreCase),
        };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    /// <summary>サンプルエントリ付きのデフォルト設定</summary>
    public static MappingConfig CreateDefault() => new()
    {
        Camera = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RICOH GR IIIx"] = "RICOH_GR_IIIx",
        },
        Lens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RF24-105mm F4 L IS USM"] = "Canon_RF24-105_F4L",
        },
    };
}

/// <summary>
/// mappings.json を保持し、ファイルが更新されていたら自動で読み直す。
/// 読み込みに失敗した場合は直前の正常な設定を使い続け、LastError に理由を残す。
/// </summary>
public sealed class MappingConfigProvider
{
    private readonly string? _path;
    private MappingConfig _current;
    private DateTime _loadedStamp;
    private readonly object _lock = new();

    public MappingConfigProvider(string path)
    {
        _path = path;
        _current = new MappingConfig();
        if (!File.Exists(path))
        {
            try { MappingConfig.CreateDefault().Save(path); }
            catch (Exception ex) { LastError = $"mappings.json を作成できませんでした: {ex.Message}"; }
        }
        Reload();
    }

    private MappingConfigProvider(MappingConfig fixedConfig) => _current = fixedConfig;

    public static MappingConfigProvider Fixed(MappingConfig config) => new(config);

    public string? Path => _path;
    public string? LastError { get; private set; }

    public MappingConfig Current
    {
        get
        {
            lock (_lock)
            {
                if (_path != null && File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _loadedStamp)
                    Reload();
                return _current;
            }
        }
    }

    private void Reload()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            _loadedStamp = File.GetLastWriteTimeUtc(_path);
            _current = MappingConfig.Load(_path);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"mappings.json の読み込みに失敗しました（前回の設定を使用します）: {ex.Message}";
        }
    }
}
