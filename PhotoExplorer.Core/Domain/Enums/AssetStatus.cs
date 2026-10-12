namespace PhotoExplorer.Domain.Enums;

public enum AssetStatus
{
    /// <summary>ファイルを検出した初期状態</summary>
    Scanned,
    /// <summary>撮影日時を含むメタデータを取得済み</summary>
    MetadataOk,
    /// <summary>メタデータはあるが撮影日時がない</summary>
    PartialMetadata,
    /// <summary>メタデータ取得不可</summary>
    NoMetadata,
    /// <summary>重複ファイルとして検出</summary>
    Duplicate,
    /// <summary>処理完了（コピー/移動済み）</summary>
    Processed,
    /// <summary>出力先に同一内容のファイルが既にあるためスキップ</summary>
    Skipped,
    /// <summary>日付フィルタで対象外</summary>
    Filtered,
    /// <summary>エラー発生</summary>
    Error,
}
