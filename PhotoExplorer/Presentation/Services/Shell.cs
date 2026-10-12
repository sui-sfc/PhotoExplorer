using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhotoExplorer.Presentation.Services;

/// <summary>エクスプローラー・関連付けアプリ・ごみ箱との連携</summary>
public static partial class Shell
{
    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(path);
        Process.Start(psi);
    }

    /// <summary>エクスプローラーでファイルを選択した状態で開く</summary>
    public static void ShowInExplorer(string filePath)
    {
        if (File.Exists(filePath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false });
        else
            OpenFolder(Path.GetDirectoryName(filePath) ?? filePath);
    }

    public static void OpenWithDefaultApp(string filePath)
    {
        if (File.Exists(filePath))
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
    }

    public static void OpenInNotepad(string filePath)
    {
        var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(filePath);
        Process.Start(psi);
    }

    /// <summary>Windows のごみ箱へ送る（あとで元に戻せる）</summary>
    public static void SendToRecycleBin(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("ファイルが見つかりません", filePath);

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // 二重 null 終端のパス一覧
            pFrom = Path.GetFullPath(filePath) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
            throw new IOException($"ごみ箱へ移動できませんでした（コード {rc}）: {filePath}");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
