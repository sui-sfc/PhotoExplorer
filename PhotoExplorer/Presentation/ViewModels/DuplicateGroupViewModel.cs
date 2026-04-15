using System.Collections.ObjectModel;

namespace PhotoExplorer.Presentation.ViewModels;

public sealed class DuplicateGroupViewModel
{
    public string GroupId { get; init; } = string.Empty;
    public ObservableCollection<string> Files { get; } = new();
    public string Summary => $"グループ {GroupId}  ({Files.Count} 件)";
}
