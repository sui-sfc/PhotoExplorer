using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Presentation.Services;
using PhotoExplorer.Presentation.ViewModels;
using PhotoExplorer.Presentation.Views;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace PhotoExplorer.Presentation.Tools;

/// <summary>
/// README 用のスクリーンショットを作る（PhotoExplorer.exe --screenshots &lt;出力フォルダ&gt;）。
/// デモ用の写真を生成して実際に DRY_RUN と COPY を実行し、各タブを PNG に保存する。
/// </summary>
internal static class ScreenshotRunner
{
    private sealed record DemoCamera(string Make, string Model, string Lens, string Prefix, string Ext);

    private static readonly DemoCamera[] Cameras =
    [
        new("Canon", "Canon EOS R5", "RF24-105mm F4 L IS USM", "IMG_", ".JPG"),
        new("SONY", "ILCE-7M4", "FE 24-70mm F2.8 GM II", "DSC", ".JPG"),
        new("RICOH IMAGING COMPANY, LTD.", "RICOH GR IIIx", "GR LENS 26.1mm F2.8", "R00", ".JPG"),
        new("FUJIFILM", "X-T5", "XF16-55mmF2.8 R LM WR", "DSCF", ".JPG"),
        new("Apple", "iPhone 15 Pro", "iPhone 15 Pro back triple camera 6.765mm f/1.78", "IMG_E", ".JPG"),
    ];

    private static readonly (double Lat, double Lon)[] Places =
    [
        (35.6595, 139.7005), // 渋谷
        (35.0116, 135.7681), // 京都
        (34.6937, 135.5023), // 大阪
        (43.0618, 141.3545), // 札幌
        (26.2124, 127.6809), // 那覇
        (48.8584, 2.2945),   // パリ
    ];

    public static async Task RunAsync(WpfApplication app, MainWindow window, MainViewModel vm, string outputDir)
    {
        int exitCode = 0;
        try
        {
            Dialogs.AutoAnswer = true;
            Directory.CreateDirectory(outputDir);
            var root = Path.Combine(Path.GetTempPath(), "PhotoExplorerScreenshots");
            var photos = Path.Combine(root, "SDカード");
            var output = Path.Combine(root, "Pictures");
            var trash = Path.Combine(root, "重複");
            foreach (var d in new[] { photos, output, trash })
                if (Directory.Exists(d)) Directory.Delete(d, true);

            await Task.Run(() => CreateDemoPhotos(photos));

            window.Width = 1280;
            window.Height = 1000;
            window.WindowState = WindowState.Normal;
            window.Left = 0;
            window.Top = 0;

            vm.InputDirectory = photos;
            vm.OutputDirectory = output;
            vm.TrashDirectory = trash;
            vm.FolderTemplate = "{yyyy}/{mm}/{camera}";
            vm.FileNameTemplate = "";
            vm.MaxParallelism = 4;

            vm.OperationMode = OperationMode.DryRun;
            await RunAndWait(vm);
            vm.OperationMode = OperationMode.Copy;
            await RunAndWait(vm);

            var tabs = (TabControl)window.FindName("MainTabs");
            string[] names = ["01-log", "02-files", "03-statistics", "04-duplicates", "05-help"];
            for (int i = 0; i < names.Length; i++)
            {
                tabs.SelectedIndex = i;
                await Settle(window, i == 3 ? 2500 : 600);
                Save(window, Path.Combine(outputDir, names[i] + ".png"));
            }

            // 統計タブは縦に長いので全体も保存する
            tabs.SelectedIndex = 2;
            await Settle(window, 600);
            if (window.FindName("StatsPanel") is FrameworkElement stats)
                Save(stats, Path.Combine(outputDir, "03-statistics-full.png"));

            // 詳細オプションを開いた状態
            tabs.SelectedIndex = 0;
            if (window.FindName("OptionsExpander") is Expander exp) exp.IsExpanded = true;
            await Settle(window, 600);
            Save(window, Path.Combine(outputDir, "06-options.png"));

            Console.WriteLine($"スクリーンショットを保存しました: {outputDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            File.WriteAllText(Path.Combine(outputDir, "screenshot-error.txt"), ex.ToString());
            exitCode = 1;
        }
        finally
        {
            Dialogs.AutoAnswer = null;
            app.Shutdown(exitCode);
        }
    }

    private static async Task RunAndWait(MainViewModel vm)
    {
        vm.StartCommand.Execute(null);
        var sw = Stopwatch.StartNew();
        await Task.Delay(300);
        while (vm.IsRunning && sw.Elapsed < TimeSpan.FromMinutes(5))
            await Task.Delay(200);
    }

    private static async Task Settle(Window window, int ms)
    {
        window.UpdateLayout();
        await Task.Delay(ms);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void Save(FrameworkElement element, string path)
    {
        const double dpi = 120; // 125% 相当で少しくっきり
        var size = new Size(element.ActualWidth, element.ActualHeight);
        if (element is Window w && w.Content is FrameworkElement content)
        {
            element = content;
            size = new Size(content.ActualWidth, content.ActualHeight);
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(size));
        }

        var scale = dpi / 96.0;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ===================== デモ写真の生成 =====================

    private static void CreateDemoPhotos(string root)
    {
        var rnd = new Random(20240715);
        var exifArgs = new List<string>();
        var renames = new List<(string From, string To)>();
        var start = new DateTime(2023, 3, 1);
        int n = 0;

        foreach (var (cam, ci) in Cameras.Select((c, i) => (c, i)))
        {
            int count = 6 + ci * 2;
            var folder = Path.Combine(root, "DCIM", $"{100 + ci}{cam.Model.Split(' ')[0].ToUpperInvariant()}");
            for (int k = 0; k < count; k++)
            {
                n++;
                var shot = start.AddDays(rnd.Next(0, 560)).AddHours(rnd.Next(6, 21)).AddMinutes(rnd.Next(60)).AddSeconds(rnd.Next(60));
                var name = $"{cam.Prefix}{1000 + n:D4}{cam.Ext}";
                var path = Path.Combine(folder, name);
                Directory.CreateDirectory(folder);
                DrawLandscape(path, rnd, n);

                var place = Places[rnd.Next(Places.Length)];
                exifArgs.Add("-Make=" + cam.Make);
                exifArgs.Add("-Model=" + cam.Model);
                exifArgs.Add("-LensModel=" + cam.Lens);
                exifArgs.Add("-DateTimeOriginal=" + shot.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
                if (rnd.NextDouble() < 0.75)
                {
                    exifArgs.Add("-GPSLatitude=" + Math.Abs(place.Lat).ToString(CultureInfo.InvariantCulture));
                    exifArgs.Add("-GPSLatitudeRef=" + (place.Lat >= 0 ? "N" : "S"));
                    exifArgs.Add("-GPSLongitude=" + Math.Abs(place.Lon).ToString(CultureInfo.InvariantCulture));
                    exifArgs.Add("-GPSLongitudeRef=" + (place.Lon >= 0 ? "E" : "W"));
                }
                if (rnd.NextDouble() < 0.3) exifArgs.Add("-XMP:Rating=" + rnd.Next(3, 6));
                exifArgs.Add(path);
                exifArgs.Add("-execute");

                // Sony は RAW + JPEG で撮影したことにする（RAW 側は別内容のファイル）
                if (cam.Make == "SONY" && k % 2 == 0)
                {
                    // 中身は JPEG。タグを書き込んでから .ARW に名前を変える
                    var raw = Path.ChangeExtension(path, ".ARW");
                    var rawTmp = raw + ".tmp.jpg";
                    DrawLandscape(rawTmp, rnd, n + 500);
                    renames.Add((rawTmp, raw));
                    exifArgs.Add("-Make=" + cam.Make);
                    exifArgs.Add("-Model=" + cam.Model);
                    exifArgs.Add("-LensModel=" + cam.Lens);
                    exifArgs.Add("-DateTimeOriginal=" + shot.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
                    exifArgs.Add(rawTmp);
                    exifArgs.Add("-execute");
                    File.WriteAllText(Path.ChangeExtension(path, ".xmp"), "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>");
                }
            }
        }

        // スキャンした古いプリント（EXIF なし）
        var scans = Path.Combine(root, "スキャン");
        Directory.CreateDirectory(scans);
        for (int i = 1; i <= 3; i++)
        {
            var p = Path.Combine(scans, $"scan_{i:D3}.jpg");
            DrawLandscape(p, rnd, 900 + i);
            File.SetLastWriteTime(p, new DateTime(2011, 8, i * 3));
            File.SetCreationTime(p, new DateTime(2011, 8, i * 3));
        }

        RunExifTool(exifArgs);
        foreach (var (from, to) in renames) File.Move(from, to);

        // バックアップ用にコピーしておいたもの（重複）
        var backup = Path.Combine(root, "バックアップ");
        Directory.CreateDirectory(backup);
        var all = Directory.GetFiles(Path.Combine(root, "DCIM"), "*.JPG", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        foreach (var src in all.Where((_, i) => i % 7 == 2).Take(4))
            File.Copy(src, Path.Combine(backup, Path.GetFileNameWithoutExtension(src) + " (1).JPG"));
        File.Copy(all[5], Path.Combine(backup, Path.GetFileNameWithoutExtension(all[5]) + " - コピー.JPG"));
    }

    private static void RunExifTool(List<string> args)
    {
        // 「-execute」で区切った各コマンドに、コマンドラインの -common_args 以降が共通で付く。
        // 日本語パスを含む引数は標準入力から UTF-8 で渡す。
        if (args.Count > 0 && args[^1] == "-execute") args.RemoveAt(args.Count - 1);
        var psi = new ProcessStartInfo(Infrastructure.ExifToolLocator.Resolve(null))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-charset", "filename=utf8", "-@", "-", "-common_args", "-overwrite_original", "-q" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        foreach (var a in args) p.StandardInput.WriteLine(a);
        p.StandardInput.Close();
        p.WaitForExit();
        outTask.Wait();
        var err = errTask.Result;
        if (!string.IsNullOrWhiteSpace(err)) Console.Error.WriteLine(err);
    }

    /// <summary>空・山・太陽・湖の簡単な風景画を描いて JPEG に保存する</summary>
    private static void DrawLandscape(string path, Random rnd, int seed)
    {
        const int w = 900, h = 600;
        var r = new Random(seed * 7919);
        Color Hsv(double hue, double s, double v)
        {
            hue = (hue % 360 + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = v - c;
            var (rr, gg, bb) = hue switch
            {
                < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
                < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
            };
            return Color.FromRgb((byte)((rr + m) * 255), (byte)((gg + m) * 255), (byte)((bb + m) * 255));
        }

        double skyHue = r.Next(0, 6) switch { 0 => 20, 1 => 35, 2 => 200, 3 => 210, 4 => 260, _ => 190 };
        bool sunset = skyHue < 60 || skyHue > 250;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var sky = new LinearGradientBrush(Hsv(skyHue, 0.55, 0.95), Hsv(skyHue + (sunset ? 25 : -15), sunset ? 0.7 : 0.25, 1.0), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

            var sunColor = sunset ? Color.FromRgb(255, 220, 150) : Color.FromRgb(255, 250, 225);
            var sun = new RadialGradientBrush(sunColor, Color.FromArgb(0, sunColor.R, sunColor.G, sunColor.B));
            double sx = r.Next(120, w - 120), sy = r.Next(80, 260);
            dc.DrawEllipse(sun, null, new Point(sx, sy), 120, 120);
            dc.DrawEllipse(new SolidColorBrush(sunColor), null, new Point(sx, sy), 38, 38);

            for (int layer = 0; layer < 3; layer++)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    double baseY = 330 + layer * 70;
                    g.BeginFigure(new Point(0, h), true, true);
                    double x = 0;
                    g.LineTo(new Point(0, baseY - r.Next(0, 80)), true, true);
                    while (x < w)
                    {
                        x += r.Next(60, 160);
                        g.LineTo(new Point(Math.Min(x, w), baseY - r.Next(10, 160 - layer * 40)), true, true);
                    }
                    g.LineTo(new Point(w, h), true, true);
                }
                geo.Freeze();
                double v = sunset ? 0.45 - layer * 0.12 : 0.55 - layer * 0.13;
                double mHue = sunset ? skyHue + 240 : 140 + r.Next(-30, 30);
                dc.DrawGeometry(new SolidColorBrush(Hsv(mHue, 0.35 + layer * 0.1, Math.Max(0.12, v))), null, geo);
            }

            var water = new LinearGradientBrush(Hsv(skyHue, 0.5, 0.6), Hsv(skyHue, 0.6, 0.3), 90) { Opacity = 0.9 };
            dc.DrawRectangle(water, null, new Rect(0, 520, w, 80));
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new JpegBitmapEncoder { QualityLevel = 82 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
