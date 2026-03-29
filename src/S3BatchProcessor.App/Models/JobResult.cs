namespace S3BatchProcessor.App.Models;

public class JobResult
{
    public S3ObjectItem File { get; set; } = null!;
    public Ec2InstanceItem Instance { get; set; } = null!;
    public JobStatus Status { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Output { get; set; }
}
