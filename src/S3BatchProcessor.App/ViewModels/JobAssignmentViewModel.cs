using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using S3BatchProcessor.App.Models;
using S3BatchProcessor.App.Services;

namespace S3BatchProcessor.App.ViewModels;

public partial class JobAssignmentViewModel : ObservableObject
{
    private readonly IConfiguration _configuration;
    private readonly IS3Service _s3Service;

    public event Action<IList<JobAssignment>, string, string, string, string>? RunRequested;

    public JobAssignmentViewModel(IConfiguration configuration, IS3Service s3Service)
    {
        _configuration = configuration;
        _s3Service = s3Service;

        ProcessorDirectory = ResolveProcessorDirectory();
        OutputS3Prefix = _configuration["Processing:OutputS3Prefix"] ?? "s3://batchtest3-cbai/results/";
        JobLogDirectory = _configuration["Processing:JobLogDirectory"] ?? @"C:\processor\jobs";
        DeploySource = _configuration["Processing:DeploySource"] ?? "";
        ProcessorFileName = ExtractFileName(DeploySource);
    }

    private string ResolveProcessorDirectory()
    {
        var dir = _configuration["Processing:ProcessorDirectory"];
        if (!string.IsNullOrWhiteSpace(dir))
            return dir.TrimEnd('\\', '/');

        var legacy = _configuration["Processing:ExecutablePath"];
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            var d = Path.GetDirectoryName(legacy.Trim());
            if (!string.IsNullOrEmpty(d))
                return d.TrimEnd('\\', '/');
        }

        return @"C:\processor";
    }

    private static string ExtractFileName(string? s3Uri)
    {
        if (string.IsNullOrWhiteSpace(s3Uri)) return string.Empty;
        var uri = s3Uri.Trim();
        if (!uri.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var key = uri[5..];
        var slash = key.IndexOf('/');
        if (slash < 0 || slash >= key.Length - 1) return string.Empty;
        key = key[(slash + 1)..].TrimEnd('/');
        return key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
    }

    [ObservableProperty]
    private string _processorDirectory = @"C:\processor";

    [ObservableProperty]
    private string _processorFileName = string.Empty;

    [ObservableProperty]
    private string _outputS3Prefix = string.Empty;

    [ObservableProperty]
    private string _jobLogDirectory = @"C:\processor\jobs";

    [ObservableProperty]
    private string _deploySource = string.Empty;

    [ObservableProperty]
    private S3ObjectItem? _pendingDeploySelection;

    [ObservableProperty]
    private int _unassignedCount;

    [ObservableProperty]
    private int _totalFileCount;

    [ObservableProperty]
    private string _validationError = string.Empty;

    [ObservableProperty]
    private bool _hasValidationError;

    [ObservableProperty]
    private bool _isDeployPickerOpen;

    [ObservableProperty]
    private bool _isLoadingBuckets;

    [ObservableProperty]
    private bool _isLoadingDeployFiles;

    [ObservableProperty]
    private string? _selectedDeployBucket;

    [ObservableProperty]
    private string _deployBrowsePrefix = string.Empty;

    public ObservableCollection<JobAssignment> Assignments { get; } = new();
    public ObservableCollection<S3ObjectItem> UnassignedFiles { get; } = new();
    public ObservableCollection<S3BucketItem> DeployBuckets { get; } = new();
    public ObservableCollection<S3ObjectItem> DeployFiles { get; } = new();

    public List<S3ObjectItem> SelectedUnassignedFiles { get; } = new();

    partial void OnPendingDeploySelectionChanged(S3ObjectItem? value)
        => ConfirmDeployPickerCommand.NotifyCanExecuteChanged();

    partial void OnSelectedDeployBucketChanged(string? value)
    {
        DeployBrowsePrefix = string.Empty;
        PendingDeploySelection = null;
        if (!string.IsNullOrEmpty(value))
            _ = LoadDeployFilesAsync();
    }

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
    private async Task OpenDeployPickerAsync()
    {
        PendingDeploySelection = null;
        IsDeployPickerOpen = true;
        if (DeployBuckets.Count == 0)
        {
            IsLoadingBuckets = true;
            try
            {
                var buckets = await _s3Service.ListBucketsAsync();
                DeployBuckets.Clear();
                foreach (var b in buckets)
                    DeployBuckets.Add(b);
            }
            catch { }
            finally { IsLoadingBuckets = false; }
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDeploy))]
    private void ConfirmDeployPicker()
    {
        if (PendingDeploySelection is not { IsFolder: false } item) return;

        DeploySource = $"s3://{item.BucketName}/{item.Key}";
        ProcessorFileName = item.Name;
        PendingDeploySelection = null;
        IsDeployPickerOpen = false;
    }

    private bool CanConfirmDeploy() => PendingDeploySelection is { IsFolder: false };

    [RelayCommand]
    private void CancelDeployPicker()
    {
        PendingDeploySelection = null;
        IsDeployPickerOpen = false;
    }

    [RelayCommand]
    private async Task LoadDeployFilesAsync()
    {
        if (string.IsNullOrEmpty(SelectedDeployBucket)) return;
        IsLoadingDeployFiles = true;
        try
        {
            var items = await _s3Service.ListObjectsAsync(SelectedDeployBucket, DeployBrowsePrefix);
            DeployFiles.Clear();
            foreach (var item in items)
                DeployFiles.Add(item);
            PendingDeploySelection = null;
        }
        catch { }
        finally { IsLoadingDeployFiles = false; }
    }

    [RelayCommand]
    private void OpenDeployFolder(S3ObjectItem? item)
    {
        if (item is not { IsFolder: true }) return;
        DeployBrowsePrefix = item.Key;
        PendingDeploySelection = null;
        _ = LoadDeployFilesAsync();
    }

    [RelayCommand]
    private void DeployNavigateUp()
    {
        if (string.IsNullOrEmpty(DeployBrowsePrefix)) return;

        var trimmed = DeployBrowsePrefix.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        DeployBrowsePrefix = lastSlash >= 0 ? trimmed[..(lastSlash + 1)] : string.Empty;
        PendingDeploySelection = null;
        _ = LoadDeployFilesAsync();
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
        var executablePath = Path.Combine(ProcessorDirectory, ProcessorFileName);
        RunRequested?.Invoke(Assignments, executablePath, OutputS3Prefix, JobLogDirectory, DeploySource);
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

        if (string.IsNullOrWhiteSpace(ProcessorDirectory))
            problems.Add("Processor directory is required.");

        if (string.IsNullOrWhiteSpace(DeploySource) || string.IsNullOrWhiteSpace(ProcessorFileName))
            problems.Add("Processor not selected. Click Browse to pick a processor from S3.");

        if (string.IsNullOrWhiteSpace(OutputS3Prefix))
            problems.Add("Output S3 prefix is required.");

        if (string.IsNullOrWhiteSpace(JobLogDirectory))
            problems.Add("Job log directory is required.");

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
