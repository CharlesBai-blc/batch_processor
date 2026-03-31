using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public class JobOrchestrationService : IJobOrchestrationService
{
    private readonly ISsmService _ssmService;
    private readonly IEc2Service _ec2Service;
    private readonly IS3Service _s3Service;
    private readonly ILogger<JobOrchestrationService> _logger;
    private readonly string? _outputS3Prefix;
    private readonly string _jobBatNamePrefix;
    private readonly int _pollIntervalMs;

    private readonly List<JobResult> _activeResults = new();
    private CancellationTokenSource? _linkedCts;

    private string? _deployBucketRegion;
    private string? _outputBucketRegion;

    private const int InstancePollIntervalMs = 5000;
    private const int InstanceStartTimeoutMs = 300_000;

    public JobOrchestrationService(ISsmService ssmService, IEc2Service ec2Service, IS3Service s3Service, IConfiguration configuration, ILogger<JobOrchestrationService> logger)
    {
        _ssmService = ssmService;
        _ec2Service = ec2Service;
        _s3Service = s3Service;
        _logger = logger;
        _outputS3Prefix = configuration["Processing:OutputS3Prefix"];
        _jobBatNamePrefix = configuration["Processing:JobBatNamePrefix"] ?? "test_";

        var pollSeconds = 3;
        if (int.TryParse(configuration["Processing:PollIntervalSeconds"], out var parsed) && parsed > 0)
            pollSeconds = parsed;
        _pollIntervalMs = pollSeconds * 1000;
    }

    public async Task<bool> EnsureInstancesRunningAsync(
        IEnumerable<JobAssignment> assignments,
        Action<string> onStatusUpdate,
        CancellationToken cancellationToken = default)
    {
        var instances = assignments.Select(a => a.Instance).Distinct().ToList();

        // Launch any planned spot instances first
        var planned = instances.Where(i => i.IsPlanned).ToList();
        if (planned.Count > 0)
        {
            var byGroup = planned.GroupBy(i => (i.PlannedLaunchTemplateId!, i.Region));
            foreach (var group in byGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (templateId, region) = group.Key;
                var placeholders = group.ToList();

                onStatusUpdate($"Launching {placeholders.Count} spot instance(s) in {region}...");

                try
                {
                    var launched = await _ec2Service.LaunchSpotInstancesAsync(
                        templateId, placeholders.Count, region, cancellationToken);

                    for (var i = 0; i < Math.Min(launched.Count, placeholders.Count); i++)
                    {
                        var p = placeholders[i];
                        var real = launched[i];
                        p.InstanceId = real.InstanceId;
                        p.InstanceType = real.InstanceType;
                        p.State = real.State;
                        p.PublicIp = real.PublicIp;
                        p.LaunchTime = real.LaunchTime;
                        p.Tags = real.Tags;
                        p.IsPlanned = false;
                    }

                    onStatusUpdate($"Launched: {string.Join(", ", launched.Select(l => l.InstanceId))}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to launch spot instances in {Region}", region);
                    onStatusUpdate($"ERROR: Failed to launch spot instances in {region}: {ex.Message}");
                    return false;
                }
            }
        }

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

        onStatusUpdate("All instances are running. Waiting for SSM agent registration...");

        var ssmWaiting = notRunning.Select(i => i.InstanceId).ToHashSet();
        var ssmElapsed = 0;
        while (ssmWaiting.Count > 0 && ssmElapsed < InstanceStartTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var inst in notRunning.Where(i => ssmWaiting.Contains(i.InstanceId)).ToList())
            {
                var online = await _ssmService.IsInstanceOnlineAsync(inst.InstanceId, inst.Region, cancellationToken);
                if (online)
                {
                    ssmWaiting.Remove(inst.InstanceId);
                    onStatusUpdate($"  ✓ {inst.NameTag} SSM agent is online.");
                }
            }

            if (ssmWaiting.Count > 0)
            {
                onStatusUpdate($"Waiting for SSM agent on {ssmWaiting.Count} instance(s)... ({ssmElapsed / 1000}s)");
                await Task.Delay(InstancePollIntervalMs, cancellationToken);
                ssmElapsed += InstancePollIntervalMs;
            }
        }

        if (ssmWaiting.Count > 0)
        {
            var names = string.Join(", ", notRunning.Where(i => ssmWaiting.Contains(i.InstanceId)).Select(i => i.NameTag));
            onStatusUpdate($"ERROR: SSM agent did not come online for: {names}");
            return false;
        }

        onStatusUpdate("All instances ready for commands.");
        return true;
    }

    public async Task ExecuteJobsAsync(
        IEnumerable<JobAssignment> assignments,
        string executablePath,
        string outputS3Prefix,
        string jobLogDirectory,
        string deploySource,
        string commandArgs,
        string inputDirectory,
        Action<JobResult> onResultUpdated,
        CancellationToken cancellationToken = default)
    {
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _linkedCts.Token;
        _activeResults.Clear();

        await ResolveBucketRegionsAsync(deploySource, ct);

        var perInstance = new Dictionary<string, (Ec2InstanceItem Instance, List<(JobResult Result, S3ObjectItem File)> Files, string OutputPrefix)>();

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

                var id = assignment.Instance.InstanceId;
                if (!perInstance.ContainsKey(id))
                {
                    var prefix = !string.IsNullOrWhiteSpace(assignment.OutputS3Path)
                        ? assignment.OutputS3Path
                        : outputS3Prefix;
                    perInstance[id] = (assignment.Instance, new List<(JobResult, S3ObjectItem)>(), prefix);
                }
                perInstance[id].Files.Add((result, file));
            }
        }

        var instanceTasks = perInstance.Values
            .Select(g => SendBatchToInstanceAsync(
                g.Instance, g.Files,
                executablePath, g.OutputPrefix, jobLogDirectory, deploySource, commandArgs, inputDirectory,
                onResultUpdated, ct))
            .ToList();

        await Task.WhenAll(instanceTasks);

        await PollUntilAllCompleteAsync(onResultUpdated, ct);
    }

    private async Task SendBatchToInstanceAsync(
        Ec2InstanceItem instance,
        IList<(JobResult Result, S3ObjectItem File)> files,
        string executablePath,
        string outputS3Prefix,
        string jobLogDirectory,
        string deploySource,
        string commandArgs,
        string inputDirectory,
        Action<JobResult> onResultUpdated,
        CancellationToken ct)
    {
        try
        {
            var s3Files = files.Select(f => f.File).ToList();
            var script = BuildScript(executablePath, s3Files, outputS3Prefix, jobLogDirectory, deploySource, commandArgs, inputDirectory);

            _logger.LogInformation("Sending batch command ({FileCount} files) to {Instance}",
                files.Count, instance.InstanceId);

            var commandId = await _ssmService.SendCommandAsync(
                instance.InstanceId, script, instance.Region, ct);

            foreach (var (result, _) in files)
            {
                result.CommandId = commandId;
                result.Status = JobStatus.InProgress;
                onResultUpdated(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send batch command to {Instance}", instance.InstanceId);
            foreach (var (result, _) in files)
            {
                result.Status = JobStatus.Failed;
                result.EndTime = DateTime.UtcNow;
                result.Output = ex.Message;
                onResultUpdated(result);
            }
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

            await Task.Delay(_pollIntervalMs, ct);

            var commandGroups = pending
                .GroupBy(r => (r.CommandId!, r.Instance.InstanceId, r.Instance.Region))
                .ToList();

            foreach (var group in commandGroups)
            {
                if (ct.IsCancellationRequested) break;
                var (commandId, instanceId, region) = group.Key;

                try
                {
                    var overallStatus = await _ssmService.GetCommandStatusAsync(commandId, instanceId, region, ct);
                    var (stdout, stderr) = await _ssmService.GetCommandOutputAsync(commandId, instanceId, region, ct);

                    foreach (var result in group)
                    {
                        var fileName = result.File.Name;
                        var prevStatus = result.Status;

                        if (stdout is not null)
                        {
                            if (stdout.Contains($"FILE_DONE:{fileName}"))
                            {
                                result.Status = JobStatus.Success;
                                result.EndTime = DateTime.UtcNow;
                            }
                            else if (stdout.Contains($"FILE_FAILED:{fileName}"))
                            {
                                result.Status = JobStatus.Failed;
                                result.EndTime = DateTime.UtcNow;
                            }
                            else if (stdout.Contains($"FILE_START:{fileName}"))
                            {
                                result.Status = JobStatus.InProgress;
                            }
                        }

                        if (overallStatus is JobStatus.Failed or JobStatus.TimedOut or JobStatus.Success)
                        {
                            if (result.Status is JobStatus.Pending or JobStatus.InProgress)
                            {
                                result.Status = overallStatus == JobStatus.Success ? JobStatus.Success : JobStatus.Failed;
                                result.EndTime = DateTime.UtcNow;
                            }
                            result.Output = stdout;
                            result.ErrorOutput = stderr;
                        }

                        if (result.Status != prevStatus)
                            onResultUpdated(result);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error polling status for command {CommandId}", commandId);
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

        var cancelledCommands = new HashSet<string>();
        foreach (var result in inFlight)
        {
            try
            {
                if (cancelledCommands.Add(result.CommandId!))
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

    private string BuildScript(
        string executablePath,
        IList<S3ObjectItem> files,
        string outputS3Prefix,
        string jobLogDirectory,
        string deploySource,
        string commandArgs,
        string inputDirectory)
    {
        var (outputBucket, outputPrefix) = ParseS3Uri(outputS3Prefix);
        var deployRegion = _deployBucketRegion ?? "us-east-1";
        var outRegion = _outputBucketRegion ?? "us-east-1";

        var inputDir = inputDirectory.TrimEnd('\\');
        var outputDir = inputDir + @"\output";

        // Build the bat file content — contains ALL operations
        var bat = new List<string>
        {
            "@echo off",
            "",
            "REM === Directory setup ===",
            $"if not exist \"{inputDir}\" mkdir \"{inputDir}\"",
            $"if not exist \"{outputDir}\" mkdir \"{outputDir}\"",
            $"if not exist \"{jobLogDirectory}\" mkdir \"{jobLogDirectory}\"",
            "",
            "REM === Download binary ===",
            $"aws s3 cp \"{deploySource}\" \"{executablePath}\" --region {deployRegion}",
        };

        bat.Add("");
        bat.Add("REM === Download input files ===");
        foreach (var file in files)
        {
            var sourceRegion = !string.IsNullOrEmpty(file.BucketRegion) ? file.BucketRegion : "us-east-1";
            var localInput = inputDir + @"\" + file.Name;
            bat.Add($"aws s3 cp \"s3://{file.BucketName}/{file.Key}\" \"{localInput}\" --region {sourceRegion}");
        }

        bat.Add("");
        bat.Add("REM === Process files ===");
        foreach (var file in files)
        {
            var resolvedArgs = commandArgs.Replace("{filename}", inputDir + @"\" + file.Name);
            bat.Add($"echo FILE_START:{file.Name}");
            bat.Add($"\"{executablePath}\" {resolvedArgs}");
            bat.Add($"if %ERRORLEVEL% EQU 0 (echo FILE_DONE:{file.Name}) else (echo FILE_FAILED:{file.Name})");
        }

        bat.Add("");
        bat.Add("REM === Upload results ===");
        foreach (var file in files)
        {
            var localOutput = outputDir + @"\result_" + file.Name;
            var outputKey = outputPrefix + "result_" + file.Name;
            bat.Add($"aws s3 cp \"{localOutput}\" \"s3://{outputBucket}/{outputKey}\" --region {outRegion}");
            bat.Add($"if %ERRORLEVEL% NEQ 0 echo UPLOAD_FAILED:{file.Name}");
        }

        bat.Add("");
        bat.Add("REM === Upload this bat file as audit log ===");
        bat.Add($"aws s3 cp \"%~f0\" \"s3://{outputBucket}/{outputPrefix}{_jobBatNamePrefix}%TIMESTAMP%.bat\" --region {outRegion}");
        bat.Add("if %ERRORLEVEL% NEQ 0 echo BAT_UPLOAD_FAILED");

        var batContent = string.Join("\n", bat);

        // PowerShell wrapper: create job log dir, write bat, inject timestamp, run it
        var escapedBatDir = jobLogDirectory.Replace("'", "''");
        var ps = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"$jobBatDir = '{escapedBatDir}'",
            "New-Item -ItemType Directory -Force -Path $jobBatDir | Out-Null",
            "$ts = Get-Date -Format 'yyyyMMdd_HHmmss_fff'",
            $"$batPath = Join-Path $jobBatDir \"{_jobBatNamePrefix}$ts.bat\"",
            "",
            $"$batContent = @\"\n{batContent}\n\"@",
            "$batContent = $batContent.Replace('%TIMESTAMP%', $ts)",
            "Set-Content -Path $batPath -Value $batContent -Encoding ASCII",
            "",
            "cmd /c \"`\"$batPath`\"\"",
        };

        return string.Join("\n", ps);
    }

    private async Task ResolveBucketRegionsAsync(string deploySource, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrEmpty(deploySource))
            {
                var (deployBucket, _) = ParseS3Uri(deploySource);
                _deployBucketRegion = await _s3Service.GetBucketRegionAsync(deployBucket, ct);
            }

            if (!string.IsNullOrEmpty(_outputS3Prefix))
            {
                var (outputBucket, _) = ParseS3Uri(_outputS3Prefix);
                _outputBucketRegion = await _s3Service.GetBucketRegionAsync(outputBucket, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve bucket regions, using defaults");
        }
    }

    private static (string Bucket, string Key) ParseS3Uri(string s3Uri)
    {
        if (s3Uri.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            var withoutScheme = s3Uri[5..];
            var slashIdx = withoutScheme.IndexOf('/');
            if (slashIdx > 0)
                return (withoutScheme[..slashIdx], withoutScheme[(slashIdx + 1)..]);
            return (withoutScheme, string.Empty);
        }
        return (s3Uri, string.Empty);
    }
}
