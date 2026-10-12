using System.Collections.ObjectModel;
using System.Windows.Media;
using PhotoExplorer.Domain.Entities;
using PhotoExplorer.Presentation.Converters;
using PhotoExplorer.Presentation.Services;

namespace PhotoExplorer.Presentation.ViewModels;

public sealed class DuplicateGroupViewModel
{
    public DuplicateGroupViewModel(string groupId, IEnumerable<ImageAsset> files)
    {
        GroupId = groupId;
        foreach (var f in files) Files.Add(new DuplicateFileViewModel(f));
    }

    public string GroupId { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; } = new();

    public long WastedBytes => Files.Where(f => !f.IsKept).Sum(f => f.Asset.FileSize);

    public string Summary =>
        $"{Files.FirstOrDefault()?.FileName}  —  同じ内容のファイル {Files.Count} 件（{FileSizeConverter.Format(Files.FirstOrDefault()?.Asset.FileSize ?? 0)} ずつ）";
}

public sealed class DuplicateFileViewModel : ViewModelBase
{
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _isRemoved;

    public DuplicateFileViewModel(ImageAsset asset) => Asset = asset;

    public ImageAsset Asset { get; }
    public bool IsKept => Asset.IsKeptInDuplicateGroup;
    public string Badge => IsKept ? "残す" : "重複";
    public string FileName => Asset.FileName;
    public string Folder => Path.GetDirectoryName(Asset.SourcePath) ?? "";

    /// <summary>実行後にファイルがある場所（退避済みなら退避先）</summary>
    public string CurrentPath =>
        !File.Exists(Asset.SourcePath) && Asset.OutputPath != null && File.Exists(Asset.OutputPath)
            ? Asset.OutputPath
            : Asset.SourcePath;

    public string Details
    {
        get
        {
            var parts = new List<string> { FileSizeConverter.Format(Asset.FileSize) };
            if (Asset.Exif?.ImageWidth is int w && Asset.Exif?.ImageHeight is int h) parts.Add($"{w}×{h}");
            parts.Add(Asset.BestDate.ToString("yyyy/MM/dd HH:mm"));
            return string.Join("  ・  ", parts);
        }
    }

    public bool IsRemoved
    {
        get => _isRemoved;
        set { if (SetField(ref _isRemoved, value)) OnPropertyChanged(nameof(CanRecycle)); }
    }

    public bool CanRecycle => !IsKept && !IsRemoved;

    /// <summary>初めて表示されたときに読み込む</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        var img = await ThumbnailLoader.LoadAsync(CurrentPath);
        if (img == null) return;
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            _thumbnail = img;
            OnPropertyChanged(nameof(Thumbnail));
        });
    }
}
