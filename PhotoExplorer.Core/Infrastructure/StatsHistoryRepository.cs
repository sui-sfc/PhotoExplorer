using Microsoft.Data.Sqlite;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Enums;
using static PhotoExplorer.Infrastructure.SqliteDb;

namespace PhotoExplorer.Infrastructure;

public sealed record RunSummary(
    string RunId,
    DateTime RunAt,
    OperationMode Mode,
    string InputDirectory,
    string OutputDirectory,
    ScanStatistics Stats,
    int Processed,
    int Skipped,
    int Duplicates,
    int Errors,
    double ElapsedSeconds,
    bool Cancelled);

public sealed record RunHistoryRecord(
    long Id,
    string? RunId,
    DateTime RunAt,
    string Mode,
    string InputDirectory,
    string? OutputDirectory,
    int TotalFiles,
    int JpegCount,
    int RawCount,
    int WithGps,
    int Processed,
    int Skipped,
    int Duplicates,
    int Errors,
    double ElapsedSeconds,
    bool Cancelled,
    DateTime? UndoneAt,
    int OperationCount);

/// <summary>実行履歴（統計）を SQLite に保存する</summary>
public sealed class StatsHistoryRepository : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public StatsHistoryRepository(string databasePath)
    {
        _connection = Open(databasePath);
        Execute(_connection, """
            CREATE TABLE IF NOT EXISTS RunHistory (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                RunAt          TEXT NOT NULL,
                InputDirectory TEXT,
                TotalFiles     INTEGER NOT NULL DEFAULT 0,
                JpegCount      INTEGER NOT NULL DEFAULT 0,
                RawCount       INTEGER NOT NULL DEFAULT 0,
                WithExif       INTEGER NOT NULL DEFAULT 0,
                WithGps        INTEGER NOT NULL DEFAULT 0,
                Filtered       INTEGER NOT NULL DEFAULT 0,
                Processed      INTEGER NOT NULL DEFAULT 0,
                Duplicates     INTEGER NOT NULL DEFAULT 0,
                Errors         INTEGER NOT NULL DEFAULT 0,
                EarliestShot   TEXT,
                LatestShot     TEXT
            );
            CREATE TABLE IF NOT EXISTS RunCameraStats (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId      INTEGER NOT NULL REFERENCES RunHistory(Id) ON DELETE CASCADE,
                CameraName TEXT NOT NULL,
                Count      INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS RunExtStats (
                Id    INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL REFERENCES RunHistory(Id) ON DELETE CASCADE,
                Ext   TEXT NOT NULL,
                Count INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS RunLensStats (
                Id       INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId    INTEGER NOT NULL REFERENCES RunHistory(Id) ON DELETE CASCADE,
                LensName TEXT NOT NULL,
                Count    INTEGER NOT NULL
            );
            """);
        AddMissingColumns(_connection, "RunHistory",
        [
            ("RunGuid", "TEXT"), ("Mode", "TEXT"), ("OutputDirectory", "TEXT"),
            ("Skipped", "INTEGER NOT NULL DEFAULT 0"), ("ElapsedSeconds", "REAL NOT NULL DEFAULT 0"),
            ("Cancelled", "INTEGER NOT NULL DEFAULT 0"), ("UndoneAt", "TEXT"),
        ]);
        OperationJournal.EnsureSchema(_connection);
    }

    public void SaveRun(RunSummary run)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            long rowId;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO RunHistory
                        (RunAt, RunGuid, Mode, InputDirectory, OutputDirectory, TotalFiles, JpegCount, RawCount,
                         WithExif, WithGps, Filtered, Processed, Skipped, Duplicates, Errors,
                         EarliestShot, LatestShot, ElapsedSeconds, Cancelled)
                    VALUES
                        (@RunAt, @RunGuid, @Mode, @Input, @Output, @Total, @Jpeg, @Raw,
                         @WithExif, @WithGps, @Filtered, @Processed, @Skipped, @Duplicates, @Errors,
                         @Earliest, @Latest, @Elapsed, @Cancelled)
                    RETURNING Id;
                    """;
                var s = run.Stats;
                cmd.Parameters.AddWithValue("@RunAt",      Db(run.RunAt));
                cmd.Parameters.AddWithValue("@RunGuid",    run.RunId);
                cmd.Parameters.AddWithValue("@Mode",       run.Mode.ToString());
                cmd.Parameters.AddWithValue("@Input",      run.InputDirectory);
                cmd.Parameters.AddWithValue("@Output",     run.OutputDirectory);
                cmd.Parameters.AddWithValue("@Total",      s.TotalFiles);
                cmd.Parameters.AddWithValue("@Jpeg",       s.JpegCount);
                cmd.Parameters.AddWithValue("@Raw",        s.RawCount);
                cmd.Parameters.AddWithValue("@WithExif",   s.WithExif);
                cmd.Parameters.AddWithValue("@WithGps",    s.WithGps);
                cmd.Parameters.AddWithValue("@Filtered",   s.Filtered);
                cmd.Parameters.AddWithValue("@Processed",  run.Processed);
                cmd.Parameters.AddWithValue("@Skipped",    run.Skipped);
                cmd.Parameters.AddWithValue("@Duplicates", run.Duplicates);
                cmd.Parameters.AddWithValue("@Errors",     run.Errors);
                cmd.Parameters.AddWithValue("@Earliest",   Db(s.EarliestShot));
                cmd.Parameters.AddWithValue("@Latest",     Db(s.LatestShot));
                cmd.Parameters.AddWithValue("@Elapsed",    run.ElapsedSeconds);
                cmd.Parameters.AddWithValue("@Cancelled",  run.Cancelled ? 1 : 0);
                rowId = (long)cmd.ExecuteScalar()!;
            }

            InsertBreakdown(tx, "RunCameraStats", "CameraName", rowId, run.Stats.CameraBreakdown);
            InsertBreakdown(tx, "RunExtStats", "Ext", rowId, run.Stats.ExtBreakdown);
            InsertBreakdown(tx, "RunLensStats", "LensName", rowId, run.Stats.LensBreakdown);
            tx.Commit();
        }
    }

    private void InsertBreakdown(SqliteTransaction tx, string table, string column, long rowId,
        IReadOnlyList<(string Label, int Count)> items)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table} (RunId, {column}, Count) VALUES (@R, @L, @N)";
        var r = cmd.Parameters.Add("@R", SqliteType.Integer);
        var l = cmd.Parameters.Add("@L", SqliteType.Text);
        var n = cmd.Parameters.Add("@N", SqliteType.Integer);
        foreach (var (label, count) in items)
        {
            r.Value = rowId; l.Value = label; n.Value = count;
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<RunHistoryRecord> LoadHistory(int limit = 100)
    {
        lock (_lock)
        {
            var results = new List<RunHistoryRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT h.*,
                       (SELECT COUNT(*) FROM Operations o WHERE o.RunId = h.RunGuid) AS OpCount
                FROM RunHistory h
                ORDER BY h.Id DESC
                LIMIT @L
                """;
            cmd.Parameters.AddWithValue("@L", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                results.Add(new RunHistoryRecord(
                    Id:              r.GetInt64(r.GetOrdinal("Id")),
                    RunId:           Str(r, "RunGuid"),
                    RunAt:           Date(r, "RunAt") ?? DateTime.MinValue,
                    Mode:            Str(r, "Mode") ?? "",
                    InputDirectory:  Str(r, "InputDirectory") ?? "",
                    OutputDirectory: Str(r, "OutputDirectory"),
                    TotalFiles:      Int(r, "TotalFiles") ?? 0,
                    JpegCount:       Int(r, "JpegCount") ?? 0,
                    RawCount:        Int(r, "RawCount") ?? 0,
                    WithGps:         Int(r, "WithGps") ?? 0,
                    Processed:       Int(r, "Processed") ?? 0,
                    Skipped:         Int(r, "Skipped") ?? 0,
                    Duplicates:      Int(r, "Duplicates") ?? 0,
                    Errors:          Int(r, "Errors") ?? 0,
                    ElapsedSeconds:  Real(r, "ElapsedSeconds") ?? 0,
                    Cancelled:       Int(r, "Cancelled") == 1,
                    UndoneAt:        Date(r, "UndoneAt"),
                    OperationCount:  Int(r, "OpCount") ?? 0));
            }
            return results;
        }
    }

    /// <summary>全実行のカメラ別撮影枚数（累計）</summary>
    public IReadOnlyList<(string Label, int Count)> LoadCameraTotals(int top = 15)
    {
        lock (_lock)
        {
            var list = new List<(string, int)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT CameraName, SUM(Count) AS Total
                FROM RunCameraStats c JOIN RunHistory h ON h.Id = c.RunId
                WHERE h.Mode IS NULL OR h.Mode <> 'DryRun'
                GROUP BY CameraName ORDER BY Total DESC LIMIT @T
                """;
            cmd.Parameters.AddWithValue("@T", top);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
            return list;
        }
    }

    public void MarkUndone(string runId, DateTime at)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE RunHistory SET UndoneAt = @At WHERE RunGuid = @R";
            cmd.Parameters.AddWithValue("@At", Db(at));
            cmd.Parameters.AddWithValue("@R", runId);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            Execute(_connection, """
                DELETE FROM RunCameraStats; DELETE FROM RunExtStats; DELETE FROM RunLensStats;
                DELETE FROM RunHistory; DELETE FROM Operations;
                """);
        }
    }

    public void Dispose() => _connection.Dispose();
}
