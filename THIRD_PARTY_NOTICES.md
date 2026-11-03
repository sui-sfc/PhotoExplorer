# Third-party notices / 使用しているソフトウェア

PhotoExplorer itself is released under the [MIT License](LICENSE) (Copyright (c) 2026 sui-sfc).
It uses the following open-source components. Their licenses apply to those components.

PhotoExplorer 本体は [MIT License](LICENSE) です。以下のオープンソースソフトウェアを使用しており、それぞれのライセンスが適用されます。

## Bundled in the release (配布物に含まれるもの)

| Component | License | Copyright |
|---|---|---|
| [.NET Runtime / WPF](https://github.com/dotnet) | MIT | .NET Foundation and Contributors |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | MIT | .NET Foundation and Contributors |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT | .NET Foundation and Contributors |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | Apache License 2.0 | Copyright 2014-2024 SourceGear, LLC |
| [SQLite](https://www.sqlite.org/) | Public Domain | — |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) | MIT | Copyright (c) 2007 James Newton-King |
| [WPF UI](https://github.com/lepoco/wpfui) | MIT | Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors |

The full license texts are available at each project's repository linked above.
各ライセンスの全文は、上のリンク先の各リポジトリで確認できます。

## Not bundled (同梱していないもの)

[ExifTool](https://exiftool.org/) by Phil Harvey is required to read photo metadata but is **not** distributed with PhotoExplorer.
Users download it separately. ExifTool is free software distributed under the same terms as Perl itself (GPL or Artistic License).

撮影情報の読み取りに使う ExifTool（作者: Phil Harvey）は PhotoExplorer には同梱していません。各自で exiftool.org から入手してください。
