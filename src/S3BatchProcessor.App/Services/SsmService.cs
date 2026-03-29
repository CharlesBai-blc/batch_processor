using Amazon;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Logging;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public class SsmService : ISsmService
{
    private readonly ILogger<SsmService> _logger;
    private readonly Dictionary<string, AmazonSimpleSystemsManagementClient> _regionalClients = new();

    public SsmService(ILogger<SsmService> logger)
    {
        _logger = logger;
    }

    private AmazonSimpleSystemsManagementClient GetClient(string region)
    {
        if (_regionalClients.TryGetValue(region, out var existing))
            return existing;

        var client = new AmazonSimpleSystemsManagementClient(RegionEndpoint.GetBySystemName(region));
        _regionalClients[region] = client;
        return client;
    }

    public async Task<string> SendCommandAsync(string instanceId, string command, string region, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Sending SSM command to {InstanceId} in {Region}", instanceId, region);

        var client = GetClient(region);
        var response = await client.SendCommandAsync(new SendCommandRequest
        {
            InstanceIds = [instanceId],
            DocumentName = "AWS-RunShellScript",
            Parameters = new Dictionary<string, List<string>>
            {
                ["commands"] = [command]
            },
            TimeoutSeconds = 60
        }, cancellationToken);

        var commandId = response.Command.CommandId;
        _logger.LogDebug("SSM command {CommandId} sent to {InstanceId}", commandId, instanceId);
        return commandId;
    }

    public async Task<JobStatus> GetCommandStatusAsync(string commandId, string instanceId, string region, CancellationToken cancellationToken = default)
    {
        var client = GetClient(region);

        try
        {
            var response = await client.GetCommandInvocationAsync(new GetCommandInvocationRequest
            {
                CommandId = commandId,
                InstanceId = instanceId
            }, cancellationToken);

            return MapStatus(response.StatusDetails);
        }
        catch (InvocationDoesNotExistException)
        {
            return JobStatus.Pending;
        }
    }

    public async Task<string?> GetCommandOutputAsync(string commandId, string instanceId, string region, CancellationToken cancellationToken = default)
    {
        var client = GetClient(region);

        var response = await client.GetCommandInvocationAsync(new GetCommandInvocationRequest
        {
            CommandId = commandId,
            InstanceId = instanceId
        }, cancellationToken);

        return response.StandardOutputContent;
    }

    public async Task CancelCommandAsync(string commandId, string region, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Cancelling SSM command {CommandId}", commandId);
        var client = GetClient(region);
        await client.CancelCommandAsync(new CancelCommandRequest
        {
            CommandId = commandId
        }, cancellationToken);
    }

    private static JobStatus MapStatus(string? statusDetails) => statusDetails switch
    {
        "Pending" => JobStatus.Pending,
        "InProgress" => JobStatus.InProgress,
        "Success" => JobStatus.Success,
        "Failed" => JobStatus.Failed,
        "TimedOut" => JobStatus.TimedOut,
        "Cancelled" => JobStatus.Failed,
        "Cancelling" => JobStatus.InProgress,
        _ => JobStatus.Pending
    };
}
