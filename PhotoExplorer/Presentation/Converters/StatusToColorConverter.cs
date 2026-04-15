using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PhotoExplorer.Domain.Enums;
// UseWindowsForms により System.Drawing.Brush と競合するため明示的にエイリアスを定義
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace PhotoExplorer.Presentation.Converters;

[ValueConversion(typeof(AssetStatus), typeof(Brush))]
public sealed class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AssetStatus status)
        {
            return status switch
            {
                AssetStatus.MetadataOk => Brushes.LightGreen,
                AssetStatus.PartialMetadata => Brushes.LightYellow,
                AssetStatus.NoMetadata => Brushes.LightGray,
                AssetStatus.Duplicate => Brushes.LightSalmon,
                AssetStatus.Processed => new SolidColorBrush(Color.FromRgb(0xB7, 0xEB, 0x8F)),
                AssetStatus.Error => Brushes.LightCoral,
                _ => Brushes.White,
            };
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(OperationMode), typeof(string))]
public sealed class OperationModeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is OperationMode mode)
        {
            return mode switch
            {
                OperationMode.DryRun => "DRY_RUN（確認のみ）",
                OperationMode.Copy => "COPY（コピー）",
                OperationMode.Move => "MOVE（移動）",
                _ => mode.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Accepts bool or int (int 0 = false, >0 = true)
        bool bValue = value switch
        {
            bool b  => b,
            int  i  => i > 0,
            _       => false,
        };
        bool invert = parameter?.ToString() == "Invert";
        if (invert) bValue = !bValue;
        return bValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 棒グラフ用 — Ratio (0.0–1.0) × コンテナ幅 を Rectangle の Width に変換する
/// </summary>
public sealed class RatioToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2
            && values[0] is double ratio
            && values[1] is double containerWidth
            && containerWidth > 0)
        {
            return Math.Max(0, ratio * containerWidth);
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
