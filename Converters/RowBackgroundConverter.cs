using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VatscaUpdateChecker.Converters;

public class RowBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? "RowAltBg" : "RowBg";
        return Application.Current.Resources[key] as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
