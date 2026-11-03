using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using PhotoExplorer.Application;
using PhotoExplorer.Domain;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Presentation.Converters;
using PhotoExplorer.Presentation.Services;

namespace PhotoExplorer.Presentation.ViewModels;

/// <summary>絞り込みの選択肢（Value が null なら「すべて」）</summary>
public sealed record FilterOption(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>「ライブラリ」タブ: 整理の記録から写真を探して並べる</summary>
public sealed class LibraryViewModel : ViewModelBase
{
    private readonly LibraryService _service;
    private IReadOnlyList<LibraryPhoto> _all = [];
    private IReadOnlyList<LibraryItemViewModel> _filtered = [];
    private bool _loaded, _isLoading, _suppressApply;
    private int _columns = 5;
    private double _thumbnailSize = 180;
    private string _status = "";
    private LibraryItemViewModel? _selected;
    private ImageSource? _preview;
    private int _previewRequest;

    public LibraryViewModel(LibraryService service)
    {
        _service = service;
        ReloadCommand        = new AsyncRelayCommand(ReloadAsync, () => !IsLoading);
        ClearFiltersCommand  = new RelayCommand(ClearFilters);
        SelectCommand        = new RelayCommand(p => Selected = p as LibraryItemViewModel);
        OpenCommand          = new RelayCommand(p => { if ((p as LibraryItemViewModel ?? Selected) is { Exists: true } i) Shell.OpenWithDefaultApp(i.Path); });
        ShowInExplorerCommand = new RelayCommand(p => { if ((p as LibraryItemViewModel ?? Selected) is { } i) Shell.ShowInExplorer(i.Path); });
        ClearCacheCommand    = new RelayCommand(ClearCache);
    }

    // ===================== 読み込み =====================

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public bool HasPhotos => _all.Count > 0;

    /// <summary>初めてタブを開いたときに読み込む</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : ReloadAsync();

    /// <summary>整理を実行した後など。まだ一度も開いていなければ何もしない</summary>
    public Task ReloadIfLoadedAsync() => _loaded ? ReloadAsync() : Task.CompletedTask;

    public async Task ReloadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Status = "写真を読み込んでいます...";
        try
        {
            _all = await _service.LoadAsync();
            _loaded = true;
            BuildFacets();
            Apply();
        }
        catch (Exception ex)
        {
            Status = $"読み込めませんでした: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasPhotos));
        }
    }

    // ===================== 絞り込み =====================

    private FilterOption? _camera, _lens, _location, _kind, _rating;
    private DateTime? _from, _to;
    private string _search = "";
    private bool _includeMissing, _includeDuplicates;
    private LibrarySort _sort = LibrarySort.NewestFirst;

    public ObservableCollection<FilterOption> Cameras { get; } = new();
    public ObservableCollection<FilterOption> Lenses { get; } = new();
    public ObservableCollection<FilterOption> Locations { get; } = new();

    public IReadOnlyList<FilterOption> Kinds { get; } =
    [
        new(null, "すべての種類"),
        new(nameof(MediaKind.Jpeg), "JPEG"),
        new(nameof(MediaKind.Raw), "RAW"),
        new(nameof(MediaKind.Heif), "HEIF"),
        new(nameof(MediaKind.Video), "動画"),
    ];

    public IReadOnlyList<FilterOption> Ratings { get; } =
    [
        new("0", "★ 指定なし"),
        new("1", "★1 以上"),
        new("2", "★2 以上"),
        new("3", "★3 以上"),
        new("4", "★4 以上"),
        new("5", "★5 のみ"),
    ];

    public IReadOnlyList<KeyValuePair<LibrarySort, string>> Sorts { get; } =
    [
        new(LibrarySort.NewestFirst, "撮影日が新しい順"),
        new(LibrarySort.OldestFirst, "撮影日が古い順"),
        new(LibrarySort.FileName, "ファイル名順"),
    ];

    public FilterOption? Camera   { get => _camera;   set { if (SetField(ref _camera, value)) Apply(); } }
    public FilterOption? Lens     { get => _lens;     set { if (SetField(ref _lens, value)) Apply(); } }
    public FilterOption? Location { get => _location; set { if (SetField(ref _location, value)) Apply(); } }
    public FilterOption? Kind     { get => _kind;     set { if (SetField(ref _kind, value)) Apply(); } }
    public FilterOption? Rating   { get => _rating;   set { if (SetField(ref _rating, value)) Apply(); } }
    public DateTime? From         { get => _from;     set { if (SetField(ref _from, value)) Apply(); } }
    public DateTime? To           { get => _to;       set { if (SetField(ref _to, value)) Apply(); } }
    public string Search          { get => _search;   set { if (SetField(ref _search, value ?? "")) Apply(); } }
    public bool IncludeMissing    { get => _includeMissing;    set { if (SetField(ref _includeMissing, value)) { RefreshFacets(); Apply(); } } }
    public bool IncludeDuplicates { get => _includeDuplicates; set { if (SetField(ref _includeDuplicates, value)) { RefreshFacets(); Apply(); } } }

    private void RefreshFacets()
    {
        if (_loaded && !_suppressApply) BuildFacets();
    }
    public LibrarySort Sort       { get => _sort;     set { if (SetField(ref _sort, value)) Apply(); } }

    /// <summary>カメラ名で選ぶ（スクリーンショット作成など、外部から絞り込むとき用）</summary>
    public void SelectCamera(string camera) =>
        Camera = Cameras.FirstOrDefault(c => string.Equals(c.Value, camera, StringComparison.OrdinalIgnoreCase)) ?? Camera;

    private void BuildFacets()
    {
        _suppressApply = true;
        try
        {
            // 件数は「重複も表示」「見つからないファイルも表示」の設定に合わせて数える
            var visible = _all.Where(p => (_includeMissing || p.Exists)
                                          && (_includeDuplicates || p.Asset.Status != Domain.Enums.AssetStatus.Duplicate)).ToList();
            Refill(Cameras, "すべてのカメラ", LibraryService.Facet(visible, p => p.Camera), ref _camera, nameof(Camera));
            Refill(Lenses, "すべてのレンズ", LibraryService.Facet(visible, p => p.Lens), ref _lens, nameof(Lens));

            // 国だけ（例: Japan）と、国 / 都市（例: Japan / Kyoto）の両方を選べるようにする
            var countries = LibraryService.Facet(visible, p => p.Location?.Split(" / ")[0]);
            var cities = LibraryService.Facet(visible, p => p.Location is { } l && l.Contains(" / ") ? l : null);
            var places = countries.Concat(cities)
                .OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase).ToList();
            Refill(Locations, "すべての場所", places, ref _location, nameof(Location));

            _kind ??= Kinds[0];
            _rating ??= Ratings[0];
            OnPropertiesChanged(nameof(Kind), nameof(Rating));
        }
        finally { _suppressApply = false; }
    }

    private void Refill(ObservableCollection<FilterOption> target, string allLabel,
        IReadOnlyList<(string Value, int Count)> values, ref FilterOption? current, string propertyName)
    {
        var keep = current?.Value;
        target.Clear();
        target.Add(new FilterOption(null, allLabel));
        foreach (var (value, count) in values)
            target.Add(new FilterOption(value, $"{value}（{count:N0}）"));
        current = target.FirstOrDefault(o => o.Value == keep && keep != null) ?? target[0];
        OnPropertyChanged(propertyName);
    }

    private void ClearFilters()
    {
        _suppressApply = true;
        Camera = Cameras.FirstOrDefault();
        Lens = Lenses.FirstOrDefault();
        Location = Locations.FirstOrDefault();
        Kind = Kinds[0];
        Rating = Ratings[0];
        From = null;
        To = null;
        Search = "";
        IncludeMissing = false;
        IncludeDuplicates = false;
        _suppressApply = false;
        Apply();
    }

    private void Apply()
    {
        if (_suppressApply || !_loaded) return;

        var criteria = new LibraryCriteria
        {
            Camera = _camera?.Value,
            Lens = _lens?.Value,
            Location = _location?.Value,
            Kind = _kind?.Value is { } k ? Enum.Parse<MediaKind>(k) : null,
            MinRating = int.TryParse(_rating?.Value, out var r) ? r : 0,
            From = _from,
            To = _to,
            Search = _search,
            IncludeMissing = _includeMissing,
            IncludeDuplicates = _includeDuplicates,
            Sort = _sort,
        };
        _filtered = LibraryService.Filter(_all, criteria).Select(p => new LibraryItemViewModel(p, this)).ToList();
        BuildRows();

        int missing = _all.Count(p => !p.Exists);
        Status = _all.Count == 0
            ? "まだ写真の記録がありません。「実行ログ」タブで一度整理（DRY_RUN でも可）すると、ここに並びます。"
            : $"{_filtered.Count:N0} 枚 / 全 {_all.Count:N0} 枚" + (missing > 0 && !_includeMissing ? $"（見つからないファイル {missing:N0} 件は非表示）" : "");

        if (_selected != null && !_filtered.Any(i => i.Path == _selected.Path)) Selected = null;
    }

    // ===================== グリッド =====================

    /// <summary>1 行ずつ並べる（行単位で仮想化して、数万枚でも軽く表示する）</summary>
    public IReadOnlyList<IReadOnlyList<LibraryItemViewModel>> Rows { get; private set; } = [];

    public double ThumbnailSize
    {
        get => _thumbnailSize;
        set
        {
            if (!SetField(ref _thumbnailSize, Math.Clamp(value, 100, 360))) return;
            OnPropertyChanged(nameof(CardWidth));
            OnPropertyChanged(nameof(CardHeight));
            _columns = 0; // 列数が同じでも作り直して、新しい大きさのサムネイルを読み込ませる
            RecomputeColumns();
        }
    }

    public double CardWidth => _thumbnailSize + 12;
    public double CardHeight => _thumbnailSize * 0.75;
    public int ThumbnailDecodeWidth => ThumbnailLoader.BucketWidth(_thumbnailSize);

    private double _availableWidth = 900;

    /// <summary>表示幅が変わったとき（画面側から呼ぶ）</summary>
    public void SetAvailableWidth(double width)
    {
        _availableWidth = width;
        RecomputeColumns();
    }

    private void RecomputeColumns()
    {
        var cols = Math.Max(1, (int)((_availableWidth - 24) / (CardWidth + 8)));
        if (cols == _columns && Rows.Count > 0) return;
        _columns = cols;
        BuildRows();
    }

    private void BuildRows()
    {
        Rows = _filtered.Chunk(_columns).Select(c => (IReadOnlyList<LibraryItemViewModel>)c).ToList();
        OnPropertyChanged(nameof(Rows));
    }

    // ===================== 選択・プレビュー =====================

    public LibraryItemViewModel? Selected
    {
        get => _selected;
        set
        {
            var old = _selected;
            if (!SetField(ref _selected, value)) return;
            if (old != null) old.IsSelected = false;
            if (value != null) value.IsSelected = true;
            OnPropertiesChanged(nameof(HasSelection), nameof(SelectedDetails));
            _ = LoadPreviewAsync(value);
        }
    }

    public bool HasSelection => _selected != null;

    public ImageSource? Preview { get => _preview; private set => SetField(ref _preview, value); }

    public IReadOnlyList<KeyValuePair<string, string>> SelectedDetails => _selected?.Details ?? [];

    private async Task LoadPreviewAsync(LibraryItemViewModel? item)
    {
        int request = Interlocked.Increment(ref _previewRequest);
        Preview = null;
        if (item == null || !item.Exists) return;
        var image = await ThumbnailLoader.LoadAsync(item.Path, 1000);
        if (request == _previewRequest) Preview = image;
    }

    // ===================== コマンド =====================

    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand SelectCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand ShowInExplorerCommand { get; }
    public RelayCommand ClearCacheCommand { get; }

    private void ClearCache()
    {
        var size = FileSizeConverter.Format(ThumbnailLoader.DiskCacheBytes());
        if (!Dialogs.Confirm($"保存してあるサムネイル（{size}）を削除します。\n次に表示するときに作り直します（写真そのものは消えません）。\n\n続けますか？",
                "サムネイルのキャッシュを削除"))
            return;
        try
        {
            ThumbnailLoader.ClearDiskCache();
            foreach (var i in _filtered) i.ResetThumbnail();
            BuildRows();
            Status = "サムネイルのキャッシュを削除しました。";
        }
        catch (Exception ex) { Dialogs.Error($"削除できませんでした:\n{ex.Message}"); }
    }
}

/// <summary>ライブラリの 1 枚</summary>
public sealed class LibraryItemViewModel : ViewModelBase
{
    private readonly LibraryViewModel _owner;
    private ImageSource? _thumbnail;
    private int _requestedWidth;
    private bool _isSelected;

    public LibraryItemViewModel(LibraryPhoto photo, LibraryViewModel owner)
    {
        Photo = photo;
        _owner = owner;
    }

    public LibraryPhoto Photo { get; }
    public string Path => Photo.CurrentPath;
    public string FileName => Photo.FileName;
    public bool Exists => Photo.Exists;
    public string DateText => Photo.Date.ToString(Photo.HasExifDate ? "yyyy/MM/dd HH:mm" : "yyyy/MM/dd（ファイル日時）", CultureInfo.InvariantCulture);
    public string Stars => Photo.Rating > 0 ? new string('★', Photo.Rating) : "";
    public string Caption => Photo.Camera == "Unknown" ? DateText : $"{DateText}  ・  {Photo.Camera}";
    public bool IsVideo => Photo.Kind == MediaKind.Video;
    public string KindLabel => MediaFormats.KindLabel(Photo.Kind);

    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    public string Tooltip => $"{FileName}\n{Photo.CurrentPath}" + (Exists ? "" : "\n（ファイルが見つかりません）");

    /// <summary>初めて表示されたときに読み込む</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            var width = _owner.ThumbnailDecodeWidth;
            if (_requestedWidth != width && Exists)
            {
                _requestedWidth = width;
                _ = LoadThumbnailAsync(width);
            }
            return _thumbnail;
        }
    }

    public void ResetThumbnail()
    {
        _requestedWidth = 0;
        _thumbnail = null;
    }

    private async Task LoadThumbnailAsync(int width)
    {
        var img = await ThumbnailLoader.LoadAsync(Path, width);
        if (img == null || width != _requestedWidth) return;
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            _thumbnail = img;
            OnPropertyChanged(nameof(Thumbnail));
        });
    }

    public IReadOnlyList<KeyValuePair<string, string>> Details
    {
        get
        {
            var a = Photo.Asset;
            var e = a.Exif;
            var list = new List<KeyValuePair<string, string>>
            {
                new("ファイル", FileName),
                new("撮影日時", DateText),
                new("カメラ", Photo.Camera),
            };
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) list.Add(new(label, value));
            }
            Add("レンズ", Photo.Lens == "Unknown" ? null : Photo.Lens);
            Add("焦点距離", e?.FocalLength is { } f ? $"{f} mm" : null);
            Add("絞り", e?.Aperture is { } ap ? $"F{ap}" : null);
            Add("シャッター", e?.ShutterSpeed is { } ss ? FormatShutter(ss) : null);
            Add("ISO", e?.ISO);
            Add("サイズ", e?.ImageWidth is int w && e.ImageHeight is int h ? $"{w} × {h}" : null);
            Add("レーティング", Stars);
            Add("撮影地", Photo.Location);
            Add("GPS", e?.HasGps == true ? $"{e.GPSLatitude:F5}, {e.GPSLongitude:F5}" : null);
            Add("種類", KindLabel);
            Add("ファイルサイズ", FileSizeConverter.Format(a.FileSize));
            Add("場所", Photo.CurrentPath);
            if (!Exists) Add("注意", "ファイルが見つかりません（別のツールで移動・削除された可能性があります）");
            return list;
        }
    }

    /// <summary>exiftool の -n 出力（秒の小数）を 1/250 のような表記にする</summary>
    private static string FormatShutter(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s <= 0) return value;
        if (s >= 1) return $"{s:0.#} 秒";
        return $"1/{Math.Round(1 / s):0} 秒";
    }
}
