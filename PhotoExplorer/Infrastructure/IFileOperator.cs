namespace PhotoExplorer.Infrastructure;

/// <summary>ファイルのコピー・移動・論理削除を行う</summary>
public interface IFileOperator
{
    /// <summary>ファイルをコピーする（上書き禁止・衝突時リネーム）</summary>
    /// <returns>実際の出力先パス</returns>
    Task<string> CopyAsync(string sourcePath, string destPath, CancellationToken cancellationToken = default);

    /// <summary>ファイルを移動する（上書き禁止・衝突時リネーム）</summary>
    /// <returns>実際の出力先パス</returns>
    Task<string> MoveAsync(string sourcePath, string destPath, CancellationToken cancellationToken = default);

    /// <summary>ファイルをゴミ箱ディレクトリへ移動する（論理削除）</summary>
    /// <returns>ゴミ箱内のパス</returns>
    Task<string> MoveToTrashAsync(string sourcePath, string trashDirectory, CancellationToken cancellationToken = default);
}
