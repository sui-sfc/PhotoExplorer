using Microsoft.Data.Sqlite;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Domain.ValueObjects;
using static PhotoExplorer.Infrastructure.SqliteDb;

namespace PhotoExplorer.Infrastructure;

/// <summary>ImageAsset の永続化（前回の EXIF・ハッシュの再利用にも使う）</summary>
public interface IAssetRepository
{
    Task SaveAllAsync(IEnumerable<ImageAsset> assets, CancellationToken cancellationToken = default);

    /// <summary>指定パスの保存済みデータを返す（キーは SourcePath）</summary>
    Task<IReadOnlyDictionary<string, ImageAsset>> LoadByPathsAsync(
        IEnumerable<string> paths, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteAssetRepository : IAssetRepository, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly (string Name, string Type)[] Columns =
    [
        ("SourcePath", "TEXT"), ("FileSize", "INTEGER"), ("FileCreatedAt", "TEXT"), ("FileModifiedAt", "TEXT"),
        ("PartialHash", "TEXT"), ("FullHash", "TEXT"), ("MetadataExtracted", "INTEGER NOT NULL DEFAULT 0"),
        ("DateTimeOriginal", "TEXT"), ("CreateDate", "TEXT"), ("ModifyDate", "TEXT"),
        ("Make", "TEXT"), ("Model", "TEXT"), ("LensModel", "TEXT"),
        ("FocalLength", "TEXT"), ("Aperture", "TEXT"), ("ShutterSpeed", "TEXT"), ("ISO", "TEXT"),
        ("ImageWidth", "INTEGER"), ("ImageHeight", "INTEGER"), ("Rating", "INTEGER"),
        ("UsedDateField", "TEXT"),
        ("GPSLatitude", "REAL"), ("GPSLongitude", "REAL"),
        ("GeolocationCity", "TEXT"), ("GeolocationRegion", "TEXT"),
        ("GeolocationCountry", "TEXT"), ("GeolocationCountryCode", "TEXT"),
        ("NormalizedCamera", "TEXT"), ("NormalizedLens", "TEXT"), ("OutputPath", "TEXT"),
        ("Status", "TEXT NOT NULL DEFAULT 'Scanned'"), ("ErrorMessage", "TEXT"), ("ProcessedAt", "TEXT"),
        ("DuplicateGroupId", "TEXT"), ("IsKeptInDuplicateGroup", "INTEGER NOT NULL DEFAULT 1"),
    ];

    public SqliteAssetRepository(string databasePath)
    {
        _connection = Open(databasePath);
        Execute(_connection, """
            CREATE TABLE IF NOT EXISTS Assets (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                SourcePath TEXT    NOT NULL UNIQUE,
                FileSize   INTEGER NOT NULL
            )
            """);
        AddMissingColumns(_connection, "Assets", Columns.Skip(2));
    }

    public async Task SaveAllAsync(IEnumerable<ImageAsset> assets, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;

            var names = Columns.Select(c => c.Name).ToArray();
            cmd.CommandText = $"""
                INSERT INTO Assets ({string.Join(", ", names)})
                VALUES ({string.Join(", ", names.Select(n => "@" + n))})
                ON CONFLICT(SourcePath) DO UPDATE SET
                {string.Join(",\n", names.Skip(1).Select(n => $"{n} = excluded.{n}"))}
                """;
            // 型は値から推論させる（INTEGER/REAL/TEXT が混在するため）
            var p = names.ToDictionary(n => n, n =>
            {
                var prm = cmd.CreateParameter();
                prm.ParameterName = "@" + n;
                cmd.Parameters.Add(prm);
                return prm;
            });

            foreach (var a in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var e = a.Exif;
                p["SourcePath"].Value             = a.SourcePath;
                p["FileSize"].Value               = a.FileSize;
                p["FileCreatedAt"].Value          = Db(a.FileCreatedAt);
                p["FileModifiedAt"].Value         = Db(a.FileModifiedAt);
                p["PartialHash"].Value            = Db(a.PartialHash);
                p["FullHash"].Value               = Db(a.FullHash);
                p["MetadataExtracted"].Value      = a.MetadataExtracted ? 1 : 0;
                p["DateTimeOriginal"].Value       = Db(e?.DateTimeOriginal);
                p["CreateDate"].Value             = Db(e?.CreateDate);
                p["ModifyDate"].Value             = Db(e?.ModifyDate);
                p["Make"].Value                   = Db(e?.Make);
                p["Model"].Value                  = Db(e?.Model);
                p["LensModel"].Value              = Db(e?.LensModel);
                p["FocalLength"].Value            = Db(e?.FocalLength);
                p["Aperture"].Value               = Db(e?.Aperture);
                p["ShutterSpeed"].Value           = Db(e?.ShutterSpeed);
                p["ISO"].Value                    = Db(e?.ISO);
                p["ImageWidth"].Value             = Db(e?.ImageWidth);
                p["ImageHeight"].Value            = Db(e?.ImageHeight);
                p["Rating"].Value                 = Db(e?.Rating);
                p["UsedDateField"].Value          = Db(e?.UsedDateField);
                p["GPSLatitude"].Value            = Db(e?.GPSLatitude);
                p["GPSLongitude"].Value           = Db(e?.GPSLongitude);
                p["GeolocationCity"].Value        = Db(e?.GeolocationCity);
                p["GeolocationRegion"].Value      = Db(e?.GeolocationRegion);
                p["GeolocationCountry"].Value     = Db(e?.GeolocationCountry);
                p["GeolocationCountryCode"].Value = Db(e?.GeolocationCountryCode);
                p["NormalizedCamera"].Value       = Db(a.NormalizedCamera);
                p["NormalizedLens"].Value         = Db(a.NormalizedLens);
                p["OutputPath"].Value             = Db(a.OutputPath);
                p["Status"].Value                 = a.Status.ToString();
                p["ErrorMessage"].Value           = Db(a.ErrorMessage);
                p["ProcessedAt"].Value            = Db(a.ProcessedAt);
                p["DuplicateGroupId"].Value       = Db(a.DuplicateGroupId);
                p["IsKeptInDuplicateGroup"].Value = a.IsKeptInDuplicateGroup ? 1 : 0;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, ImageAsset>> LoadByPathsAsync(
        IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var wanted = new HashSet<string>(paths, PathUtil.Comparer);
        var results = new Dictionary<string, ImageAsset>(PathUtil.Comparer);
        if (wanted.Count == 0) return results;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Assets";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(reader.GetOrdinal("SourcePath"));
                if (!wanted.Contains(path)) continue;
                results[path] = ReadAsset(reader);
            }
        }
        finally { _lock.Release(); }
        return results;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Execute(_connection, "DELETE FROM Assets"); }
        finally { _lock.Release(); }
    }

    private static ImageAsset ReadAsset(SqliteDataReader r)
    {
        var asset = new ImageAsset
        {
            Id                     = r.GetInt64(r.GetOrdinal("Id")),
            SourcePath             = r.GetString(r.GetOrdinal("SourcePath")),
            FileSize               = r.GetInt64(r.GetOrdinal("FileSize")),
            FileCreatedAt          = Date(r, "FileCreatedAt") ?? default,
            FileModifiedAt         = Date(r, "FileModifiedAt") ?? default,
            PartialHash            = Str(r, "PartialHash"),
            FullHash               = Str(r, "FullHash"),
            MetadataExtracted      = Int(r, "MetadataExtracted") == 1,
            NormalizedCamera       = Str(r, "NormalizedCamera"),
            NormalizedLens         = Str(r, "NormalizedLens"),
            OutputPath             = Str(r, "OutputPath"),
            Status                 = Enum.TryParse<AssetStatus>(Str(r, "Status"), out var s) ? s : AssetStatus.Scanned,
            ErrorMessage           = Str(r, "ErrorMessage"),
            ProcessedAt            = Date(r, "ProcessedAt"),
            DuplicateGroupId       = Str(r, "DuplicateGroupId"),
            IsKeptInDuplicateGroup = Int(r, "IsKeptInDuplicateGroup") != 0,
        };

        var exif = new ExifData
        {
            DateTimeOriginal       = Date(r, "DateTimeOriginal"),
            CreateDate             = Date(r, "CreateDate"),
            ModifyDate             = Date(r, "ModifyDate"),
            Make                   = Str(r, "Make"),
            Model                  = Str(r, "Model"),
            LensModel              = Str(r, "LensModel"),
            FocalLength            = Str(r, "FocalLength"),
            Aperture               = Str(r, "Aperture"),
            ShutterSpeed           = Str(r, "ShutterSpeed"),
            ISO                    = Str(r, "ISO"),
            ImageWidth             = Int(r, "ImageWidth"),
            ImageHeight            = Int(r, "ImageHeight"),
            Rating                 = Int(r, "Rating"),
            UsedDateField          = Str(r, "UsedDateField"),
            GPSLatitude            = Real(r, "GPSLatitude"),
            GPSLongitude           = Real(r, "GPSLongitude"),
            GeolocationCity        = Str(r, "GeolocationCity"),
            GeolocationRegion      = Str(r, "GeolocationRegion"),
            GeolocationCountry     = Str(r, "GeolocationCountry"),
            GeolocationCountryCode = Str(r, "GeolocationCountryCode"),
        };
        asset.Exif = exif.IsEmpty ? null : exif;
        return asset;
    }

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
