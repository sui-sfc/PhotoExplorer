using System.Diagnostics;
using Newtonsoft.Json;
using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Infrastructure;

/// <summary>再起動をまたいで保持する設定（preferences.json）</summary>
public sealed class UserPreferences
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "preferences.json");

    public string InputDirectory   { get; set; } = string.Empty;
    public string OutputDirectory  { get; set; } = string.Empty;
    public string TrashDirectory   { get; set; } = string.Empty;
    public string FolderTemplate   { get; set; } = "{yyyy}/{mm}/{dd}/{camera}";
    public string FileNameTemplate { get; set; } = string.Empty;
    public OperationMode Mode      { get; set; } = OperationMode.DryRun;
    public int MaxParallelism      { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    public DateTime? FilterStartDate { get; set; }
    public DateTime? FilterEndDate   { get; set; }

    public string ExifToolPath     { get; set; } = string.Empty;
    public bool IncludeVideos      { get; set; }
    public bool IncludeSidecars    { get; set; } = true;
    public bool VerifyCopies       { get; set; } = true;
    public bool DetectDuplicates   { get; set; } = true;
    public bool UseMetadataCache   { get; set; } = true;
    public bool UseNoDateFolder    { get; set; }
    public string NoDateFolderName { get; set; } = "_NoDate";
    public bool VerboseLog         { get; set; }

    public double WindowLeft   { get; set; } = double.NaN;
    public double WindowTop    { get; set; } = double.NaN;
    public double WindowWidth  { get; set; } = 1180;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }
    public int SelectedTab     { get; set; }

    public static UserPreferences Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<UserPreferences>(File.ReadAllText(path)) ?? new UserPreferences();
        }
        catch (Exception ex)
        {
            // 壊れた設定ファイルは退避して既定値で起動する
            Debug.WriteLine($"preferences.json を読み込めません: {ex.Message}");
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
        }
        return new UserPreferences();
    }

    /// <summary>一時ファイルに書いてから置き換える（書き込み途中で落ちても壊れない）</summary>
    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"preferences.json を保存できません: {ex.Message}");
            return false;
        }
    }
}
