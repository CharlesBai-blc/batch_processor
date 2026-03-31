using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public interface IEc2Service
{
    Task<List<Ec2InstanceItem>> DescribeInstancesAsync(IEnumerable<string> regions, CancellationToken cancellationToken = default);
    Task StartInstanceAsync(string instanceId, string region, CancellationToken cancellationToken = default);
    Task StopInstanceAsync(string instanceId, string region, CancellationToken cancellationToken = default);
    Task<List<LaunchTemplateItem>> DescribeLaunchTemplatesAsync(string region, CancellationToken ct = default);
    Task<List<Ec2InstanceItem>> LaunchSpotInstancesAsync(string launchTemplateId, int count, string region, CancellationToken ct = default);
    Task TerminateInstancesAsync(IEnumerable<string> instanceIds, string region, CancellationToken ct = default);
}
