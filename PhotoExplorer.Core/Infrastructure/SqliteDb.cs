using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PhotoExplorer.Infrastructure;

internal static class SqliteDb
{
    public static SqliteConnection Open(string databasePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        conn.Open();
        Execute(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        return conn;
    }

    public static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>不足しているカラムだけを追加する（既存データは保持）</summary>
    public static void AddMissingColumns(SqliteConnection conn, string table, IEnumerable<(string Name, string Type)> columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(1));
        }
        foreach (var (name, type) in columns)
        {
            if (existing.Contains(name)) continue;
            Execute(conn, $"ALTER TABLE {table} ADD COLUMN {name} {type}");
        }
    }

    public static object Db(object? value) => value ?? DBNull.Value;
    public static object Db(DateTime? value) => value?.ToString("o", CultureInfo.InvariantCulture) ?? (object)DBNull.Value;

    public static string? Str(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    public static double? Real(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetDouble(ord);
    }

    public static int? Int(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetInt32(ord);
    }

    public static DateTime? Date(SqliteDataReader r, string col) => ParseDate(Str(r, col));

    public static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
