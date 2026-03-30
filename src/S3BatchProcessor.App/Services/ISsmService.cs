using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public interface ISsmService
{
    Task<string> SendCommandAsync(string instanceId, string command, string region, CancellationToken cancellationToken = default);
    Task<JobStatus> GetCommandStatusAsync(string commandId, string instanceId, string region, CancellationToken cancellationToken = default);
    Task<(string? Stdout, string? Stderr)> GetCommandOutputAsync(string commandId, string instanceId, string region, CancellationToken cancellationToken = default);
    Task CancelCommandAsync(string commandId, string region, CancellationToken cancellationToken = default);
    Task<bool> IsInstanceOnlineAsync(string instanceId, string region, CancellationToken cancellationToken = default);
}
