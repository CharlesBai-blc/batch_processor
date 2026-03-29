using Amazon;

namespace S3BatchProcessor.App.Services;

public interface IAwsConnectionService
{
    string? AccountId { get; }
    string? UserArn { get; }
    string? ErrorMessage { get; }
    RegionEndpoint CurrentRegion { get; }
    bool IsConnected { get; }

    event Action? RegionChanged;

    Task<bool> VerifyConnectionAsync(CancellationToken cancellationToken = default);
    void SetRegion(RegionEndpoint region);
}
