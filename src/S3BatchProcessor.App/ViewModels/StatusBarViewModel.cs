using System.Collections.ObjectModel;
using Amazon;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S3BatchProcessor.App.Services;

namespace S3BatchProcessor.App.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    private readonly IAwsConnectionService _connectionService;

    public StatusBarViewModel(IAwsConnectionService connectionService)
    {
        _connectionService = connectionService;
        _selectedRegion = connectionService.CurrentRegion;
    }

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _identityDisplay = "Connecting...";

    [ObservableProperty]
    private string _regionDisplay = "us-east-1";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private RegionEndpoint _selectedRegion;

    public ObservableCollection<RegionEndpoint> AvailableRegions { get; } = new(new[]
    {
        RegionEndpoint.USEast1,
        RegionEndpoint.USEast2,
        RegionEndpoint.USWest1,
        RegionEndpoint.USWest2,
        RegionEndpoint.EUWest1,
        RegionEndpoint.EUWest2,
        RegionEndpoint.EUCentral1,
        RegionEndpoint.APSoutheast1,
        RegionEndpoint.APSoutheast2,
        RegionEndpoint.APNortheast1,
    });

    public async Task InitializeAsync()
    {
        await VerifyAndUpdateAsync();
    }

    partial void OnSelectedRegionChanged(RegionEndpoint value)
    {
        _connectionService.SetRegion(value);
        RegionDisplay = value.SystemName;
        _ = VerifyAndUpdateAsync();
    }

    [RelayCommand]
    private async Task VerifyAndUpdateAsync()
    {
        IdentityDisplay = "Connecting...";
        ErrorMessage = null;

        var success = await _connectionService.VerifyConnectionAsync();

        IsConnected = success;
        if (success)
        {
            IdentityDisplay = _connectionService.UserArn ?? "Connected";
            ErrorMessage = null;
        }
        else
        {
            IdentityDisplay = "Not connected";
            ErrorMessage = _connectionService.ErrorMessage;
        }

        RegionDisplay = _connectionService.CurrentRegion.SystemName;
    }
}
