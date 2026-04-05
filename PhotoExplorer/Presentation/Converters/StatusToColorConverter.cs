using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PhotoExplorer.Domain.Enums;

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
        bool bValue = value is bool b && b;
        bool invert = parameter?.ToString() == "Invert";
        if (invert) bValue = !bValue;
        return bValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
