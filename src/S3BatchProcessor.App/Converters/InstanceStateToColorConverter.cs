using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Converters;

public class InstanceStateToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            Ec2InstanceState.Running => Brushes.Green,
            Ec2InstanceState.Pending => Brushes.Orange,
            Ec2InstanceState.Stopping => Brushes.Orange,
            Ec2InstanceState.Stopped => Brushes.Gray,
            Ec2InstanceState.Terminated => Brushes.Red,
            _ => Brushes.Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
