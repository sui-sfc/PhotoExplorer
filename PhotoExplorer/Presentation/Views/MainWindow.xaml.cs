using System.Collections.Specialized;
using System.Windows;
using PhotoExplorer.Presentation.ViewModels;

namespace PhotoExplorer.Presentation.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // ログが追加されたら末尾へ自動スクロール
        viewModel.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
    }

    private void LogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (LogListBox.Items.Count > 0)
                    LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            });
        }
    }
}
