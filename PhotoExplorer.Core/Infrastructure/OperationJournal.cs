using Microsoft.Data.Sqlite;
using static PhotoExplorer.Infrastructure.SqliteDb;

namespace PhotoExplorer.Infrastructure;

public enum OperationKind { Copy, Move, Trash }

public sealed record JournalEntry(
    long Id, string RunId, OperationKind Kind, string Source, string Destination,
    string? Hash, long Size, DateTime At);

/// <summary>
/// 実行したファイル操作を 1 件ずつ即座に記録する。
/// 途中で中断・クラッシュしても、記録済みの操作は「元に戻す」で取り消せる。
/// </summary>
public sealed class OperationJournal : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insertCmd;
    private readonly SqliteParameter _pRunId, _pKind, _pSource, _pDest, _pHash, _pSize, _pAt;
    private readonly object _lock = new();

    public OperationJournal(string databasePath)
    {
        _connection = Open(databasePath);
        EnsureSchema(_connection);
        _insertCmd = _connection.CreateCommand();
        _insertCmd.CommandText = """
            INSERT INTO Operations (RunId, Kind, Source, Destination, Hash, Size, At)
            VALUES (@R, @K, @S, @D, @H, @Z, @A)
            """;
        _pRunId  = _insertCmd.Parameters.Add("@R", SqliteType.Text);
        _pKind   = _insertCmd.Parameters.Add("@K", SqliteType.Text);
        _pSource = _insertCmd.Parameters.Add("@S", SqliteType.Text);
        _pDest   = _insertCmd.Parameters.Add("@D", SqliteType.Text);
        _pHash   = _insertCmd.Parameters.Add("@H", SqliteType.Text);
        _pSize   = _insertCmd.Parameters.Add("@Z", SqliteType.Integer);
        _pAt     = _insertCmd.Parameters.Add("@A", SqliteType.Text);
        _insertCmd.Prepare();
    }

    internal static void EnsureSchema(SqliteConnection conn) => Execute(conn, """
        CREATE TABLE IF NOT EXISTS Operations (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId       TEXT NOT NULL,
            Kind        TEXT NOT NULL,
            Source      TEXT NOT NULL,
            Destination TEXT NOT NULL,
            Hash        TEXT,
            Size        INTEGER NOT NULL DEFAULT 0,
            At          TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Operations_RunId ON Operations(RunId);
        """);

    public void Record(string runId, OperationKind kind, string source, string destination, string? hash, long size)
    {
        lock (_lock)
        {
            _pRunId.Value  = runId;
            _pKind.Value   = kind.ToString();
            _pSource.Value = source;
            _pDest.Value   = destination;
            _pHash.Value   = Db(hash);
            _pSize.Value   = size;
            _pAt.Value     = Db(DateTime.Now);
            _insertCmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<JournalEntry> GetOperations(string runId)
    {
        lock (_lock)
        {
            var list = new List<JournalEntry>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Operations WHERE RunId = @R ORDER BY Id";
            cmd.Parameters.AddWithValue("@R", runId);
            using var r = cmd.ExecuteReader();
            int oId = r.GetOrdinal("Id"), oRunId = r.GetOrdinal("RunId"), oKind = r.GetOrdinal("Kind"),
                oSrc = r.GetOrdinal("Source"), oDest = r.GetOrdinal("Destination"),
                oHash = r.GetOrdinal("Hash"), oSize = r.GetOrdinal("Size"), oAt = r.GetOrdinal("At");
            while (r.Read())
            {
                list.Add(new JournalEntry(
                    r.GetInt64(oId),
                    r.GetString(oRunId),
                    Enum.TryParse<OperationKind>(r.IsDBNull(oKind) ? null : r.GetString(oKind), out var k) ? k : OperationKind.Copy,
                    r.GetString(oSrc),
                    r.GetString(oDest),
                    r.IsDBNull(oHash) ? null : r.GetString(oHash),
                    r.GetInt64(oSize),
                    ParseDate(r.IsDBNull(oAt) ? null : r.GetString(oAt)) ?? DateTime.MinValue));
            }
            return list;
        }
    }

    public void Dispose()
    {
        _insertCmd.Dispose();
        _connection.Dispose();
    }
}
