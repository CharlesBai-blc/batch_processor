using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using S3BatchProcessor.App.Models;
using S3BatchProcessor.App.Services;

namespace S3BatchProcessor.App.ViewModels;

public partial class Ec2ManagerViewModel : ObservableObject
{
    private readonly IEc2Service _ec2Service;
    private readonly DispatcherTimer _refreshTimer;
    private readonly string[] _scanRegions;

    private static readonly string[] DefaultRegions =
    [
        "us-east-1", "us-east-2", "us-west-1", "us-west-2",
        "eu-west-1", "eu-west-2", "eu-central-1",
        "ap-southeast-1", "ap-southeast-2", "ap-northeast-1"
    ];

    private readonly HashSet<string> _spotLaunchedInstanceIds = new();

    public Ec2ManagerViewModel(IEc2Service ec2Service, IConfiguration configuration)
    {
        _ec2Service = ec2Service;

        var configRegions = configuration.GetSection("Aws:ScanRegions").Get<string[]>();
        _scanRegions = configRegions is { Length: > 0 } ? configRegions : DefaultRegions;

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

    [ObservableProperty]
    private string? _selectedSpotRegion;

    [ObservableProperty]
    private LaunchTemplateItem? _selectedLaunchTemplate;

    [ObservableProperty]
    private int _spotInstanceCount = 1;

    [ObservableProperty]
    private bool _isLaunchingSpot;

    [ObservableProperty]
    private string? _spotLaunchStatus;

    [ObservableProperty]
    private bool _isLoadingTemplates;

    public ObservableCollection<LaunchTemplateItem> LaunchTemplates { get; } = new();
    public string[] ScanRegions => _scanRegions;
    public IReadOnlySet<string> SpotLaunchedInstanceIds => _spotLaunchedInstanceIds;

    public event Action? ContinueRequested;

    public ObservableCollection<Ec2InstanceItem> Instances { get; } = new();
    public ObservableCollection<Ec2InstanceItem> SelectedInstances { get; } = new();

    private ICollectionView? _selectedInstancesView;
    public ICollectionView SelectedInstancesView
    {
        get
        {
            if (_selectedInstancesView is null)
            {
                _selectedInstancesView = CollectionViewSource.GetDefaultView(SelectedInstances);
                _selectedInstancesView.GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(Ec2InstanceItem.Region)));
            }
            return _selectedInstancesView;
        }
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        if (value) _refreshTimer.Start();
        else _refreshTimer.Stop();
    }

    partial void OnSelectedSpotRegionChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _ = LoadLaunchTemplatesAsync(value);
        else
        {
            LaunchTemplates.Clear();
            SelectedLaunchTemplate = null;
        }
    }

    [RelayCommand]
    private async Task RefreshInstancesAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var instances = await _ec2Service.DescribeInstancesAsync(_scanRegions);

            var plannedInstances = SelectedInstances.Where(i => i.IsPlanned).ToList();
            var selectedIds = SelectedInstances.Where(i => !i.IsPlanned).Select(i => i.InstanceId).ToHashSet();
            Instances.Clear();
            SelectedInstances.Clear();

            foreach (var inst in instances.OrderBy(i => i.Region).ThenBy(i => i.NameTag))
            {
                Instances.Add(inst);
                if (selectedIds.Contains(inst.InstanceId))
                    SelectedInstances.Add(inst);
            }

            // Re-tag instances launched by this session
            foreach (var inst in Instances)
            {
                if (_spotLaunchedInstanceIds.Contains(inst.InstanceId))
                    inst.IsSpotLaunched = true;
            }

            // Preserve planned (not-yet-launched) instances
            foreach (var planned in plannedInstances)
                SelectedInstances.Add(planned);

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

    private async Task LoadLaunchTemplatesAsync(string region)
    {
        try
        {
            IsLoadingTemplates = true;
            SpotLaunchStatus = null;
            LaunchTemplates.Clear();
            SelectedLaunchTemplate = null;

            var templates = await _ec2Service.DescribeLaunchTemplatesAsync(region);

            foreach (var t in templates.OrderBy(t => t.LaunchTemplateName))
                LaunchTemplates.Add(t);

            if (LaunchTemplates.Count == 0)
                SpotLaunchStatus = "No launch templates found in this region.";
        }
        catch (Exception ex)
        {
            SpotLaunchStatus = $"Failed to load templates: {ex.Message}";
        }
        finally
        {
            IsLoadingTemplates = false;
        }
    }

    [RelayCommand]
    private void LaunchSpotInstances()
    {
        if (SelectedLaunchTemplate is null || string.IsNullOrEmpty(SelectedSpotRegion))
            return;

        var templateName = SelectedLaunchTemplate.LaunchTemplateName;

        for (var i = 0; i < SpotInstanceCount; i++)
        {
            var placeholder = new Ec2InstanceItem
            {
                InstanceId = $"planned-{Guid.NewGuid():N}",
                Region = SelectedSpotRegion,
                State = Ec2InstanceState.Pending,
                IsSpot = true,
                IsSpotLaunched = true,
                IsPlanned = true,
                PlannedLaunchTemplateId = SelectedLaunchTemplate.LaunchTemplateId,
                InstanceType = templateName,
                Tags = new Dictionary<string, string>
                {
                    ["Name"] = $"Spot-Planned-{templateName}-{i + 1}"
                }
            };
            SelectedInstances.Add(placeholder);
        }

        SelectedInstanceCount = SelectedInstances.Count;
        SpotLaunchStatus = $"Added {SpotInstanceCount} planned spot instance(s). They will be launched when you click Run.";
    }

    public async Task TerminateSpotLaunchedInstancesAsync(IEnumerable<Ec2InstanceItem> instancesToTerminate)
    {
        var toTerminate = instancesToTerminate
            .Where(i => i.State is not Ec2InstanceState.Terminated && !i.IsPlanned)
            .ToList();

        if (toTerminate.Count == 0) return;

        var byRegion = toTerminate.GroupBy(i => i.Region);
        foreach (var group in byRegion)
        {
            try
            {
                await _ec2Service.TerminateInstancesAsync(
                    group.Select(i => i.InstanceId),
                    group.Key);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to terminate spot instances in {group.Key}: {ex.Message}";
            }
        }

        foreach (var id in toTerminate.Select(i => i.InstanceId))
            _spotLaunchedInstanceIds.Remove(id);

        await RefreshInstancesAsync();
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
    private void ClearSelected()
    {
        SelectedInstances.Clear();
        SelectedInstanceCount = 0;
    }

    [RelayCommand]
    private void Continue()
    {
        if (SelectedInstanceCount <= 0) return;
        ContinueRequested?.Invoke();
    }

}
