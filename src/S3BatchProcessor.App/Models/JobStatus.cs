namespace S3BatchProcessor.App.Models;

public enum JobStatus
{
    NotStarted,
    Pending,
    InProgress,
    Success,
    Failed,
    TimedOut
}
