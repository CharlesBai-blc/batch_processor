using Microsoft.Extensions.Logging;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public class JobOrchestrationService : IJobOrchestrationService
{
    private readonly ISsmService _ssmService;
    private readonly ILogger<JobOrchestrationService> _logger;

    public JobOrchestrationService(ISsmService ssmService, ILogger<JobOrchestrationService> logger)
    {
        _ssmService = ssmService;
        _logger = logger;
    }

    public Task ExecuteJobsAsync(IEnumerable<JobAssignment> assignments, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<IEnumerable<JobResult>> GetResultsAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
