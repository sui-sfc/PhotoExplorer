using System.Globalization;
using System.Text;
using PhotoExplorer.Domain.Entities;

namespace PhotoExplorer.Application;

/// <summary>結果を Excel で開ける CSV（UTF-8 BOM 付き）に書き出す</summary>
public static class CsvExporter
{
    private static readonly string[] Header =
    [
        "状態", "元のパス", "出力先", "サイズ(バイト)", "撮影日時", "日時の取得元",
        "カメラ", "レンズ", "幅", "高さ", "レーティング", "緯度", "経度", "国", "都市",
        "重複グループ", "残すファイル", "メッセージ",
    ];

    public static void Write(string path, IEnumerable<ImageAsset> assets, Func<ImageAsset, string> statusText)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Write(writer, assets, statusText);
    }

    public static void Write(TextWriter writer, IEnumerable<ImageAsset> assets, Func<ImageAsset, string> statusText)
    {
        var inv = CultureInfo.InvariantCulture;
        writer.WriteLine(string.Join(",", Header.Select(Escape)));
        foreach (var a in assets)
        {
            var e = a.Exif;
            string?[] row =
            [
                statusText(a),
                a.SourcePath,
                a.OutputPath,
                a.FileSize.ToString(inv),
                a.BestDate.ToString("yyyy-MM-dd HH:mm:ss", inv),
                a.HasExifDate ? e!.UsedDateField : "ファイル日時",
                a.NormalizedCamera,
                a.NormalizedLens,
                e?.ImageWidth?.ToString(inv),
                e?.ImageHeight?.ToString(inv),
                e?.Rating?.ToString(inv),
                e?.GPSLatitude?.ToString("F6", inv),
                e?.GPSLongitude?.ToString("F6", inv),
                e?.GeolocationCountry,
                e?.GeolocationCity,
                a.DuplicateGroupId,
                a.DuplicateGroupId == null ? null : (a.IsKeptInDuplicateGroup ? "はい" : "いいえ"),
                a.ErrorMessage,
            ];
            writer.WriteLine(string.Join(",", row.Select(Escape)));
        }
    }

    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Excel で数式として解釈されないようにする
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
