namespace PhotoExplorer.Domain;

public sealed record TemplateVariable(string Token, string Description, string Example);

/// <summary>フォルダ名・ファイル名テンプレートで使える変数の一覧</summary>
public static class TemplateVariables
{
    public static readonly IReadOnlyList<TemplateVariable> All =
    [
        new("{yyyy}",        "撮影年（4桁）",                    "2024"),
        new("{yy}",          "撮影年（2桁）",                    "24"),
        new("{mm}",          "撮影月（2桁）",                    "07"),
        new("{MMM}",         "撮影月（英語の略称）",             "Jul"),
        new("{dd}",          "撮影日（2桁）",                    "15"),
        new("{HH}",          "撮影時（24時間・2桁）",            "09"),
        new("{min}",         "撮影分（2桁）",                    "05"),
        new("{ss}",          "撮影秒（2桁）",                    "42"),
        new("{camera}",      "カメラ名（マッピング適用後）",     "Canon_EOS_R5"),
        new("{make}",        "メーカー名",                       "Canon"),
        new("{lens}",        "レンズ名（マッピング適用後）",     "RF24-105_F4L"),
        new("{type}",        "種別（JPEG / RAW / HEIF / VIDEO）", "RAW"),
        new("{ext}",         "拡張子（小文字）",                 "cr3"),
        new("{rating}",      "レーティング（★の数。なければ 0）", "3"),
        new("{city}",        "都市名（GPS 付き写真のみ）",       "Shibuya"),
        new("{region}",      "都道府県・州",                     "Tokyo"),
        new("{country}",     "国名",                             "Japan"),
        new("{countrycode}", "国コード",                         "JP"),
        new("{name}",        "元のファイル名（拡張子なし）",     "IMG_0001"),
    ];

    public static readonly IReadOnlyList<string> FolderPresets =
    [
        "{yyyy}/{mm}/{dd}/{camera}",
        "{yyyy}/{yyyy}-{mm}-{dd}",
        "{yyyy}/{mm}/{type}",
        "{yyyy}/{mm}/{dd}/{type}/{camera}",
        "{camera}/{yyyy}/{mm}",
        "{country}/{city}/{yyyy}-{mm}",
        "{yyyy}/{rating}star",
    ];

    public static readonly IReadOnlyList<string> FileNamePresets =
    [
        "",
        "{yyyy}{mm}{dd}_{HH}{min}{ss}",
        "{yyyy}-{mm}-{dd}_{name}",
        "{yyyy}{mm}{dd}_{HH}{min}{ss}_{camera}",
    ];
}
