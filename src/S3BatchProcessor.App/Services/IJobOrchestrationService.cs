using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public interface IJobOrchestrationService
{
    Task<bool> EnsureInstancesRunningAsync(
        IEnumerable<JobAssignment> assignments,
        Action<string> onStatusUpdate,
        CancellationToken cancellationToken = default);

    Task ExecuteJobsAsync(
        IEnumerable<JobAssignment> assignments,
        string executablePath,
        string outputS3Prefix,
        string jobLogDirectory,
        string deploySource,
        Action<JobResult> onResultUpdated,
        CancellationToken cancellationToken = default);

    Task CancelAllAsync(CancellationToken cancellationToken = default);
}
