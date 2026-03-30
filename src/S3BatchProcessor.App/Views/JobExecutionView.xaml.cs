using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace S3BatchProcessor.App.Views;

public partial class JobExecutionView : UserControl
{
    public JobExecutionView()
    {
        InitializeComponent();
    }

    private void DataGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        var hit = VisualTreeHelper.HitTest(grid, e.GetPosition(grid));
        if (hit?.VisualHit is null) return;

        var row = FindParent<DataGridRow>(hit.VisualHit);
        if (row is null)
            grid.SelectedItem = null;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
