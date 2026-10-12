using PhotoExplorer.Infrastructure;

namespace PhotoExplorer.Application;

public sealed record UndoResult(int Restored, int Skipped, int Failed, IReadOnlyList<LogEntry> Log);

/// <summary>操作ジャーナルをもとに、ある実行で行ったコピー・移動を取り消す</summary>
public sealed class UndoService
{
    private readonly OperationJournal _journal;
    private readonly StatsHistoryRepository _history;

    public UndoService(OperationJournal journal, StatsHistoryRepository history)
    {
        _journal = journal;
        _history = history;
    }

    public Task<UndoResult> UndoAsync(string runId, string? outputRoot, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var log = new List<LogEntry>();
            int restored = 0, skipped = 0, failed = 0;
            var ops = _journal.GetOperations(runId);
            if (ops.Count == 0)
            {
                log.Add(LogEntry.Warn("この実行には取り消せるファイル操作がありません。"));
                return new UndoResult(0, 0, 0, log);
            }

            log.Add(LogEntry.Info($"{ops.Count} 件の操作を新しい順に取り消します..."));

            foreach (var op in ops.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    switch (op.Kind)
                    {
                        case OperationKind.Copy:
                            if (!File.Exists(op.Destination))
                            {
                                skipped++;
                                log.Add(LogEntry.Warn($"既に存在しません: {op.Destination}"));
                                break;
                            }
                            if (!await IsUnchangedAsync(op, cancellationToken).ConfigureAwait(false))
                            {
                                skipped++;
                                log.Add(LogEntry.Warn($"コピー後に変更されているため残しました: {op.Destination}"));
                                break;
                            }
                            File.Delete(op.Destination);
                            restored++;
                            log.Add(LogEntry.Ok($"削除: {op.Destination}"));
                            if (outputRoot != null) PathUtil.DeleteEmptyParents(Path.GetDirectoryName(op.Destination), outputRoot);
                            break;

                        case OperationKind.Move:
                        case OperationKind.Trash:
                            if (!File.Exists(op.Destination))
                            {
                                skipped++;
                                log.Add(LogEntry.Warn($"移動先にファイルがありません: {op.Destination}"));
                                break;
                            }
                            if (File.Exists(op.Source))
                            {
                                skipped++;
                                log.Add(LogEntry.Warn($"元の場所に同名ファイルがあるため戻せません: {op.Source}"));
                                break;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(op.Source)!);
                            File.Move(op.Destination, op.Source);
                            restored++;
                            log.Add(LogEntry.Ok($"復元: {op.Destination} => {op.Source}"));
                            if (op.Kind == OperationKind.Move && outputRoot != null)
                                PathUtil.DeleteEmptyParents(Path.GetDirectoryName(op.Destination), outputRoot);
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    log.Add(LogEntry.Error($"取り消し失敗: {op.Destination} - {ex.Message}"));
                }
            }

            _history.MarkUndone(runId, DateTime.Now);
            log.Add(LogEntry.Info($"取り消し完了: 復元 {restored} / スキップ {skipped} / 失敗 {failed}"));
            return new UndoResult(restored, skipped, failed, log);
        }, cancellationToken);

    private static async Task<bool> IsUnchangedAsync(JournalEntry op, CancellationToken ct)
    {
        if (new FileInfo(op.Destination).Length != op.Size) return false;
        if (op.Hash == null) return true;
        var hash = await FileHasher.ComputeFullAsync(op.Destination, ct).ConfigureAwait(false);
        return string.Equals(hash, op.Hash, StringComparison.OrdinalIgnoreCase);
    }
}
