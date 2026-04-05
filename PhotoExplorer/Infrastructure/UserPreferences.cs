using System.IO;
using Newtonsoft.Json;
using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Infrastructure;

/// <summary>再起動をまたいで保持するUI設定</summary>
public sealed class UserPreferences
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoExplorer", "preferences.json");

    public string InputDirectory  { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string TrashDirectory  { get; set; } = string.Empty;
    public string FolderTemplate  { get; set; } = "{yyyy}/{mm}/{dd}/{camera}";
    public OperationMode Mode     { get; set; } = OperationMode.DryRun;
    public int MaxParallelism     { get; set; } = 4;

    public static UserPreferences Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                var json = File.ReadAllText(DefaultPath);
                return JsonConvert.DeserializeObject<UserPreferences>(json) ?? new UserPreferences();
            }
        }
        catch { /* 読み込み失敗時はデフォルト値を使用 */ }
        return new UserPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            File.WriteAllText(DefaultPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { /* 保存失敗は無視 */ }
    }
}
