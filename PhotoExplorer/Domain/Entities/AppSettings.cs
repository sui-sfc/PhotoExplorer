using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Domain.Entities;

/// <summary>アプリケーション設定</summary>
public sealed class AppSettings
{
    /// <summary>入力ディレクトリ（スキャン対象）</summary>
    public string InputDirectory { get; set; } = string.Empty;

    /// <summary>出力ディレクトリ（整理先）</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// フォルダ生成テンプレート
    /// 使用可能変数: {yyyy} {mm} {dd} {camera} {lens}
    /// </summary>
    public string FolderTemplate { get; set; } = "{yyyy}/{mm}/{dd}/{camera}";

    /// <summary>操作モード</summary>
    public OperationMode Mode { get; set; } = OperationMode.DryRun;

    /// <summary>論理削除（重複ファイル）の移動先</summary>
    public string TrashDirectory { get; set; } = string.Empty;

    /// <summary>最大並列処理数</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>exiftoolの実行パス</summary>
    public string ExifToolPath { get; set; } = "exiftool.exe";

    /// <summary>SQLiteデータベースパス</summary>
    public string DatabasePath { get; set; } = "photo_explorer.db";

    /// <summary>処理対象の撮影開始日（null = 制限なし）</summary>
    public DateTime? FilterStartDate { get; set; }

    /// <summary>処理対象の撮影終了日（null = 制限なし）</summary>
    public DateTime? FilterEndDate { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
