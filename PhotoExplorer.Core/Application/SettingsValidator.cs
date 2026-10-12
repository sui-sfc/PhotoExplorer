using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.Services;
using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Application;

public sealed record ValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>実行前に設定の矛盾や危険な組み合わせをチェックする</summary>
public static class SettingsValidator
{
    public static ValidationResult Validate(AppSettings s, IPathResolver resolver, bool checkWritable = true)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        bool writes = s.Mode != OperationMode.DryRun;

        if (string.IsNullOrWhiteSpace(s.InputDirectory))
            errors.Add("入力フォルダを指定してください。");
        else if (!Directory.Exists(s.InputDirectory))
            errors.Add($"入力フォルダが見つかりません: {s.InputDirectory}");

        bool hasOutput = !string.IsNullOrWhiteSpace(s.OutputDirectory);
        if (writes && !hasOutput)
            errors.Add("COPY / MOVE では出力フォルダを指定してください。");

        if (hasOutput && !string.IsNullOrWhiteSpace(s.InputDirectory) && !IsRooted(s.OutputDirectory))
            errors.Add("出力フォルダはドライブ名から始まるフルパスで指定してください。");

        if (hasOutput && !string.IsNullOrWhiteSpace(s.InputDirectory) && IsRooted(s.OutputDirectory) && IsRooted(s.InputDirectory))
        {
            if (PathUtil.PathEquals(s.InputDirectory, s.OutputDirectory))
            {
                if (writes) errors.Add("入力フォルダと出力フォルダが同じです。別のフォルダを指定してください。");
                else warnings.Add("入力フォルダと出力フォルダが同じです。");
            }
            else if (PathUtil.IsSameOrUnder(s.OutputDirectory, s.InputDirectory))
                warnings.Add("出力フォルダが入力フォルダの中にあります（出力フォルダはスキャン対象から除外します）。");
        }

        if (!string.IsNullOrWhiteSpace(s.TrashDirectory))
        {
            if (!IsRooted(s.TrashDirectory))
                errors.Add("重複の退避先はフルパスで指定してください。");
            else
            {
                if (IsRooted(s.InputDirectory) && PathUtil.PathEquals(s.TrashDirectory, s.InputDirectory))
                    errors.Add("重複の退避先が入力フォルダと同じです。");
                if (hasOutput && IsRooted(s.OutputDirectory) && PathUtil.PathEquals(s.TrashDirectory, s.OutputDirectory))
                    errors.Add("重複の退避先が出力フォルダと同じです。");
            }
        }

        if (string.IsNullOrWhiteSpace(s.FolderTemplate))
            errors.Add("フォルダテンプレートを入力してください。");
        else
        {
            var unknown = resolver.FindUnknownVariables(s.FolderTemplate);
            if (unknown.Count > 0)
                errors.Add($"フォルダテンプレートに使えない変数があります: {string.Join(" ", unknown)}");
        }

        if (!string.IsNullOrWhiteSpace(s.FileNameTemplate))
        {
            var unknown = resolver.FindUnknownVariables(s.FileNameTemplate);
            if (unknown.Count > 0)
                errors.Add($"ファイル名テンプレートに使えない変数があります: {string.Join(" ", unknown)}");
            if (!s.FileNameTemplate.Contains("{name}") && !s.FileNameTemplate.Contains("{ss}"))
                warnings.Add("ファイル名テンプレートに {name} か {ss} が無いと、同じ名前になったファイルに _001 のような連番が付きます。");
        }

        if (s.FilterStartDate.HasValue && s.FilterEndDate.HasValue && s.FilterStartDate > s.FilterEndDate)
            errors.Add("日付フィルタの開始日が終了日より後になっています。");

        if (s.MaxDegreeOfParallelism is < 1 or > 32)
            errors.Add("並列数は 1〜32 の範囲で指定してください。");

        if (writes && checkWritable && hasOutput && errors.Count == 0)
        {
            var problem = CheckWritable(s.OutputDirectory);
            if (problem != null) errors.Add(problem);
            if (!string.IsNullOrWhiteSpace(s.TrashDirectory))
            {
                problem = CheckWritable(s.TrashDirectory);
                if (problem != null) errors.Add(problem);
            }
        }

        return new ValidationResult(errors, warnings);
    }

    private static bool IsRooted(string path)
    {
        try { return Path.IsPathFullyQualified(path); }
        catch { return false; }
    }

    /// <summary>フォルダを作成し、試しにファイルを書いてみる</summary>
    public static string? CheckWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".photoexplorer_write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return $"フォルダに書き込めません: {directory}（{ex.Message}）";
        }
    }
}
