using CommunityToolkit.Mvvm.ComponentModel;

namespace S3BatchProcessor.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel(
        StatusBarViewModel statusBar,
        S3BrowserViewModel s3Browser,
        Ec2ManagerViewModel ec2Manager,
        JobAssignmentViewModel jobAssignment,
        JobExecutionViewModel jobExecution)
    {
        StatusBar = statusBar;
        S3Browser = s3Browser;
        Ec2Manager = ec2Manager;
        JobAssignment = jobAssignment;
        JobExecution = jobExecution;

        S3Browser.ContinueRequested += () => SelectedTabIndex = 1;
        Ec2Manager.ContinueRequested += () => SelectedTabIndex = 2;

        JobAssignment.RunRequested += (assignments, exePath, outputPrefix) =>
        {
            _ = JobExecution.StartExecution(assignments, exePath, outputPrefix);
        };
    }

    public StatusBarViewModel StatusBar { get; }
    public S3BrowserViewModel S3Browser { get; }
    public Ec2ManagerViewModel Ec2Manager { get; }
    public JobAssignmentViewModel JobAssignment { get; }
    public JobExecutionViewModel JobExecution { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 1 && !Ec2Manager.IsLoading)
        {
            _ = Ec2Manager.RefreshInstancesCommand.ExecuteAsync(null);
        }

        if (value == 2)
        {
            JobAssignment.LoadSelections(
                S3Browser.CommittedSelections,
                Ec2Manager.SelectedInstances);
        }
    }
}
