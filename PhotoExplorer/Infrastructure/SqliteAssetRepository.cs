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
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Assets (
                Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                SourcePath         TEXT    NOT NULL UNIQUE,
                FileSize           INTEGER NOT NULL,
                FileCreatedAt      TEXT,
                PartialHash        TEXT,
                FullHash           TEXT,
                DateTimeOriginal   TEXT,
                CreateDate         TEXT,
                ModifyDate         TEXT,
                Make               TEXT,
                Model              TEXT,
                LensModel          TEXT,
                FocalLength        TEXT,
                Aperture           TEXT,
                ShutterSpeed       TEXT,
                ISO                TEXT,
                UsedDateField      TEXT,
                NormalizedCamera   TEXT,
                NormalizedLens     TEXT,
                OutputPath         TEXT,
                Status             TEXT    NOT NULL DEFAULT 'Scanned',
                ErrorMessage       TEXT,
                ProcessedAt        TEXT,
                DuplicateGroupId   TEXT,
                IsKeptInDuplicateGroup INTEGER NOT NULL DEFAULT 1
            )
            """;
        cmd.ExecuteNonQuery();
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
                     NormalizedCamera, NormalizedLens, OutputPath, Status,
                     ErrorMessage, ProcessedAt, DuplicateGroupId, IsKeptInDuplicateGroup)
                VALUES
                    (@SourcePath, @FileSize, @FileCreatedAt, @PartialHash, @FullHash,
                     @DateTimeOriginal, @CreateDate, @ModifyDate, @Make, @Model, @LensModel,
                     @FocalLength, @Aperture, @ShutterSpeed, @ISO, @UsedDateField,
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
        cmd.Parameters.AddWithValue("@SourcePath", asset.SourcePath);
        cmd.Parameters.AddWithValue("@FileSize", asset.FileSize);
        cmd.Parameters.AddWithValue("@FileCreatedAt", asset.FileCreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@PartialHash", (object?)asset.PartialHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FullHash", (object?)asset.FullHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DateTimeOriginal", asset.Exif?.DateTimeOriginal?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@CreateDate", asset.Exif?.CreateDate?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ModifyDate", asset.Exif?.ModifyDate?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Make", asset.Exif?.Make ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Model", asset.Exif?.Model ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@LensModel", asset.Exif?.LensModel ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FocalLength", asset.Exif?.FocalLength ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Aperture", asset.Exif?.Aperture ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ShutterSpeed", asset.Exif?.ShutterSpeed ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ISO", asset.Exif?.ISO ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UsedDateField", asset.Exif?.UsedDateField ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@NormalizedCamera", (object?)asset.NormalizedCamera ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NormalizedLens", (object?)asset.NormalizedLens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OutputPath", (object?)asset.OutputPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", asset.Status.ToString());
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)asset.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ProcessedAt", asset.ProcessedAt?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@DuplicateGroupId", (object?)asset.DuplicateGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsKeptInDuplicateGroup", asset.IsKeptInDuplicateGroup ? 1 : 0);
    }

    private static ImageAsset ReadAsset(SqliteDataReader r)
    {
        var asset = new ImageAsset
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            SourcePath = r.GetString(r.GetOrdinal("SourcePath")),
            FileSize = r.GetInt64(r.GetOrdinal("FileSize")),
            FileCreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("FileCreatedAt"))),
            PartialHash = r.IsDBNull(r.GetOrdinal("PartialHash")) ? null : r.GetString(r.GetOrdinal("PartialHash")),
            FullHash = r.IsDBNull(r.GetOrdinal("FullHash")) ? null : r.GetString(r.GetOrdinal("FullHash")),
            NormalizedCamera = r.IsDBNull(r.GetOrdinal("NormalizedCamera")) ? null : r.GetString(r.GetOrdinal("NormalizedCamera")),
            NormalizedLens = r.IsDBNull(r.GetOrdinal("NormalizedLens")) ? null : r.GetString(r.GetOrdinal("NormalizedLens")),
            OutputPath = r.IsDBNull(r.GetOrdinal("OutputPath")) ? null : r.GetString(r.GetOrdinal("OutputPath")),
            Status = Enum.Parse<AssetStatus>(r.GetString(r.GetOrdinal("Status"))),
            ErrorMessage = r.IsDBNull(r.GetOrdinal("ErrorMessage")) ? null : r.GetString(r.GetOrdinal("ErrorMessage")),
            ProcessedAt = r.IsDBNull(r.GetOrdinal("ProcessedAt")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("ProcessedAt"))),
            DuplicateGroupId = r.IsDBNull(r.GetOrdinal("DuplicateGroupId")) ? null : r.GetString(r.GetOrdinal("DuplicateGroupId")),
            IsKeptInDuplicateGroup = r.GetInt32(r.GetOrdinal("IsKeptInDuplicateGroup")) == 1,
        };

        // Exif の復元
        if (!r.IsDBNull(r.GetOrdinal("Make")) || !r.IsDBNull(r.GetOrdinal("Model")))
        {
            asset.Exif = new ExifData
            {
                DateTimeOriginal = r.IsDBNull(r.GetOrdinal("DateTimeOriginal")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("DateTimeOriginal"))),
                CreateDate = r.IsDBNull(r.GetOrdinal("CreateDate")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("CreateDate"))),
                ModifyDate = r.IsDBNull(r.GetOrdinal("ModifyDate")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("ModifyDate"))),
                Make = r.IsDBNull(r.GetOrdinal("Make")) ? null : r.GetString(r.GetOrdinal("Make")),
                Model = r.IsDBNull(r.GetOrdinal("Model")) ? null : r.GetString(r.GetOrdinal("Model")),
                LensModel = r.IsDBNull(r.GetOrdinal("LensModel")) ? null : r.GetString(r.GetOrdinal("LensModel")),
                FocalLength = r.IsDBNull(r.GetOrdinal("FocalLength")) ? null : r.GetString(r.GetOrdinal("FocalLength")),
                Aperture = r.IsDBNull(r.GetOrdinal("Aperture")) ? null : r.GetString(r.GetOrdinal("Aperture")),
                ShutterSpeed = r.IsDBNull(r.GetOrdinal("ShutterSpeed")) ? null : r.GetString(r.GetOrdinal("ShutterSpeed")),
                ISO = r.IsDBNull(r.GetOrdinal("ISO")) ? null : r.GetString(r.GetOrdinal("ISO")),
                UsedDateField = r.IsDBNull(r.GetOrdinal("UsedDateField")) ? null : r.GetString(r.GetOrdinal("UsedDateField")),
            };
        }

        return asset;
    }

    public void Dispose() => _connection.Dispose();
}
