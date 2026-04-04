# PhotoExplorer - 写真整理ツール

EXIF情報をもとに写真（JPEG/RAW）を体系的なフォルダ構造へ整理するデスクトップアプリケーションです。

## 機能

- **ファイルスキャン**: JPEG・RAW ファイルを再帰的に収集（隠しファイル・シンボリックリンク除外）
- **EXIFメタデータ抽出**: exiftool を使って DateTimeOriginal / CreateDate / ModifyDate / カメラ / レンズ情報を取得
- **フォルダ構造生成**: テンプレート `{yyyy}/{mm}/{dd}/{camera}/{lens}` で自由に指定
- **重複検出**: サイズ → 部分ハッシュ（64KB） → フルSHA-256 の3段階で高速・確実に検出
- **ファイル操作**:
  - `DRY_RUN`: 変更なしで結果だけ確認（必ず最初にこれで確認）
  - `COPY`: コピー（元ファイル保持）
  - `MOVE`: 移動（元ファイル削除）
- **論理削除**: 重複ファイルをゴミ箱フォルダへ移動（物理削除なし）
- **状態管理**: SQLite で処理状態を保存（中断・再実行可能）
- **並列処理**: 最大並列数を指定して高速処理

## 必要環境

- Windows 11
- [exiftool](https://exiftool.org/) （`Resources/exiftool.exe` に配置、または PATH に追加）
- .NET 8 Runtime（Single-file 発行時は不要）

## セットアップ

### ビルド

```powershell
dotnet build PhotoExplorer.sln
```

### 発行（単体 EXE）

```powershell
dotnet publish PhotoExplorer/PhotoExplorer.csproj -c Release
```

### exiftool の配置

[https://exiftool.org/](https://exiftool.org/) から Windows 版 `exiftool(-k).exe` をダウンロードし、
`exiftool.exe` にリネームして以下に配置します：

```
PhotoExplorer/Resources/exiftool.exe
```

## 使い方

1. **入力フォルダ** — 整理したい写真が入っているフォルダを指定
2. **出力フォルダ** — 整理後のファイルを置くフォルダを指定
3. **テンプレート** — フォルダ構造のテンプレートを指定（例: `{yyyy}/{mm}/{dd}/{camera}`）
4. **モード** — 最初は `DRY_RUN` で実行してログを確認
5. **並列数** — 処理並列数（デフォルト 4）
6. **▶ 実行** ボタンで開始

### テンプレート変数

| 変数 | 内容 |
|------|------|
| `{yyyy}` | 撮影年（4桁） |
| `{mm}` | 撮影月（2桁） |
| `{dd}` | 撮影日（2桁） |
| `{camera}` | カメラ名（正規化済み） |
| `{lens}` | レンズ名（正規化済み） |

## アーキテクチャ

```
PhotoExplorer/
├── Domain/          # エンティティ・値オブジェクト・サービスインターフェース
│   ├── Entities/    # ImageAsset, AppSettings
│   ├── Enums/       # AssetStatus, OperationMode
│   ├── ValueObjects/ # ExifData
│   └── Services/    # IPathResolver, IDuplicateDetector, INormalizer
├── Application/     # ユースケース（OrganizeUseCase）
├── Infrastructure/  # 実装（FileScanner, ExifTool, SQLite, FileOperator...）
└── Presentation/    # WPF/MVVM（MainViewModel, MainWindow）
```

処理フロー: `Scan → EXIF → Normalize → Resolve → Duplicate → Execute → Log`
