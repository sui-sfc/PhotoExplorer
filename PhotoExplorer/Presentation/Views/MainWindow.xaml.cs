using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PhotoExplorer.Presentation.ViewModels;

namespace PhotoExplorer.Presentation.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        RestoreWindowPlacement();
        viewModel.LogAppended += (_, _) => ScrollLogToEnd();

        AddHandler(PreviewDragOverEvent, new DragEventHandler(OnPreviewDragOver), handledEventsToo: true);
        AddHandler(PreviewDropEvent, new DragEventHandler(OnPreviewDrop), handledEventsToo: true);
    }

    private void ScrollLogToEnd()
    {
        if (!_vm.AutoScroll || LogListBox.Items.Count == 0) return;
        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    // ---- フォルダのドラッグ＆ドロップ（Tag="Input" / "Output" / "Trash" のテキストボックス） ----

    private static string? DropTarget(DragEventArgs e) =>
        (e.OriginalSource as DependencyObject) is { } src && FindTaggedTextBox(src) is { Tag: string tag } ? tag : null;

    private static TextBox? FindTaggedTextBox(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is TextBox { Tag: string } tb) return tb;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && DropTarget(e) != null)
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (DropTarget(e) is not { } target) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            _vm.SetDroppedFolder(target, paths[0]);
            e.Handled = true;
        }
    }

    // ---- ウィンドウの位置・大きさ・タブを保存 ----

    private void RestoreWindowPlacement()
    {
        var p = _vm.Preferences;
        if (p.WindowWidth >= MinWidth) Width = p.WindowWidth;
        if (p.WindowHeight >= MinHeight) Height = p.WindowHeight;

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!double.IsNaN(p.WindowLeft) && !double.IsNaN(p.WindowTop)
            && screen.Contains(new Point(p.WindowLeft + 50, p.WindowTop + 20)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.WindowLeft;
            Top = p.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (p.WindowMaximized) WindowState = WindowState.Maximized;
        if (p.SelectedTab >= 0 && p.SelectedTab < MainTabs.Items.Count) MainTabs.SelectedIndex = p.SelectedTab;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsRunning && !Services.Dialogs.Confirm("処理中です。中断して終了しますか？\n（ここまでの操作は次回起動後も元に戻せます）", "終了の確認", warning: true))
        {
            e.Cancel = true;
            return;
        }
        if (_vm.IsRunning && _vm.StopCommand.CanExecute(null)) _vm.StopCommand.Execute(null);

        var p = _vm.Preferences;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        p.WindowLeft = bounds.Left;
        p.WindowTop = bounds.Top;
        p.WindowWidth = bounds.Width;
        p.WindowHeight = bounds.Height;
        p.WindowMaximized = WindowState == WindowState.Maximized;
        p.SelectedTab = MainTabs.SelectedIndex;
        _vm.SavePreferences();
        base.OnClosing(e);
    }
}
