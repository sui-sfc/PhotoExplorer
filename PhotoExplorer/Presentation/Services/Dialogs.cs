using System.Windows;
using Microsoft.Win32;

namespace PhotoExplorer.Presentation.Services;

/// <summary>ダイアログ表示（メインウィンドウを親にする）</summary>
public static class Dialogs
{
    private static Window? Owner => WpfApplication.Current?.MainWindow;

    /// <summary>自動操作（スクリーンショット作成など）用。設定すると確認ダイアログを出さずにこの値で答える</summary>
    public static bool? AutoAnswer { get; set; }

    public static string? PickFolder(string title, string? initial)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public static string? PickFile(string title, string filter, string? initial)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (!string.IsNullOrWhiteSpace(initial))
        {
            var dir = Directory.Exists(initial) ? initial : Path.GetDirectoryName(initial);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public static string? SaveFile(string title, string filter, string fileName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, AddExtension = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public static bool Confirm(string message, string title = "確認", bool warning = false) =>
        AutoAnswer ?? Show(message, title, MessageBoxButton.YesNo, warning ? MessageBoxImage.Warning : MessageBoxImage.Question)
            == MessageBoxResult.Yes;

    public static void Info(string message, string title = "PhotoExplorer")
    {
        if (AutoAnswer == null) Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void Error(string message, string title = "PhotoExplorer")
    {
        if (AutoAnswer == null) Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else Console.Error.WriteLine($"[{title}] {message}");
    }

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon) =>
        Owner != null
            ? System.Windows.MessageBox.Show(Owner, message, title, buttons, icon)
            : System.Windows.MessageBox.Show(message, title, buttons, icon);
}
