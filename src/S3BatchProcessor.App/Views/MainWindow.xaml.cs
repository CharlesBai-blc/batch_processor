using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using S3BatchProcessor.App.ViewModels;

namespace S3BatchProcessor.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is not MainViewModel vm)
            return;

        vm.PropertyChanged += OnMainViewModelPropertyChanged;
        vm.JobExecution.PropertyChanged += OnJobExecutionPropertyChanged;
        ApplyAssignFirstSplit();
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedTabIndex) || DataContext is not MainViewModel vm)
            return;

        if (vm.SelectedTabIndex == 2 && !vm.JobExecution.IsRunning)
            ApplyAssignFirstSplit();
    }

    private void OnJobExecutionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JobExecutionViewModel.IsRunning) || DataContext is not MainViewModel vm)
            return;

        if (vm.JobExecution.IsRunning)
            ApplyExecutionFavoredSplit();
    }

    private void ApplyAssignFirstSplit()
    {
        if (AssignRunSplitGrid.RowDefinitions.Count < 3)
            return;

        AssignRunSplitGrid.RowDefinitions[0].Height = new GridLength(3, GridUnitType.Star);
        AssignRunSplitGrid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
    }

    private void ApplyExecutionFavoredSplit()
    {
        if (AssignRunSplitGrid.RowDefinitions.Count < 3)
            return;

        AssignRunSplitGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        AssignRunSplitGrid.RowDefinitions[2].Height = new GridLength(2, GridUnitType.Star);
    }
}
