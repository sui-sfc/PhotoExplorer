namespace PhotoExplorer.Domain.Services;

/// <summary>カメラ・レンズ名の表記ゆれを正規化するサービス</summary>
public interface INormalizer
{
    /// <summary>
    /// カメラ名を正規化する。候補を順に（マッピング完全一致 → 部分一致 → 組み込み）照合する。
    /// </summary>
    string NormalizeCamera(params string?[] candidates);

    string NormalizeLens(string? rawLens);

    /// <summary>テンプレート変数の値をファイル名として安全な文字列にする（空白は _ に置換）</summary>
    string SanitizeForPath(string value);
}
