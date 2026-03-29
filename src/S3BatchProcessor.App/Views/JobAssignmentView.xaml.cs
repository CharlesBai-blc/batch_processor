using System.Windows.Controls;
using S3BatchProcessor.App.Models;
using S3BatchProcessor.App.ViewModels;

namespace S3BatchProcessor.App.Views;

public partial class JobAssignmentView : UserControl
{
    public JobAssignmentView()
    {
        InitializeComponent();
    }

    private void UnassignedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not JobAssignmentViewModel vm) return;

        vm.SelectedUnassignedFiles.Clear();
        foreach (var item in UnassignedList.SelectedItems)
        {
            if (item is S3ObjectItem file)
                vm.SelectedUnassignedFiles.Add(file);
        }
    }
}
