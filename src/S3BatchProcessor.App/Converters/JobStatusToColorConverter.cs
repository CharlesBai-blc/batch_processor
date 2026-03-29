using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Converters;

public class JobStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            JobStatus.Success => Brushes.Green,
            JobStatus.InProgress => Brushes.DodgerBlue,
            JobStatus.Pending => Brushes.Orange,
            JobStatus.Failed => Brushes.Red,
            JobStatus.TimedOut => Brushes.DarkRed,
            JobStatus.NotStarted => Brushes.Gray,
            _ => Brushes.Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
