using Microsoft.Data.Sqlite;
using PhotoExplorer.Application;

namespace PhotoExplorer.Infrastructure;

/// <summary>実行履歴を SQLite に保存する</summary>
public sealed class StatsHistoryRepository : IDisposable
{
    private readonly SqliteConnection _connection;

    public StatsHistoryRepository(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
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
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveRun(
        string inputDirectory,
        ScanStatistics stats,
        int processed, int duplicates, int errors)
    {
        using var tx = _connection.BeginTransaction();

        long runId;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO RunHistory
                    (RunAt, InputDirectory, TotalFiles, JpegCount, RawCount,
                     WithExif, WithGps, Filtered, Processed, Duplicates, Errors,
                     EarliestShot, LatestShot)
                VALUES
                    (@RunAt, @Input, @Total, @Jpeg, @Raw,
                     @WithExif, @WithGps, @Filtered, @Processed, @Duplicates, @Errors,
                     @Earliest, @Latest)
                """;
            cmd.Parameters.AddWithValue("@RunAt",      DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("@Input",      inputDirectory);
            cmd.Parameters.AddWithValue("@Total",      stats.TotalFiles);
            cmd.Parameters.AddWithValue("@Jpeg",       stats.JpegCount);
            cmd.Parameters.AddWithValue("@Raw",        stats.RawCount);
            cmd.Parameters.AddWithValue("@WithExif",   stats.WithExif);
            cmd.Parameters.AddWithValue("@WithGps",    stats.WithGps);
            cmd.Parameters.AddWithValue("@Filtered",   stats.Filtered);
            cmd.Parameters.AddWithValue("@Processed",  processed);
            cmd.Parameters.AddWithValue("@Duplicates", duplicates);
            cmd.Parameters.AddWithValue("@Errors",     errors);
            cmd.Parameters.AddWithValue("@Earliest",   stats.EarliestShot?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Latest",     stats.LatestShot?.ToString("o")   ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
            runId = _connection.LastInsertRowId;
        }

        foreach (var (camera, count) in stats.CameraBreakdown)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO RunCameraStats (RunId, CameraName, Count) VALUES (@R,@C,@N)";
            cmd.Parameters.AddWithValue("@R", runId);
            cmd.Parameters.AddWithValue("@C", camera);
            cmd.Parameters.AddWithValue("@N", count);
            cmd.ExecuteNonQuery();
        }

        foreach (var (ext, count) in stats.ExtBreakdown)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO RunExtStats (RunId, Ext, Count) VALUES (@R,@E,@N)";
            cmd.Parameters.AddWithValue("@R", runId);
            cmd.Parameters.AddWithValue("@E", ext);
            cmd.Parameters.AddWithValue("@N", count);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<RunHistoryRecord> LoadHistory(int limit = 50)
    {
        var results = new List<RunHistoryRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM RunHistory ORDER BY Id DESC LIMIT @L";
        cmd.Parameters.AddWithValue("@L", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new RunHistoryRecord(
                Id:            r.GetInt64(r.GetOrdinal("Id")),
                RunAt:         DateTime.Parse(r.GetString(r.GetOrdinal("RunAt"))),
                InputDirectory: r.IsDBNull(r.GetOrdinal("InputDirectory")) ? "" : r.GetString(r.GetOrdinal("InputDirectory")),
                TotalFiles:    r.GetInt32(r.GetOrdinal("TotalFiles")),
                JpegCount:     r.GetInt32(r.GetOrdinal("JpegCount")),
                RawCount:      r.GetInt32(r.GetOrdinal("RawCount")),
                WithGps:       r.GetInt32(r.GetOrdinal("WithGps")),
                Processed:     r.GetInt32(r.GetOrdinal("Processed")),
                Duplicates:    r.GetInt32(r.GetOrdinal("Duplicates")),
                Errors:        r.GetInt32(r.GetOrdinal("Errors"))
            ));
        }
        return results;
    }

    public void Dispose() => _connection.Dispose();
}

public sealed record RunHistoryRecord(
    long Id,
    DateTime RunAt,
    string InputDirectory,
    int TotalFiles,
    int JpegCount,
    int RawCount,
    int WithGps,
    int Processed,
    int Duplicates,
    int Errors);
