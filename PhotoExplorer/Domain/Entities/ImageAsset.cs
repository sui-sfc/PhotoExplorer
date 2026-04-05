using System.IO;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Domain.Entities;

/// <summary>管理対象の画像ファイルを表すエンティティ</summary>
public sealed class ImageAsset
{
    public long? Id { get; set; }

    /// <summary>元のファイルパス（絶対パス）</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>ファイルサイズ（バイト）</summary>
    public long FileSize { get; set; }

    /// <summary>ファイル作成日時</summary>
    public DateTime FileCreatedAt { get; set; }

    /// <summary>先頭部分のハッシュ（高速重複チェック用）</summary>
    public string? PartialHash { get; set; }

    /// <summary>ファイル全体のSHA-256ハッシュ</summary>
    public string? FullHash { get; set; }

    /// <summary>抽出されたEXIFデータ</summary>
    public ExifData? Exif { get; set; }

    /// <summary>正規化後のカメラ名</summary>
    public string? NormalizedCamera { get; set; }

    /// <summary>正規化後のレンズ名</summary>
    public string? NormalizedLens { get; set; }

    /// <summary>計算された出力先パス</summary>
    public string? OutputPath { get; set; }

    /// <summary>処理状態</summary>
    public AssetStatus Status { get; set; } = AssetStatus.Scanned;

    /// <summary>エラーメッセージ</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>処理日時</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>ファイル拡張子（小文字）</summary>
    public string Extension => Path.GetExtension(SourcePath).ToLowerInvariant();

    /// <summary>ファイル名</summary>
    public string FileName => Path.GetFileName(SourcePath);

    /// <summary>重複グループID（同一ファイルを識別）</summary>
    public string? DuplicateGroupId { get; set; }

    /// <summary>重複グループの中で保持するファイルか</summary>
    public bool IsKeptInDuplicateGroup { get; set; } = true;
}
