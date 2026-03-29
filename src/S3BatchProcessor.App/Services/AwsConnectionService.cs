using Amazon;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace S3BatchProcessor.App.Services;

public class AwsConnectionService : IAwsConnectionService
{
    private readonly ILogger<AwsConnectionService> _logger;

    public AwsConnectionService(IConfiguration configuration, ILogger<AwsConnectionService> logger)
    {
        _logger = logger;

        var defaultRegion = configuration["Aws:DefaultRegion"];
        if (!string.IsNullOrEmpty(defaultRegion))
        {
            CurrentRegion = RegionEndpoint.GetBySystemName(defaultRegion);
        }
    }

    public string? AccountId { get; private set; }
    public string? UserArn { get; private set; }
    public string? ErrorMessage { get; private set; }
    public RegionEndpoint CurrentRegion { get; private set; } = RegionEndpoint.USEast1;
    public bool IsConnected { get; private set; }

    public event Action? RegionChanged;

    public async Task<bool> VerifyConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Verifying AWS credentials via STS GetCallerIdentity");

            using var stsClient = new AmazonSecurityTokenServiceClient(CurrentRegion);
            var response = await stsClient.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);

            AccountId = response.Account;
            UserArn = response.Arn;
            IsConnected = true;
            ErrorMessage = null;

            _logger.LogInformation("AWS connection verified: {Arn}", UserArn);
            return true;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            AccountId = null;
            UserArn = null;

            ErrorMessage = ex switch
            {
                Amazon.Runtime.AmazonServiceException { ErrorCode: "AccessDenied" }
                    => "Access denied. Check your AWS credentials.",
                Amazon.Runtime.AmazonServiceException
                    => $"AWS error: {ex.Message}",
                System.Net.Http.HttpRequestException
                    => "Cannot reach AWS. Check your internet connection.",
                _   => $"AWS credentials not found or expired. Run 'aws configure' to set up credentials."
            };

            _logger.LogError(ex, "AWS connection verification failed");
            return false;
        }
    }

    public void SetRegion(RegionEndpoint region)
    {
        if (CurrentRegion.SystemName == region.SystemName) return;

        _logger.LogInformation("Region changed from {Old} to {New}", CurrentRegion.SystemName, region.SystemName);
        CurrentRegion = region;
        RegionChanged?.Invoke();
    }
}
