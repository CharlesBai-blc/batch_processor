using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Microsoft.Extensions.Logging;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public class Ec2Service : IEc2Service
{
    private readonly ILogger<Ec2Service> _logger;
    private readonly Dictionary<string, AmazonEC2Client> _regionalClients = new();

    public Ec2Service(ILogger<Ec2Service> logger)
    {
        _logger = logger;
    }

    private AmazonEC2Client GetClient(string region)
    {
        if (_regionalClients.TryGetValue(region, out var existing))
            return existing;

        var client = new AmazonEC2Client(RegionEndpoint.GetBySystemName(region));
        _regionalClients[region] = client;
        return client;
    }

    public async Task<List<Ec2InstanceItem>> DescribeInstancesAsync(IEnumerable<string> regions, CancellationToken cancellationToken = default)
    {
        var regionList = regions.ToList();
        _logger.LogDebug("Describing EC2 instances across {Count} regions", regionList.Count);

        var tasks = regionList.Select(region => DescribeInstancesInRegionAsync(region, cancellationToken));
        var results = await Task.WhenAll(tasks);

        return results.SelectMany(r => r).ToList();
    }

    private async Task<List<Ec2InstanceItem>> DescribeInstancesInRegionAsync(string region, CancellationToken cancellationToken)
    {
        try
        {
            var client = GetClient(region);
            var request = new DescribeInstancesRequest();
            var items = new List<Ec2InstanceItem>();

            DescribeInstancesResponse response;
            do
            {
                response = await client.DescribeInstancesAsync(request, cancellationToken);

                foreach (var reservation in response.Reservations ?? [])
                {
                    foreach (var instance in reservation.Instances ?? [])
                    {
                        var state = MapState(instance.State?.Name?.Value);
                        if (state == Ec2InstanceState.Terminated) continue;

                        var tags = new Dictionary<string, string>();
                        foreach (var tag in instance.Tags ?? [])
                            tags[tag.Key] = tag.Value;

                        items.Add(new Ec2InstanceItem
                        {
                            InstanceId = instance.InstanceId,
                            InstanceType = instance.InstanceType?.Value ?? "unknown",
                            State = state,
                            AvailabilityZone = instance.Placement?.AvailabilityZone ?? region,
                            Region = region,
                            Tags = tags,
                            PublicIp = instance.PublicIpAddress,
                            LaunchTime = instance.LaunchTime
                        });
                    }
                }

                request.NextToken = response.NextToken;
            }
            while (!string.IsNullOrEmpty(response.NextToken));

            _logger.LogDebug("Found {Count} instances in {Region}", items.Count, region);
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to describe instances in {Region}", region);
            return [];
        }
    }

    public async Task StartInstanceAsync(string instanceId, string region, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting instance {InstanceId} in {Region}", instanceId, region);
        var client = GetClient(region);
        await client.StartInstancesAsync(new StartInstancesRequest
        {
            InstanceIds = [instanceId]
        }, cancellationToken);
    }

    public async Task StopInstanceAsync(string instanceId, string region, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping instance {InstanceId} in {Region}", instanceId, region);
        var client = GetClient(region);
        await client.StopInstancesAsync(new StopInstancesRequest
        {
            InstanceIds = [instanceId]
        }, cancellationToken);
    }

    private static Ec2InstanceState MapState(string? stateName) => stateName switch
    {
        "pending" => Ec2InstanceState.Pending,
        "running" => Ec2InstanceState.Running,
        "shutting-down" => Ec2InstanceState.Stopping,
        "stopping" => Ec2InstanceState.Stopping,
        "stopped" => Ec2InstanceState.Stopped,
        "terminated" => Ec2InstanceState.Terminated,
        _ => Ec2InstanceState.Stopped
    };
}
