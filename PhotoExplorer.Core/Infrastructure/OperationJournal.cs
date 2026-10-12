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
    private readonly object _lock = new();

    public OperationJournal(string databasePath)
    {
        _connection = Open(databasePath);
        EnsureSchema(_connection);
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
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Operations (RunId, Kind, Source, Destination, Hash, Size, At)
                VALUES (@R, @K, @S, @D, @H, @Z, @A)
                """;
            cmd.Parameters.AddWithValue("@R", runId);
            cmd.Parameters.AddWithValue("@K", kind.ToString());
            cmd.Parameters.AddWithValue("@S", source);
            cmd.Parameters.AddWithValue("@D", destination);
            cmd.Parameters.AddWithValue("@H", Db(hash));
            cmd.Parameters.AddWithValue("@Z", size);
            cmd.Parameters.AddWithValue("@A", Db(DateTime.Now));
            cmd.ExecuteNonQuery();
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
            while (r.Read())
            {
                list.Add(new JournalEntry(
                    r.GetInt64(r.GetOrdinal("Id")),
                    r.GetString(r.GetOrdinal("RunId")),
                    Enum.TryParse<OperationKind>(Str(r, "Kind"), out var k) ? k : OperationKind.Copy,
                    r.GetString(r.GetOrdinal("Source")),
                    r.GetString(r.GetOrdinal("Destination")),
                    Str(r, "Hash"),
                    r.GetInt64(r.GetOrdinal("Size")),
                    Date(r, "At") ?? DateTime.MinValue));
            }
            return list;
        }
    }

    public void Dispose() => _connection.Dispose();
}
