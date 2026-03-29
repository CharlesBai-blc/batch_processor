using System.Globalization;
using System.Windows.Data;

namespace S3BatchProcessor.App.Converters;

public class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes) return "0 B";

        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < Units.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {Units[order]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
