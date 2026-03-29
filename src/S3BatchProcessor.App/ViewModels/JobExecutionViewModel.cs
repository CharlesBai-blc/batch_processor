using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S3BatchProcessor.App.Models;
using S3BatchProcessor.App.Services;

namespace S3BatchProcessor.App.ViewModels;

public partial class JobExecutionViewModel : ObservableObject
{
    private readonly IJobOrchestrationService _orchestrationService;

    public JobExecutionViewModel(IJobOrchestrationService orchestrationService)
    {
        _orchestrationService = orchestrationService;
    }

    [ObservableProperty]
    private bool _isRunning;

    public ObservableCollection<JobResult> Results { get; } = new();

    [RelayCommand]
    private Task RunJobsAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task CancelJobsAsync() => Task.CompletedTask;
}
