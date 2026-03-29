using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S3BatchProcessor.App.Models;
using S3BatchProcessor.App.Services;

namespace S3BatchProcessor.App.ViewModels;

public partial class Ec2ManagerViewModel : ObservableObject
{
    private readonly IEc2Service _ec2Service;
    private readonly ISsmService _ssmService;
    private readonly IS3Service _s3Service;
    private readonly DispatcherTimer _refreshTimer;

    public Ec2ManagerViewModel(IEc2Service ec2Service, ISsmService ssmService, IS3Service s3Service)
    {
        _ec2Service = ec2Service;
        _ssmService = ssmService;
        _s3Service = s3Service;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += async (_, _) => await RefreshInstancesAsync();

        _instancesView = CollectionViewSource.GetDefaultView(Instances);
        _instancesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Ec2InstanceItem.Region)));
    }

    private readonly ICollectionView _instancesView;
    public ICollectionView InstancesView => _instancesView;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _selectedInstanceCount;

    [ObservableProperty]
    private bool _isAutoRefreshEnabled = true;

    public ObservableCollection<Ec2InstanceItem> Instances { get; } = new();
    public ObservableCollection<Ec2InstanceItem> SelectedInstances { get; } = new();

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        if (value) _refreshTimer.Start();
        else _refreshTimer.Stop();
    }

    [RelayCommand]
    private async Task RefreshInstancesAsync()
    {
        var regions = _s3Service.KnownBucketRegions;
        if (regions.Count == 0)
        {
            ErrorMessage = "No S3 bucket regions known yet. Browse S3 buckets first.";
            return;
        }

        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var instances = await _ec2Service.DescribeInstancesAsync(regions);

            var selectedIds = SelectedInstances.Select(i => i.InstanceId).ToHashSet();
            Instances.Clear();
            SelectedInstances.Clear();

            foreach (var inst in instances.OrderBy(i => i.Region).ThenBy(i => i.NameTag))
            {
                Instances.Add(inst);
                if (selectedIds.Contains(inst.InstanceId))
                    SelectedInstances.Add(inst);
            }

            SelectedInstanceCount = SelectedInstances.Count;

            if (!_refreshTimer.IsEnabled && IsAutoRefreshEnabled)
                _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load instances: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task StartInstanceAsync(Ec2InstanceItem? instance)
    {
        if (instance is null) return;
        try
        {
            ErrorMessage = null;
            await _ec2Service.StartInstanceAsync(instance.InstanceId, instance.Region);
            await Task.Delay(1000);
            await RefreshInstancesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to start {instance.InstanceId}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopInstanceAsync(Ec2InstanceItem? instance)
    {
        if (instance is null) return;
        try
        {
            ErrorMessage = null;
            await _ec2Service.StopInstanceAsync(instance.InstanceId, instance.Region);
            await Task.Delay(1000);
            await RefreshInstancesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to stop {instance.InstanceId}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleSelect(Ec2InstanceItem? instance)
    {
        if (instance is null) return;

        if (SelectedInstances.Contains(instance))
            SelectedInstances.Remove(instance);
        else
            SelectedInstances.Add(instance);

        SelectedInstanceCount = SelectedInstances.Count;
    }

    public bool IsSelected(Ec2InstanceItem instance) => SelectedInstances.Contains(instance);

    [RelayCommand]
    private async Task RunSsmTestAsync(Ec2InstanceItem? instance)
    {
        if (instance is null) return;

        instance.SsmTestResult = "Running...";
        RefreshInstanceInView(instance);

        try
        {
            const string testCommand = "echo \"test\" > /tmp/batch-processor-test-$(date +%s).txt && echo \"SUCCESS\"";
            var commandId = await _ssmService.SendCommandAsync(instance.InstanceId, testCommand, instance.Region);

            JobStatus status;
            do
            {
                await Task.Delay(2000);
                status = await _ssmService.GetCommandStatusAsync(commandId, instance.InstanceId, instance.Region);
            }
            while (status is JobStatus.Pending or JobStatus.InProgress);

            if (status == JobStatus.Success)
            {
                var output = await _ssmService.GetCommandOutputAsync(commandId, instance.InstanceId, instance.Region);
                instance.SsmTestResult = $"OK: {output?.Trim()}";
            }
            else
            {
                instance.SsmTestResult = $"FAILED ({status})";
            }
        }
        catch (Exception ex)
        {
            instance.SsmTestResult = $"Error: {ex.Message}";
        }

        RefreshInstanceInView(instance);
    }

    private void RefreshInstanceInView(Ec2InstanceItem instance)
    {
        var idx = Instances.IndexOf(instance);
        if (idx >= 0)
        {
            Instances.RemoveAt(idx);
            Instances.Insert(idx, instance);
        }
    }
}
