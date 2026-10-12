using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Domain.Entities;

/// <summary>管理対象の画像・動画ファイルを表すエンティティ</summary>
public sealed class ImageAsset
{
    public long? Id { get; set; }

    /// <summary>元のファイルパス（絶対パス）</summary>
    public string SourcePath { get; set; } = string.Empty;

    public long FileSize { get; set; }

    /// <summary>ファイル作成日時（コピーでリセットされやすいので参考値）</summary>
    public DateTime FileCreatedAt { get; set; }

    /// <summary>ファイル更新日時（コピーしても保持される）</summary>
    public DateTime FileModifiedAt { get; set; }

    /// <summary>先頭64KBのSHA-256（高速重複チェック用）</summary>
    public string? PartialHash { get; set; }

    /// <summary>ファイル全体のSHA-256</summary>
    public string? FullHash { get; set; }

    public ExifData? Exif { get; set; }

    /// <summary>exiftool による読み取りを試みたか（結果が空でも true）</summary>
    public bool MetadataExtracted { get; set; }

    public string? NormalizedCamera { get; set; }
    public string? NormalizedLens { get; set; }

    /// <summary>計算された（または実際の）出力先パス</summary>
    public string? OutputPath { get; set; }

    public AssetStatus Status { get; set; } = AssetStatus.Scanned;
    public string? ErrorMessage { get; set; }
    public DateTime? ProcessedAt { get; set; }

    /// <summary>重複グループID（同一内容のファイルを識別）</summary>
    public string? DuplicateGroupId { get; set; }

    /// <summary>重複グループの中で残すファイルか</summary>
    public bool IsKeptInDuplicateGroup { get; set; } = true;

    /// <summary>一緒に扱う XMP サイドカー（保存はしない）</summary>
    public string? SidecarPath { get; set; }

    public string Extension => Path.GetExtension(SourcePath).ToLowerInvariant();
    public string FileName => Path.GetFileName(SourcePath);
    public MediaKind Kind => MediaFormats.GetKind(SourcePath) ?? MediaKind.Raw;

    /// <summary>
    /// フォルダ分けに使う日付。EXIF がなければ作成日時・更新日時の古い方を使う
    /// （コピーで作成日時だけが新しくなるケースに対応）。
    /// </summary>
    public DateTime BestDate => Exif?.EffectiveDate ?? FallbackFileDate;

    public bool HasExifDate => Exif?.EffectiveDate != null;

    public DateTime FallbackFileDate
    {
        get
        {
            if (FileModifiedAt == default) return FileCreatedAt;
            if (FileCreatedAt == default) return FileModifiedAt;
            return FileCreatedAt < FileModifiedAt ? FileCreatedAt : FileModifiedAt;
        }
    }
}
