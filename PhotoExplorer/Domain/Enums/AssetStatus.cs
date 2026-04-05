namespace PhotoExplorer.Domain.Enums;

public enum AssetStatus
{
    /// <summary>ファイルを検出した初期状態</summary>
    Scanned,
    /// <summary>メタデータ完全取得済み</summary>
    MetadataOk,
    /// <summary>メタデータ一部取得</summary>
    PartialMetadata,
    /// <summary>メタデータ取得不可</summary>
    NoMetadata,
    /// <summary>重複ファイルとして検出</summary>
    Duplicate,
    /// <summary>処理完了（コピー/移動済み）</summary>
    Processed,
    /// <summary>エラー発生</summary>
    Error,
}
