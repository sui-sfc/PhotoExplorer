using PhotoExplorer.Domain.Entities;

namespace PhotoExplorer.Domain.Services;

public sealed record PathOptions(
    string OutputRoot,
    string FolderTemplate,
    string FileNameTemplate = "",
    string NoDateFolderName = "");

/// <summary>テンプレートから出力先パスを解決するサービス</summary>
public interface IPathResolver
{
    /// <summary>アセットの出力先フルパスを返す</summary>
    string Resolve(ImageAsset asset, PathOptions options);

    /// <summary>テンプレート中の未対応の {変数} を返す</summary>
    IReadOnlyList<string> FindUnknownVariables(string template);
}
