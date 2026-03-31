using System.Globalization;
using System.Windows.Data;

namespace S3BatchProcessor.App.Converters;

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;
}
