using PhotoExplorer.Domain.Entities;

namespace PhotoExplorer.Domain.Services;

/// <summary>テンプレートから出力先パスを解決するサービス</summary>
public interface IPathResolver
{
    /// <summary>
    /// アセットの出力先パスを計算して返す
    /// </summary>
    /// <param name="asset">対象アセット</param>
    /// <param name="outputRoot">出力ルートディレクトリ</param>
    /// <param name="template">フォルダテンプレート</param>
    /// <returns>出力先フルパス</returns>
    string Resolve(ImageAsset asset, string outputRoot, string template);
}
