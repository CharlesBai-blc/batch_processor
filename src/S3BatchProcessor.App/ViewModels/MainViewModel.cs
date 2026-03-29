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
    }

    public StatusBarViewModel StatusBar { get; }
    public S3BrowserViewModel S3Browser { get; }
    public Ec2ManagerViewModel Ec2Manager { get; }
    public JobAssignmentViewModel JobAssignment { get; }
    public JobExecutionViewModel JobExecution { get; }

    [ObservableProperty]
    private int _selectedTabIndex;
}
