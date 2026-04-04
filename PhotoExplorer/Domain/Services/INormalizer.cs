namespace PhotoExplorer.Domain.Services;

/// <summary>カメラ・レンズ名の表記ゆれを正規化するサービス</summary>
public interface INormalizer
{
    /// <summary>カメラ名を正規化する</summary>
    string NormalizeCamera(string? rawCamera);

    /// <summary>レンズ名を正規化する</summary>
    string NormalizeLens(string? rawLens);

    /// <summary>ファイルシステムで使用できない文字を置換する</summary>
    string SanitizeForPath(string value);
}
