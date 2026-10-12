namespace PhotoExplorer.Domain.Enums;

public enum OperationMode
{
    /// <summary>実際の操作を行わず結果だけ確認</summary>
    DryRun,
    /// <summary>ファイルをコピー（元ファイルは残す）</summary>
    Copy,
    /// <summary>ファイルを移動（元ファイルは削除）</summary>
    Move,
}
