using Microsoft.Extensions.Logging;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public class JobOrchestrationService : IJobOrchestrationService
{
    private readonly ISsmService _ssmService;
    private readonly IEc2Service _ec2Service;
    private readonly ILogger<JobOrchestrationService> _logger;

    private readonly List<JobResult> _activeResults = new();
    private CancellationTokenSource? _linkedCts;

    private const int InstancePollIntervalMs = 5000;
    private const int InstanceStartTimeoutMs = 300_000; // 5 minutes

    public JobOrchestrationService(ISsmService ssmService, IEc2Service ec2Service, ILogger<JobOrchestrationService> logger)
    {
        _ssmService = ssmService;
        _ec2Service = ec2Service;
        _logger = logger;
    }

    public async Task<bool> EnsureInstancesRunningAsync(
        IEnumerable<JobAssignment> assignments,
        Action<string> onStatusUpdate,
        CancellationToken cancellationToken = default)
    {
        var instances = assignments.Select(a => a.Instance).Distinct().ToList();
        var notRunning = instances.Where(i => i.State != Ec2InstanceState.Running).ToList();

        if (notRunning.Count == 0)
        {
            onStatusUpdate("All instances are running.");
            return true;
        }

        var stoppedOrStopping = notRunning
            .Where(i => i.State is Ec2InstanceState.Stopped or Ec2InstanceState.Stopping)
            .ToList();

        var terminated = notRunning.Where(i => i.State == Ec2InstanceState.Terminated).ToList();
        if (terminated.Count > 0)
        {
            var names = string.Join(", ", terminated.Select(i => i.NameTag));
            onStatusUpdate($"ERROR: Terminated instances cannot be started: {names}");
            return false;
        }

        foreach (var inst in stoppedOrStopping.Where(i => i.State == Ec2InstanceState.Stopped))
        {
            onStatusUpdate($"Starting {inst.NameTag} ({inst.InstanceId})...");
            try
            {
                await _ec2Service.StartInstanceAsync(inst.InstanceId, inst.Region, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start {InstanceId}", inst.InstanceId);
                onStatusUpdate($"ERROR: Failed to start {inst.NameTag}: {ex.Message}");
                return false;
            }
        }

        var waitingFor = notRunning.Select(i => i.InstanceId).ToHashSet();
        var regionMap = notRunning.ToDictionary(i => i.InstanceId, i => i.Region);
        var regions = notRunning.Select(i => i.Region).Distinct().ToList();

        var elapsed = 0;
        while (waitingFor.Count > 0 && elapsed < InstanceStartTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            onStatusUpdate($"Waiting for {waitingFor.Count} instance(s) to reach Running state... ({elapsed / 1000}s)");
            await Task.Delay(InstancePollIntervalMs, cancellationToken);
            elapsed += InstancePollIntervalMs;

            try
            {
                var freshInstances = await _ec2Service.DescribeInstancesAsync(regions, cancellationToken);
                foreach (var fresh in freshInstances)
                {
                    if (!waitingFor.Contains(fresh.InstanceId)) continue;
                    if (fresh.State == Ec2InstanceState.Running)
                    {
                        waitingFor.Remove(fresh.InstanceId);
                        var original = instances.First(i => i.InstanceId == fresh.InstanceId);
                        original.State = Ec2InstanceState.Running;
                        onStatusUpdate($"  ✓ {original.NameTag} is now Running.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error polling instance states");
            }
        }

        if (waitingFor.Count > 0)
        {
            var names = string.Join(", ", notRunning.Where(i => waitingFor.Contains(i.InstanceId)).Select(i => i.NameTag));
            onStatusUpdate($"ERROR: Timed out waiting for instances to start: {names}");
            return false;
        }

        onStatusUpdate("All instances are running.");
        return true;
    }

    public async Task ExecuteJobsAsync(
        IEnumerable<JobAssignment> assignments,
        string executablePath,
        string outputS3Prefix,
        Action<JobResult> onResultUpdated,
        CancellationToken cancellationToken = default)
    {
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _linkedCts.Token;
        _activeResults.Clear();

        var sendTasks = new List<Task>();

        foreach (var assignment in assignments)
        {
            foreach (var file in assignment.Files)
            {
                var result = new JobResult
                {
                    File = file,
                    Instance = assignment.Instance,
                    Status = JobStatus.Pending,
                    StartTime = DateTime.UtcNow
                };
                _activeResults.Add(result);
                onResultUpdated(result);

                sendTasks.Add(SendSingleCommandAsync(result, assignment.Instance, file,
                    executablePath, outputS3Prefix, onResultUpdated, ct));
            }
        }

        await Task.WhenAll(sendTasks);

        await PollUntilAllCompleteAsync(onResultUpdated, ct);
    }

    private async Task SendSingleCommandAsync(
        JobResult result,
        Ec2InstanceItem instance,
        S3ObjectItem file,
        string executablePath,
        string outputS3Prefix,
        Action<JobResult> onResultUpdated,
        CancellationToken ct)
    {
        try
        {
            var script = BuildScript(executablePath, file, outputS3Prefix);
            _logger.LogInformation("Sending command for {File} to {Instance}",
                file.Name, instance.InstanceId);

            var commandId = await _ssmService.SendCommandAsync(
                instance.InstanceId, script, instance.Region, ct);

            result.CommandId = commandId;
            result.Status = JobStatus.InProgress;
            onResultUpdated(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send command for {File} to {Instance}",
                file.Name, instance.InstanceId);
            result.Status = JobStatus.Failed;
            result.EndTime = DateTime.UtcNow;
            result.Output = ex.Message;
            onResultUpdated(result);
        }
    }

    private async Task PollUntilAllCompleteAsync(Action<JobResult> onResultUpdated, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pending = _activeResults
                .Where(r => r.Status is JobStatus.Pending or JobStatus.InProgress && r.CommandId is not null)
                .ToList();

            if (pending.Count == 0) break;

            await Task.Delay(3000, ct);

            foreach (var result in pending)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var status = await _ssmService.GetCommandStatusAsync(
                        result.CommandId!, result.Instance.InstanceId, result.Instance.Region, ct);

                    if (status == result.Status) continue;

                    result.Status = status;

                    if (status is JobStatus.Success or JobStatus.Failed or JobStatus.TimedOut)
                    {
                        result.EndTime = DateTime.UtcNow;
                        var output = await _ssmService.GetCommandOutputAsync(
                            result.CommandId!, result.Instance.InstanceId, result.Instance.Region, ct);
                        result.Output = output;
                    }

                    onResultUpdated(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error polling status for command {CommandId}", result.CommandId);
                }
            }
        }
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        _linkedCts?.Cancel();

        var inFlight = _activeResults
            .Where(r => r.CommandId is not null && r.Status is JobStatus.Pending or JobStatus.InProgress)
            .ToList();

        foreach (var result in inFlight)
        {
            try
            {
                await _ssmService.CancelCommandAsync(result.CommandId!, result.Instance.Region, cancellationToken);
                result.Status = JobStatus.Failed;
                result.EndTime = DateTime.UtcNow;
                result.Output = "Cancelled by user";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel command {CommandId}", result.CommandId);
            }
        }
    }

    private static string BuildScript(string executablePath, S3ObjectItem file, string outputS3Prefix)
    {
        var inputPath = $"/data/{file.Name}";
        var outputPath = $"/data/output/{file.Name}.result";
        var s3Dest = outputS3Prefix.TrimEnd('/') + "/" + file.Name + ".result";

        return $"""
                #!/bin/bash
                set -e
                {executablePath} "{inputPath}"
                aws s3 cp "{outputPath}" "{s3Dest}"
                """;
    }
}
