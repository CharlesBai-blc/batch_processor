using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.ViewModels;

public partial class JobAssignmentViewModel : ObservableObject
{
    [ObservableProperty]
    private string _executablePath = "/opt/processor/run.sh";

    [ObservableProperty]
    private string _outputS3Prefix = string.Empty;

    public ObservableCollection<JobAssignment> Assignments { get; } = new();
    public ObservableCollection<S3ObjectItem> UnassignedFiles { get; } = new();

    [RelayCommand]
    private void AssignFileToInstance(object? parameter) { }

    [RelayCommand]
    private void RemoveFileFromInstance(object? parameter) { }
}
