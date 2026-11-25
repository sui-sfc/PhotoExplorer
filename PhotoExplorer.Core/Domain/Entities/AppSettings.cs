using PhotoExplorer.Domain.Enums;

namespace PhotoExplorer.Domain.Entities;

/// <summary>1回の整理処理に使う設定</summary>
public sealed class AppSettings
{
    public string InputDirectory { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>フォルダ構成テンプレート（例: {yyyy}/{mm}/{dd}/{camera}）</summary>
    public string FolderTemplate { get; set; } = "{yyyy}/{mm}/{dd}/{camera}";

    /// <summary>ファイル名テンプレート（空なら元のファイル名のまま。拡張子は自動で付く）</summary>
    public string FileNameTemplate { get; set; } = string.Empty;

    public OperationMode Mode { get; set; } = OperationMode.DryRun;

    /// <summary>重複ファイルの退避先（空なら重複はその場に残す）</summary>
    public string TrashDirectory { get; set; } = string.Empty;

    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>exiftool のパス（空なら自動検出）</summary>
    public string ExifToolPath { get; set; } = string.Empty;

    public DateTime? FilterStartDate { get; set; }
    public DateTime? FilterEndDate { get; set; }

    /// <summary>対象にする最低レーティング（0 ならすべて対象、1〜5 ならその★以上のみ）</summary>
    public int MinRating { get; set; }

    /// <summary>動画（mp4/mov など）も対象にする</summary>
    public bool IncludeVideos { get; set; }

    /// <summary>XMP サイドカーを本体と一緒にコピー/移動する</summary>
    public bool IncludeSidecars { get; set; } = true;

    /// <summary>コピー後にハッシュで内容を検証する</summary>
    public bool VerifyCopies { get; set; } = true;

    /// <summary>重複検出を行う</summary>
    public bool DetectDuplicates { get; set; } = true;

    /// <summary>前回読み取った EXIF・ハッシュを再利用する（ファイルが変わっていない場合のみ）</summary>
    public bool UseMetadataCache { get; set; } = true;

    /// <summary>MOVE 実行後に空になった入力側のサブフォルダを自動で削除する</summary>
    public bool CleanEmptySourceFolders { get; set; }

    /// <summary>EXIF 日付がないファイルの置き場所（空ならファイル日時でテンプレートに従う）</summary>
    public string NoDateFolderName { get; set; } = string.Empty;

    /// <summary>診断用の詳細ログを出す</summary>
    public bool VerboseLog { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
