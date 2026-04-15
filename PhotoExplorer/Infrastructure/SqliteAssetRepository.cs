using Microsoft.Data.Sqlite;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.ValueObjects;

namespace PhotoExplorer.Infrastructure;

/// <summary>SQLiteを使用したアセット永続化実装</summary>
public sealed class SqliteAssetRepository : IAssetRepository, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteAssetRepository(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        InitializeSchema();
        MigrateSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Assets (
                Id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                SourcePath             TEXT    NOT NULL UNIQUE,
                FileSize               INTEGER NOT NULL,
                FileCreatedAt          TEXT,
                PartialHash            TEXT,
                FullHash               TEXT,
                DateTimeOriginal       TEXT,
                CreateDate             TEXT,
                ModifyDate             TEXT,
                Make                   TEXT,
                Model                  TEXT,
                LensModel              TEXT,
                FocalLength            TEXT,
                Aperture               TEXT,
                ShutterSpeed           TEXT,
                ISO                    TEXT,
                UsedDateField          TEXT,
                GPSLatitude            REAL,
                GPSLongitude           REAL,
                GeolocationCity        TEXT,
                GeolocationRegion      TEXT,
                GeolocationCountry     TEXT,
                GeolocationCountryCode TEXT,
                NormalizedCamera       TEXT,
                NormalizedLens         TEXT,
                OutputPath             TEXT,
                Status                 TEXT    NOT NULL DEFAULT 'Scanned',
                ErrorMessage           TEXT,
                ProcessedAt            TEXT,
                DuplicateGroupId       TEXT,
                IsKeptInDuplicateGroup INTEGER NOT NULL DEFAULT 1
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>既存DBに新カラムを追加するマイグレーション</summary>
    private void MigrateSchema()
    {
        var newColumns = new[]
        {
            ("GPSLatitude",            "REAL"),
            ("GPSLongitude",           "REAL"),
            ("GeolocationCity",        "TEXT"),
            ("GeolocationRegion",      "TEXT"),
            ("GeolocationCountry",     "TEXT"),
            ("GeolocationCountryCode", "TEXT"),
        };

        // PRAGMA table_info で既存カラムを取得
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Assets)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(1)); // column name
        }

        foreach (var (col, type) in newColumns)
        {
            if (existing.Contains(col)) continue;
            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE Assets ADD COLUMN {col} {type}";
            alter.ExecuteNonQuery();
        }
    }

    public async Task SaveAllAsync(
        IEnumerable<ImageAsset> assets,
        CancellationToken cancellationToken = default)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO Assets
                    (SourcePath, FileSize, FileCreatedAt, PartialHash, FullHash,
                     DateTimeOriginal, CreateDate, ModifyDate, Make, Model, LensModel,
                     FocalLength, Aperture, ShutterSpeed, ISO, UsedDateField,
                     GPSLatitude, GPSLongitude,
                     GeolocationCity, GeolocationRegion, GeolocationCountry, GeolocationCountryCode,
                     NormalizedCamera, NormalizedLens, OutputPath, Status,
                     ErrorMessage, ProcessedAt, DuplicateGroupId, IsKeptInDuplicateGroup)
                VALUES
                    (@SourcePath, @FileSize, @FileCreatedAt, @PartialHash, @FullHash,
                     @DateTimeOriginal, @CreateDate, @ModifyDate, @Make, @Model, @LensModel,
                     @FocalLength, @Aperture, @ShutterSpeed, @ISO, @UsedDateField,
                     @GPSLatitude, @GPSLongitude,
                     @GeolocationCity, @GeolocationRegion, @GeolocationCountry, @GeolocationCountryCode,
                     @NormalizedCamera, @NormalizedLens, @OutputPath, @Status,
                     @ErrorMessage, @ProcessedAt, @DuplicateGroupId, @IsKeptInDuplicateGroup)
                ON CONFLICT(SourcePath) DO UPDATE SET
                    FileSize = excluded.FileSize,
                    FileCreatedAt = excluded.FileCreatedAt,
                    PartialHash = excluded.PartialHash,
                    FullHash = excluded.FullHash,
                    DateTimeOriginal = excluded.DateTimeOriginal,
                    CreateDate = excluded.CreateDate,
                    ModifyDate = excluded.ModifyDate,
                    Make = excluded.Make,
                    Model = excluded.Model,
                    LensModel = excluded.LensModel,
                    FocalLength = excluded.FocalLength,
                    Aperture = excluded.Aperture,
                    ShutterSpeed = excluded.ShutterSpeed,
                    ISO = excluded.ISO,
                    UsedDateField = excluded.UsedDateField,
                    GPSLatitude = excluded.GPSLatitude,
                    GPSLongitude = excluded.GPSLongitude,
                    GeolocationCity = excluded.GeolocationCity,
                    GeolocationRegion = excluded.GeolocationRegion,
                    GeolocationCountry = excluded.GeolocationCountry,
                    GeolocationCountryCode = excluded.GeolocationCountryCode,
                    NormalizedCamera = excluded.NormalizedCamera,
                    NormalizedLens = excluded.NormalizedLens,
                    OutputPath = excluded.OutputPath,
                    Status = excluded.Status,
                    ErrorMessage = excluded.ErrorMessage,
                    ProcessedAt = excluded.ProcessedAt,
                    DuplicateGroupId = excluded.DuplicateGroupId,
                    IsKeptInDuplicateGroup = excluded.IsKeptInDuplicateGroup
                """;

            AddParameters(cmd, asset);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<ImageAsset>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<ImageAsset>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Assets ORDER BY Id";
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadAsset(reader));
        return results;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Assets";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(SqliteCommand cmd, ImageAsset asset)
    {
        cmd.Parameters.AddWithValue("@SourcePath",    asset.SourcePath);
        cmd.Parameters.AddWithValue("@FileSize",      asset.FileSize);
        cmd.Parameters.AddWithValue("@FileCreatedAt", asset.FileCreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@PartialHash",   (object?)asset.PartialHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FullHash",      (object?)asset.FullHash    ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@DateTimeOriginal", asset.Exif?.DateTimeOriginal?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@CreateDate",       asset.Exif?.CreateDate?.ToString("o")       ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ModifyDate",       asset.Exif?.ModifyDate?.ToString("o")       ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Make",             asset.Exif?.Make        ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Model",            asset.Exif?.Model       ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@LensModel",        asset.Exif?.LensModel   ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FocalLength",      asset.Exif?.FocalLength ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Aperture",         asset.Exif?.Aperture    ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ShutterSpeed",     asset.Exif?.ShutterSpeed ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ISO",              asset.Exif?.ISO         ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UsedDateField",    asset.Exif?.UsedDateField ?? (object)DBNull.Value);

        cmd.Parameters.AddWithValue("@GPSLatitude",            (object?)asset.Exif?.GPSLatitude            ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@GPSLongitude",           (object?)asset.Exif?.GPSLongitude           ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@GeolocationCity",        asset.Exif?.GeolocationCity        ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@GeolocationRegion",      asset.Exif?.GeolocationRegion      ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@GeolocationCountry",     asset.Exif?.GeolocationCountry     ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@GeolocationCountryCode", asset.Exif?.GeolocationCountryCode ?? (object)DBNull.Value);

        cmd.Parameters.AddWithValue("@NormalizedCamera", (object?)asset.NormalizedCamera ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NormalizedLens",   (object?)asset.NormalizedLens   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OutputPath",       (object?)asset.OutputPath       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status",           asset.Status.ToString());
        cmd.Parameters.AddWithValue("@ErrorMessage",     (object?)asset.ErrorMessage     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ProcessedAt",      asset.ProcessedAt?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@DuplicateGroupId", (object?)asset.DuplicateGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsKeptInDuplicateGroup", asset.IsKeptInDuplicateGroup ? 1 : 0);
    }

    private static ImageAsset ReadAsset(SqliteDataReader r)
    {
        var asset = new ImageAsset
        {
            Id             = r.GetInt64(r.GetOrdinal("Id")),
            SourcePath     = r.GetString(r.GetOrdinal("SourcePath")),
            FileSize       = r.GetInt64(r.GetOrdinal("FileSize")),
            FileCreatedAt  = DateTime.Parse(r.GetString(r.GetOrdinal("FileCreatedAt"))),
            PartialHash    = Str(r, "PartialHash"),
            FullHash       = Str(r, "FullHash"),
            NormalizedCamera = Str(r, "NormalizedCamera"),
            NormalizedLens   = Str(r, "NormalizedLens"),
            OutputPath       = Str(r, "OutputPath"),
            Status           = Enum.Parse<AssetStatus>(r.GetString(r.GetOrdinal("Status"))),
            ErrorMessage     = Str(r, "ErrorMessage"),
            ProcessedAt      = Str(r, "ProcessedAt") is { } pa ? DateTime.Parse(pa) : null,
            DuplicateGroupId = Str(r, "DuplicateGroupId"),
            IsKeptInDuplicateGroup = r.GetInt32(r.GetOrdinal("IsKeptInDuplicateGroup")) == 1,
        };

        if (Str(r, "Make") != null || Str(r, "Model") != null
            || Str(r, "GeolocationCity") != null || RealOrNull(r, "GPSLatitude") != null)
        {
            asset.Exif = new ExifData
            {
                DateTimeOriginal = Str(r, "DateTimeOriginal") is { } d1 ? DateTime.Parse(d1) : null,
                CreateDate       = Str(r, "CreateDate")       is { } d2 ? DateTime.Parse(d2) : null,
                ModifyDate       = Str(r, "ModifyDate")       is { } d3 ? DateTime.Parse(d3) : null,
                Make             = Str(r, "Make"),
                Model            = Str(r, "Model"),
                LensModel        = Str(r, "LensModel"),
                FocalLength      = Str(r, "FocalLength"),
                Aperture         = Str(r, "Aperture"),
                ShutterSpeed     = Str(r, "ShutterSpeed"),
                ISO              = Str(r, "ISO"),
                UsedDateField    = Str(r, "UsedDateField"),
                GPSLatitude      = RealOrNull(r, "GPSLatitude"),
                GPSLongitude     = RealOrNull(r, "GPSLongitude"),
                GeolocationCity        = Str(r, "GeolocationCity"),
                GeolocationRegion      = Str(r, "GeolocationRegion"),
                GeolocationCountry     = Str(r, "GeolocationCountry"),
                GeolocationCountryCode = Str(r, "GeolocationCountryCode"),
            };
        }

        return asset;
    }

    private static string? Str(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static double? RealOrNull(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetDouble(ord);
    }

    public void Dispose() => _connection.Dispose();
}
