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

    /// <summary>保存済みのすべてのファイル情報を返す（ライブラリ表示用）</summary>
    Task<IReadOnlyList<ImageAsset>> LoadAllAsync(CancellationToken cancellationToken = default);

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
        ("Orientation", "INTEGER"),
    ];

    private static readonly bool CaseInsensitivePaths = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private const int LookupBatchSize = 500;

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
        if (CaseInsensitivePaths)
            Execute(_connection, "CREATE INDEX IF NOT EXISTS IX_Assets_SourcePath_NoCase ON Assets(SourcePath COLLATE NOCASE)");
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
            var p = new SqliteParameter[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var prm = cmd.CreateParameter();
                prm.ParameterName = "@" + names[i];
                cmd.Parameters.Add(prm);
                p[i] = prm;
            }
            cmd.Prepare();

            foreach (var a in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var e = a.Exif;
                p[0].Value  = a.SourcePath;
                p[1].Value  = a.FileSize;
                p[2].Value  = Db(a.FileCreatedAt);
                p[3].Value  = Db(a.FileModifiedAt);
                p[4].Value  = Db(a.PartialHash);
                p[5].Value  = Db(a.FullHash);
                p[6].Value  = a.MetadataExtracted ? 1 : 0;
                p[7].Value  = Db(e?.DateTimeOriginal);
                p[8].Value  = Db(e?.CreateDate);
                p[9].Value  = Db(e?.ModifyDate);
                p[10].Value = Db(e?.Make);
                p[11].Value = Db(e?.Model);
                p[12].Value = Db(e?.LensModel);
                p[13].Value = Db(e?.FocalLength);
                p[14].Value = Db(e?.Aperture);
                p[15].Value = Db(e?.ShutterSpeed);
                p[16].Value = Db(e?.ISO);
                p[17].Value = Db(e?.ImageWidth);
                p[18].Value = Db(e?.ImageHeight);
                p[19].Value = Db(e?.Rating);
                p[20].Value = Db(e?.UsedDateField);
                p[21].Value = Db(e?.GPSLatitude);
                p[22].Value = Db(e?.GPSLongitude);
                p[23].Value = Db(e?.GeolocationCity);
                p[24].Value = Db(e?.GeolocationRegion);
                p[25].Value = Db(e?.GeolocationCountry);
                p[26].Value = Db(e?.GeolocationCountryCode);
                p[27].Value = Db(a.NormalizedCamera);
                p[28].Value = Db(a.NormalizedLens);
                p[29].Value = Db(a.OutputPath);
                p[30].Value = a.Status.ToString();
                p[31].Value = Db(a.ErrorMessage);
                p[32].Value = Db(a.ProcessedAt);
                p[33].Value = Db(a.DuplicateGroupId);
                p[34].Value = a.IsKeptInDuplicateGroup ? 1 : 0;
                p[35].Value = Db(e?.Orientation);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, ImageAsset>> LoadByPathsAsync(
        IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var distinct = paths.Distinct(PathUtil.Comparer).ToList();
        var results = new Dictionary<string, ImageAsset>(distinct.Count, PathUtil.Comparer);
        if (distinct.Count == 0) return results;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var collate = CaseInsensitivePaths ? " COLLATE NOCASE" : "";
            for (int offset = 0; offset < distinct.Count; offset += LookupBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(LookupBatchSize, distinct.Count - offset);
                using var cmd = _connection.CreateCommand();
                var placeholders = new string[count];
                for (int i = 0; i < count; i++)
                {
                    var name = "@p" + i;
                    placeholders[i] = name;
                    cmd.Parameters.AddWithValue(name, distinct[offset + i]);
                }
                cmd.CommandText = $"SELECT * FROM Assets WHERE SourcePath{collate} IN ({string.Join(",", placeholders)})";

                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var ord = new Ordinals(reader);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var asset = ReadAsset(reader, in ord);
                    results[asset.SourcePath] = asset;
                }
            }
        }
        finally { _lock.Release(); }
        return results;
    }

    public async Task<IReadOnlyList<ImageAsset>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ImageAsset>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Assets ORDER BY Id";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var ord = new Ordinals(reader);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(ReadAsset(reader, in ord));
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

    private readonly struct Ordinals(SqliteDataReader r)
    {
        public readonly int Id                     = r.GetOrdinal("Id");
        public readonly int SourcePath             = r.GetOrdinal("SourcePath");
        public readonly int FileSize               = r.GetOrdinal("FileSize");
        public readonly int FileCreatedAt          = r.GetOrdinal("FileCreatedAt");
        public readonly int FileModifiedAt         = r.GetOrdinal("FileModifiedAt");
        public readonly int PartialHash            = r.GetOrdinal("PartialHash");
        public readonly int FullHash               = r.GetOrdinal("FullHash");
        public readonly int MetadataExtracted      = r.GetOrdinal("MetadataExtracted");
        public readonly int NormalizedCamera       = r.GetOrdinal("NormalizedCamera");
        public readonly int NormalizedLens         = r.GetOrdinal("NormalizedLens");
        public readonly int OutputPath             = r.GetOrdinal("OutputPath");
        public readonly int Status                 = r.GetOrdinal("Status");
        public readonly int ErrorMessage           = r.GetOrdinal("ErrorMessage");
        public readonly int ProcessedAt            = r.GetOrdinal("ProcessedAt");
        public readonly int DuplicateGroupId       = r.GetOrdinal("DuplicateGroupId");
        public readonly int IsKeptInDuplicateGroup = r.GetOrdinal("IsKeptInDuplicateGroup");
        public readonly int DateTimeOriginal       = r.GetOrdinal("DateTimeOriginal");
        public readonly int CreateDate             = r.GetOrdinal("CreateDate");
        public readonly int ModifyDate             = r.GetOrdinal("ModifyDate");
        public readonly int Make                   = r.GetOrdinal("Make");
        public readonly int Model                  = r.GetOrdinal("Model");
        public readonly int LensModel              = r.GetOrdinal("LensModel");
        public readonly int FocalLength            = r.GetOrdinal("FocalLength");
        public readonly int Aperture               = r.GetOrdinal("Aperture");
        public readonly int ShutterSpeed           = r.GetOrdinal("ShutterSpeed");
        public readonly int ISO                    = r.GetOrdinal("ISO");
        public readonly int ImageWidth             = r.GetOrdinal("ImageWidth");
        public readonly int ImageHeight            = r.GetOrdinal("ImageHeight");
        public readonly int Rating                 = r.GetOrdinal("Rating");
        public readonly int UsedDateField          = r.GetOrdinal("UsedDateField");
        public readonly int GPSLatitude            = r.GetOrdinal("GPSLatitude");
        public readonly int GPSLongitude           = r.GetOrdinal("GPSLongitude");
        public readonly int GeolocationCity        = r.GetOrdinal("GeolocationCity");
        public readonly int GeolocationRegion      = r.GetOrdinal("GeolocationRegion");
        public readonly int GeolocationCountry     = r.GetOrdinal("GeolocationCountry");
        public readonly int GeolocationCountryCode = r.GetOrdinal("GeolocationCountryCode");
        public readonly int Orientation            = r.GetOrdinal("Orientation");
    }

    private static string? Str(SqliteDataReader r, int ord) => r.IsDBNull(ord) ? null : r.GetString(ord);
    private static int? Int(SqliteDataReader r, int ord) => r.IsDBNull(ord) ? null : r.GetInt32(ord);
    private static double? Real(SqliteDataReader r, int ord) => r.IsDBNull(ord) ? null : r.GetDouble(ord);
    private static DateTime? Date(SqliteDataReader r, int ord) => ParseDate(Str(r, ord));

    private static ImageAsset ReadAsset(SqliteDataReader r, in Ordinals o)
    {
        var asset = new ImageAsset
        {
            Id                     = r.GetInt64(o.Id),
            SourcePath             = r.GetString(o.SourcePath),
            FileSize               = r.GetInt64(o.FileSize),
            FileCreatedAt          = Date(r, o.FileCreatedAt) ?? default,
            FileModifiedAt         = Date(r, o.FileModifiedAt) ?? default,
            PartialHash            = Str(r, o.PartialHash),
            FullHash               = Str(r, o.FullHash),
            MetadataExtracted      = Int(r, o.MetadataExtracted) == 1,
            NormalizedCamera       = Str(r, o.NormalizedCamera),
            NormalizedLens         = Str(r, o.NormalizedLens),
            OutputPath             = Str(r, o.OutputPath),
            Status                 = Enum.TryParse<AssetStatus>(Str(r, o.Status), out var s) ? s : AssetStatus.Scanned,
            ErrorMessage           = Str(r, o.ErrorMessage),
            ProcessedAt            = Date(r, o.ProcessedAt),
            DuplicateGroupId       = Str(r, o.DuplicateGroupId),
            IsKeptInDuplicateGroup = Int(r, o.IsKeptInDuplicateGroup) != 0,
        };

        var exif = new ExifData
        {
            DateTimeOriginal       = Date(r, o.DateTimeOriginal),
            CreateDate             = Date(r, o.CreateDate),
            ModifyDate             = Date(r, o.ModifyDate),
            Make                   = Str(r, o.Make),
            Model                  = Str(r, o.Model),
            LensModel              = Str(r, o.LensModel),
            FocalLength            = Str(r, o.FocalLength),
            Aperture               = Str(r, o.Aperture),
            ShutterSpeed           = Str(r, o.ShutterSpeed),
            ISO                    = Str(r, o.ISO),
            ImageWidth             = Int(r, o.ImageWidth),
            ImageHeight            = Int(r, o.ImageHeight),
            Rating                 = Int(r, o.Rating),
            UsedDateField          = Str(r, o.UsedDateField),
            GPSLatitude            = Real(r, o.GPSLatitude),
            GPSLongitude           = Real(r, o.GPSLongitude),
            GeolocationCity        = Str(r, o.GeolocationCity),
            GeolocationRegion      = Str(r, o.GeolocationRegion),
            GeolocationCountry     = Str(r, o.GeolocationCountry),
            GeolocationCountryCode = Str(r, o.GeolocationCountryCode),
            Orientation            = Int(r, o.Orientation),
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
