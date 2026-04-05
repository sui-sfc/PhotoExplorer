using System.IO;
using Newtonsoft.Json;

namespace PhotoExplorer.Infrastructure;

/// <summary>カメラ・レンズ名のカスタムマッピング設定</summary>
public sealed class MappingConfig
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoExplorer", "mappings.json");

    /// <summary>カメラ名マッピング: 元の名前 → 表示名</summary>
    public Dictionary<string, string> Camera { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>レンズ名マッピング: 元の名前 → 表示名</summary>
    public Dictionary<string, string> Lens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static MappingConfig Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<MappingConfig>(json) ?? new MappingConfig();
        }
        catch
        {
            return new MappingConfig();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    /// <summary>サンプルエントリ付きのデフォルト設定を生成する</summary>
    public static MappingConfig CreateDefault() => new()
    {
        Camera = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RICOH IMAGING COMPANY, LTD. RICOH GR IIIx"] = "RICOH_GR_IIIx",
            // 追加例: ["Canon EOS R5"] = "Canon_R5",
        },
        Lens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 追加例: ["some long lens name"] = "Short_Lens",
        },
    };
}
