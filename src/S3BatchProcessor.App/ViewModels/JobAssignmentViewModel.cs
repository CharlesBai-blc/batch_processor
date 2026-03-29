using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.ViewModels;

public partial class JobAssignmentViewModel : ObservableObject
{
    private readonly IConfiguration _configuration;

    public event Action<IList<JobAssignment>, string, string>? RunRequested;

    public JobAssignmentViewModel(IConfiguration configuration)
    {
        _configuration = configuration;
        ExecutablePath = _configuration["Processing:ExecutablePath"] ?? "/opt/processor/run.sh";
        OutputS3Prefix = _configuration["Processing:OutputS3Prefix"] ?? "s3://output-bucket/results/";
    }

    [ObservableProperty]
    private string _executablePath = "/opt/processor/run.sh";

    [ObservableProperty]
    private string _outputS3Prefix = string.Empty;

    [ObservableProperty]
    private int _unassignedCount;

    [ObservableProperty]
    private int _totalFileCount;

    [ObservableProperty]
    private string _validationError = string.Empty;

    [ObservableProperty]
    private bool _hasValidationError;

    public ObservableCollection<JobAssignment> Assignments { get; } = new();
    public ObservableCollection<S3ObjectItem> UnassignedFiles { get; } = new();

    public List<S3ObjectItem> SelectedUnassignedFiles { get; } = new();

    public void LoadSelections(IEnumerable<S3ObjectItem> files, IEnumerable<Ec2InstanceItem> instances)
    {
        Assignments.Clear();
        UnassignedFiles.Clear();
        ClearValidation();

        foreach (var file in files)
            UnassignedFiles.Add(file);

        foreach (var inst in instances)
            Assignments.Add(new JobAssignment { Instance = inst });

        UpdateCounts();
    }

    [RelayCommand]
    private void AutoDistribute()
    {
        if (Assignments.Count == 0) return;

        var allFiles = UnassignedFiles.ToList();
        UnassignedFiles.Clear();

        for (var i = 0; i < allFiles.Count; i++)
        {
            var target = Assignments[i % Assignments.Count];
            target.Files.Add(allFiles[i]);
        }

        ClearValidation();
        UpdateCounts();
    }

    [RelayCommand]
    private void AddSelectedToInstance(JobAssignment? assignment)
    {
        if (assignment is null || SelectedUnassignedFiles.Count == 0) return;

        var toMove = SelectedUnassignedFiles.ToList();
        foreach (var file in toMove)
        {
            UnassignedFiles.Remove(file);
            assignment.Files.Add(file);
        }
        SelectedUnassignedFiles.Clear();
        ClearValidation();
        UpdateCounts();
    }

    [RelayCommand]
    private void AssignFileToInstance(object? parameter)
    {
        if (parameter is not object[] args || args.Length < 2) return;
        if (args[0] is not S3ObjectItem file || args[1] is not JobAssignment assignment) return;

        UnassignedFiles.Remove(file);
        assignment.Files.Add(file);
        ClearValidation();
        UpdateCounts();
    }

    [RelayCommand]
    private void RemoveFileFromInstance(object? parameter)
    {
        if (parameter is not object[] args || args.Length < 2) return;
        if (args[0] is not S3ObjectItem file || args[1] is not JobAssignment assignment) return;

        assignment.Files.Remove(file);
        UnassignedFiles.Add(file);
        UpdateCounts();
    }

    [RelayCommand]
    private void ClearAssignments()
    {
        foreach (var assignment in Assignments)
        {
            foreach (var file in assignment.Files.ToList())
                UnassignedFiles.Add(file);
            assignment.Files.Clear();
        }
        UpdateCounts();
    }

    [RelayCommand]
    private void Run()
    {
        var problems = Validate();
        if (problems.Count > 0)
        {
            ValidationError = string.Join("\n", problems);
            HasValidationError = true;
            return;
        }

        ClearValidation();
        RunRequested?.Invoke(Assignments, ExecutablePath, OutputS3Prefix);
    }

    [RelayCommand]
    private void DismissValidation() => ClearValidation();

    private List<string> Validate()
    {
        var problems = new List<string>();

        if (UnassignedFiles.Count > 0)
        {
            var names = UnassignedFiles.Select(f => f.Name).Take(5).ToList();
            var suffix = UnassignedFiles.Count > 5 ? $" and {UnassignedFiles.Count - 5} more" : "";
            problems.Add($"Unassigned files ({UnassignedFiles.Count}): {string.Join(", ", names)}{suffix}");
        }

        var emptyInstances = Assignments.Where(a => a.Files.Count == 0).ToList();
        if (emptyInstances.Count > 0)
        {
            var names = emptyInstances.Select(a => a.Instance.NameTag);
            problems.Add($"Instances with no files: {string.Join(", ", names)}");
        }

        if (Assignments.Count == 0)
            problems.Add("No instances available. Go back to Tab 2 and select instances.");

        if (string.IsNullOrWhiteSpace(ExecutablePath))
            problems.Add("Executable path is required.");

        if (string.IsNullOrWhiteSpace(OutputS3Prefix))
            problems.Add("Output S3 prefix is required.");

        return problems;
    }

    private void ClearValidation()
    {
        ValidationError = string.Empty;
        HasValidationError = false;
    }

    private void UpdateCounts()
    {
        UnassignedCount = UnassignedFiles.Count;
        TotalFileCount = UnassignedFiles.Count + Assignments.Sum(a => a.Files.Count);
    }
}
