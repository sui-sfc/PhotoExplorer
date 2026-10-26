# PhotoExplorer

[日本語](README.md) | **English**

A Windows app that takes the photos you dumped off your camera or SD card and **sorts them into a clean folder structure automatically, based on their EXIF data: capture date, camera, location and more**.

> The app's interface is in Japanese. This guide gives each on-screen label in Japanese with an English translation, so you can match them to what you see on screen.

![Run log screen](docs/images/01-log.png)

For example, set 整理する写真 (Photos to organize) to your SD card and 保存先 (Destination) to `D:\Pictures`, run it, and you get:

```
D:\Pictures
├── 2024
│   ├── 07
│   │   ├── Canon_EOS_R5
│   │   │   ├── IMG_1001.JPG
│   │   │   └── IMG_1002.JPG
│   │   └── Sony_A7IV
│   │       ├── DSC1010.ARW
│   │       ├── DSC1010.xmp   ← Lightroom sidecars move with their photo
│   │       └── DSC1010.JPG
│   └── 08
│       └── ...
└── 2023
    └── ...
```

You choose the folder layout with a template such as `{yyyy}/{mm}/{dd}/{camera}`. You can also sort by location (city), lens, or file type (RAW vs JPEG).

---

## Features

- **Finds your photos** — Searches a folder and all its subfolders for JPEG, HEIC and RAW files from all major brands (CR3 / NEF / ARW / RAF / ORF / RW2 / DNG and more). Videos (mp4 / mov) can be included too.
- **Organizes by shooting info** — Reads capture date and time, camera, lens, GPS location and star rating, then copies or moves each file into the folder your template describes. It can rename files as well.
- **Lets you try first** — `DRY_RUN` mode never touches a file. It only shows where each file would go.
- **Finds duplicates** — Detects files with identical content by hash and lists them with thumbnails. Extra copies can go to a holding folder or the Windows Recycle Bin.
- **Lets you undo** — Every copy and move is recorded. One click reverts a run, even an interrupted one or one from days ago.
- **Shows statistics** — Charts by camera, lens, month, location, hour of day and day of week, plus a history of past runs.
- **Safe to re-run** — Photos already in the destination are skipped, so you can point it at the whole SD card every time without creating duplicates.

---

## Getting started

### 1. Download

Open **[Releases (latest)](https://github.com/sui-sfc/PhotoExplorer/releases/latest)**, download **`PhotoExplorer-win-x64.zip`** from "Assets", and extract it anywhere.

- No .NET installation is required.
- If Windows shows "Windows protected your PC" on first launch, click "More info" → "Run anyway".

To build it yourself, see [For developers](#for-developers).

### 2. Get ExifTool

PhotoExplorer uses the free tool [ExifTool](https://exiftool.org/) by Phil Harvey to read photo metadata. It is **not bundled**, so download it yourself:

1. On the exiftool.org home page, download the **Windows Executable** (64-bit zip).
2. The zip contains `exiftool(-k).exe` and an `exiftool_files` folder. Put **both** into the `Resources` folder next to `PhotoExplorer.exe`.

```
PhotoExplorer.exe
Resources\
├── exiftoolの置き場所.txt   (setup instructions, in Japanese)
├── exiftool(-k).exe         ← renaming it to exiftool.exe is fine
└── exiftool_files\
```

If ExifTool isn't found, a yellow banner appears at the top of the app. Its buttons do the work for you: exiftool.org を開く (Open exiftool.org) and Resources フォルダを開く (Open the Resources folder). After adding the files, click 再確認 (Check again). When 詳細オプション (Advanced options) shows "exiftool 13.xx を使用" (using exiftool 13.xx) in green, you're ready.

If ExifTool is already on your PATH, it is used automatically. To use a copy somewhere else, set its location under 詳細オプション (Advanced options).

> Sorting by city (`{city}` etc.) requires ExifTool **12.78 or later**.

### 3. Launch and organize

1. Set **整理する写真** (Photos to organize) to the folder containing your photos. You can also drag and drop a folder from Explorer.
2. Set **保存先** (Destination) to the folder where the organized photos should go.
3. Pick a **フォルダ構成** (Folder layout) from the list or type your own. A preview such as `例: D:\Pictures\2024\07\15\Canon_EOS_R5\IMG_0001.CR3` appears right below it.
4. Set the mode to **DRY_RUN** and click ▶ 実行 (Run), or press F5. No files are changed.
5. Check where everything will go on the ファイル一覧 (File list) tab. If it looks right, switch the mode to **COPY** (keep originals) or **MOVE**, and run again.

![File list](docs/images/02-files.png)

If something goes wrong, click ↶ 直前の実行を元に戻す (Undo last run). Copied files are deleted and moved files return to their original place.

---

## A tour of the screens

### Statistics (統計)

Charts show your camera and lens mix, the hours and weekdays you shoot most, where you shoot, and more. From the run history (実行履歴) below, you can undo any past run individually.

![Statistics](docs/images/03-statistics.png)

### Duplicates (重複)

Files with identical content are grouped together with thumbnails. A green frame marks the file to keep (残す) and an orange frame marks the extra copy (重複). Names that look like copies, such as "(1)" or "- コピー" ("- Copy"), are treated as the extras.

![Duplicates](docs/images/04-duplicates.png)

### Advanced options (詳細オプション)

![Advanced options](docs/images/06-options.png)

| Option | What it does |
|---|---|
| 動画も整理する (Include videos) | Also processes mp4 / mov / mts and similar files |
| XMP サイドカーも一緒に動かす (Move XMP sidecars too) | Keeps `IMG_0001.xmp` or `IMG_0001.CR3.xmp` next to its photo, and renames the sidecar when the photo is renamed |
| コピー後に中身を検証する (Verify after copying) | Re-reads each written file and compares hashes with the original |
| 重複を探す (Find duplicates) | Turn off for a slightly faster run |
| 前回の読み取り結果を再利用 (Reuse previous results) | Unchanged files skip ExifTool, so repeat runs are much faster |
| 撮影日がないファイルをまとめる (Group files with no capture date) | Collects files without a capture date, such as scans, into a folder like `_NoDate`. When off, the file's own timestamp is used |
| 詳細ログ (Verbose log) | Logs what was read from every single file, for when photos aren't sorted as expected |

---

## Folder layout templates

Each `/`-separated part becomes a folder. The file-name template accepts the same variables (the extension is added automatically). On the ヘルプ (Help) tab, clicking a variable appends it to your template.

| Variable | Meaning | Example |
|---|---|---|
| `{yyyy}` / `{yy}` | Capture year (4 / 2 digits) | 2024 / 24 |
| `{mm}` / `{MMM}` | Capture month (2 digits / English abbreviation) | 07 / Jul |
| `{dd}` | Capture day | 15 |
| `{HH}` `{min}` `{ss}` | Capture hour, minute, second | 09 05 42 |
| `{camera}` | Camera name | Canon_EOS_R5 |
| `{make}` | Manufacturer | Canon |
| `{lens}` | Lens name | Canon_RF24-105_F4L |
| `{type}` | Kind (JPEG / RAW / HEIF / VIDEO) | RAW |
| `{ext}` | File extension | cr3 |
| `{rating}` | Star rating (0 if none) | 4 |
| `{city}` `{region}` `{country}` `{countrycode}` | Location (photos with GPS only) | Shibuya / Tokyo / Japan / JP |
| `{name}` | Original file name (without extension) | IMG_0001 |

Common combinations:

| Template | Result |
|---|---|
| `{yyyy}/{mm}/{dd}/{camera}` | 2024\07\15\Canon_EOS_R5\IMG_0001.CR3 |
| `{yyyy}/{yyyy}-{mm}-{dd}` | 2024\2024-07-15\IMG_0001.CR3 |
| `{yyyy}/{mm}/{type}` | 2024\07\RAW\IMG_0001.CR3 |
| `{country}/{city}/{yyyy}-{mm}` | Japan\Shibuya\2024-07\IMG_0001.CR3 |
| File name `{yyyy}{mm}{dd}_{HH}{min}{ss}` | …\20240715_090542.CR3 |

### Custom camera and lens names

Under 詳細オプション (Advanced options), click カメラ名・レンズ名の置き換え設定を開く (Open camera/lens name mappings) to open `mappings.json` in Notepad. On the left, put the original name (a partial match works); on the right, the folder name you want. Save, and the next run uses it.

```json
{
  "Camera": {
    "RICOH GR IIIx": "GR3x",
    "ILCE-7M4": "a7IV"
  },
  "Lens": {
    "FE 24-70mm F2.8 GM II": "2470GM2"
  }
}
```

---

## Safety

- **Nothing is overwritten.** If a different file with the same name exists, a number is added, like `IMG_0001_001.JPG`.
- **No double copies.** A file whose identical content already exists in the destination is skipped.
- **Copies are verified.** Each written file is read back and checked against the original.
- **Moves across drives are careful.** MOVE to another drive runs as copy → verify → delete original.
- **Everything is recorded.** Operations are logged one by one, so even if a run is stopped or the PC crashes, everything done up to that point can be undone.
- **Settings are checked first.** Problems such as the same folder for input and destination, template typos, or an unwritable destination are reported before anything runs. MOVE asks for confirmation.

---

## FAQ

**Camera names show as `Unknown`, or capture dates are wrong**
ExifTool probably wasn't found. Check the ExifTool status under 詳細オプション (Advanced options). If that looks fine, turn on 詳細ログ (Verbose log) and run again: the log shows the Make / Model / date read from each file.

**City names show as `Unknown`**
Either the photo has no GPS data or your ExifTool is older than 12.78.

**HEIC or RAW thumbnails show プレビューなし (No preview)**
Install the "HEIF Image Extensions" and "Raw Image Extension" from the Microsoft Store. Organizing works either way.

**Why are repeat runs faster?**
PhotoExplorer remembers the metadata it read last time and reuses it for files that haven't changed.

**Where are settings and history stored?**
In `%LocalAppData%\PhotoExplorer` (you can open it from the ヘルプ (Help) tab). Put an empty file named `portable.txt` next to `PhotoExplorer.exe` to switch to portable mode: data is then stored in a `Data` folder beside the exe, so you can carry it on a USB stick.

**Does it run on macOS or Linux?**
The user interface is Windows-only (WPF). The organizing engine (`PhotoExplorer.Core`) is cross-platform, and its tests also run on Linux.

### Keyboard shortcuts

| Key | Action |
|---|---|
| F5 | Run |
| Esc | Stop |
| Ctrl + S | Save log |
| Ctrl + E | Export file list as CSV |

---

## For developers

### Requirements

- .NET 8 SDK
- ExifTool (used by some tests and by the screenshot generator)

### Build, test, publish

```powershell
dotnet build PhotoExplorer.sln
dotnet test  PhotoExplorer.Tests/PhotoExplorer.Tests.csproj

# Single-file exe (written to the publish folder)
dotnet publish PhotoExplorer/PhotoExplorer.csproj -c Release -o publish
```

Anything you put in `PhotoExplorer/Resources` (such as ExifTool) is copied to the output's `Resources` folder on build and publish.

### Project layout

```
PhotoExplorer.Core/        Organizing engine (net8.0, OS-independent)
├── Domain/                ImageAsset, ExifData, supported formats, template variables
├── Application/           OrganizeUseCase (overall flow), UndoService, statistics, CSV, settings checks
└── Infrastructure/        File scanning, ExifTool, hashing, copy/move, SQLite, settings files
PhotoExplorer/             WPF app (MVVM)
PhotoExplorer.Tests/       xUnit tests (including integration tests that run the real ExifTool)
```

Processing flow:

```
Scan → read metadata (cache / batched ExifTool) → filter by date
     → normalize names → resolve destinations → find duplicates → copy/move (journaled, verified) → save
```

### Regenerating screenshots

The README images were captured by running the real app on Windows in GitHub Actions. It generates demo landscape photos (with camera, date and GPS metadata), runs DRY_RUN and COPY, and saves each tab as an image.

```powershell
publish\PhotoExplorer.exe --screenshots docs\images
```

Run the "CI" workflow manually (Run workflow) in GitHub Actions to recapture the screenshots on Windows and commit them.

### Publishing a release

Include `[release]` in a commit message, or run the "CI" workflow manually with "release" checked. The workflow builds `PhotoExplorer-win-x64.zip` and publishes it to Releases. The version comes from `Version` in `PhotoExplorer/PhotoExplorer.csproj`.
