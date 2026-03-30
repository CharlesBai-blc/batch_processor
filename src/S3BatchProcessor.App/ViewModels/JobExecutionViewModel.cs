using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S3BatchProcessor.App.Models;
using S3BatchProcessor.App.Services;

namespace S3BatchProcessor.App.ViewModels;

public partial class JobExecutionViewModel : ObservableObject
{
    private readonly IJobOrchestrationService _orchestrationService;
    private CancellationTokenSource? _cts;

    public JobExecutionViewModel(IJobOrchestrationService orchestrationService)
    {
        _orchestrationService = orchestrationService;
    }

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _phase = string.Empty;

    [ObservableProperty]
    private string _preFlightStatus = string.Empty;

    [ObservableProperty]
    private bool _isPreFlightActive;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private string _progressSummary = string.Empty;

    public ObservableCollection<JobResult> Results { get; } = new();
    public ObservableCollection<string> PreFlightLog { get; } = new();

    public async Task StartExecution(
        IList<JobAssignment> assignments,
        string executablePath,
        string outputS3Prefix,
        string jobLogDirectory,
        string deploySource)
    {
        Results.Clear();
        PreFlightLog.Clear();
        TotalCount = assignments.Sum(a => a.Files.Count);
        CompletedCount = 0;
        FailedCount = 0;
        IsRunning = true;
        IsPreFlightActive = true;
        Phase = "Starting Instances";
        PreFlightStatus = "Checking instance states...";
        ProgressSummary = string.Empty;

        _cts = new CancellationTokenSource();

        try
        {
            var allReady = await _orchestrationService.EnsureInstancesRunningAsync(
                assignments,
                OnPreFlightStatusUpdate,
                _cts.Token);

            if (!allReady)
            {
                Phase = "Pre-flight Failed";
                IsRunning = false;
                IsPreFlightActive = false;
                ProgressSummary = "Execution aborted — not all instances could be started.";
                return;
            }

            IsPreFlightActive = false;
            Phase = "Executing Commands";
            UpdateProgressSummary();

            await _orchestrationService.ExecuteJobsAsync(
                assignments,
                executablePath,
                outputS3Prefix,
                jobLogDirectory,
                deploySource,
                OnResultUpdated,
                _cts.Token);

            Phase = "Complete";
        }
        catch (OperationCanceledException)
        {
            Phase = "Cancelled";
        }
        finally
        {
            IsRunning = false;
            IsPreFlightActive = false;
            UpdateProgressSummary();
        }
    }

    private void OnPreFlightStatusUpdate(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            PreFlightStatus = message;
            PreFlightLog.Add(message);
        });
    }

    private void OnResultUpdated(JobResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!Results.Contains(result))
                Results.Add(result);

            CompletedCount = Results.Count(r => r.Status is JobStatus.Success);
            FailedCount = Results.Count(r => r.Status is JobStatus.Failed or JobStatus.TimedOut);
            UpdateProgressSummary();
        });
    }

    private void UpdateProgressSummary()
    {
        var finished = CompletedCount + FailedCount;
        ProgressSummary = $"{finished} / {TotalCount} completed ({CompletedCount} success, {FailedCount} failed)";
    }

    [RelayCommand]
    private async Task CancelJobsAsync()
    {
        _cts?.Cancel();
        await _orchestrationService.CancelAllAsync();
        IsRunning = false;
        IsPreFlightActive = false;
        Phase = "Cancelled";
        UpdateProgressSummary();
    }
}
