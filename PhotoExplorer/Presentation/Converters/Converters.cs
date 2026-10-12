using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PhotoExplorer.Application;
using PhotoExplorer.Domain.Enums;
using PhotoExplorer.Presentation.ViewModels;

namespace PhotoExplorer.Presentation.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    private static SolidColorBrush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static readonly SolidColorBrush Ok       = B("#DCFCE7");
    private static readonly SolidColorBrush Partial  = B("#FEF9C3");
    private static readonly SolidColorBrush None     = B("#F1F5F9");
    private static readonly SolidColorBrush Dup      = B("#FFEDD5");
    private static readonly SolidColorBrush Done     = B("#BBF7D0");
    private static readonly SolidColorBrush Skip     = B("#E0F2FE");
    private static readonly SolidColorBrush Filtered = B("#E2E8F0");
    private static readonly SolidColorBrush Err      = B("#FECACA");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AssetStatus.MetadataOk      => Ok,
        AssetStatus.PartialMetadata => Partial,
        AssetStatus.NoMetadata      => None,
        AssetStatus.Duplicate       => Dup,
        AssetStatus.Processed       => Done,
        AssetStatus.Skipped         => Skip,
        AssetStatus.Filtered        => Filtered,
        AssetStatus.Error           => Err,
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AssetStatus s ? ImageAssetViewModel.StatusText(s) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static SolidColorBrush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static readonly SolidColorBrush Debug   = B("#94A3B8");
    private static readonly SolidColorBrush Info    = B("#1E293B");
    private static readonly SolidColorBrush Success = B("#15803D");
    private static readonly SolidColorBrush Warning = B("#B45309");
    private static readonly SolidColorBrush Error   = B("#DC2626");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Debug   => Debug,
        LogLevel.Success => Success,
        LogLevel.Warning => Warning,
        LogLevel.Error   => Error,
        _ => Info,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class OperationModeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        OperationMode.DryRun => "DRY_RUN（確認だけ・変更しない）",
        OperationMode.Copy   => "COPY（コピー・元は残す）",
        OperationMode.Move   => "MOVE（移動・元から消える）",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool / 数値（0 以外）/ 文字列（空でない）/ コレクション（要素あり）/ その他（null でない）を Visible にする。
/// ConverterParameter=Invert で反転。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            bool b       => b,
            int i        => i != 0,
            long l       => l != 0,
            string s     => !string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count > 0,
            null         => false,
            _            => true,
        };
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long l ? Format(l) : value is int i ? Format(i) : string.Empty;

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):F2} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
