using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public interface IJobOrchestrationService
{
    Task ExecuteJobsAsync(IEnumerable<JobAssignment> assignments, CancellationToken cancellationToken = default);
    Task<IEnumerable<JobResult>> GetResultsAsync(CancellationToken cancellationToken = default);
    Task CancelAllAsync(CancellationToken cancellationToken = default);
}
