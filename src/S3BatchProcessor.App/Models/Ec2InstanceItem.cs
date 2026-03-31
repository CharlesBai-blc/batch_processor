namespace S3BatchProcessor.App.Models;

public class Ec2InstanceItem
{
    public string InstanceId { get; set; } = string.Empty;
    public string InstanceType { get; set; } = string.Empty;
    public Ec2InstanceState State { get; set; }
    public string AvailabilityZone { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public Dictionary<string, string> Tags { get; set; } = new();
    public string? PublicIp { get; set; }
    public DateTime? LaunchTime { get; set; }
    public string? SsmTestResult { get; set; }
    public bool IsSpot { get; set; }
    public bool IsSpotLaunched { get; set; }
    public bool IsPlanned { get; set; }
    public string? PlannedLaunchTemplateId { get; set; }

    public string NameTag => Tags.TryGetValue("Name", out var n) ? n : InstanceId;
    public string LifecycleLabel => IsSpot ? "Spot" : "On-Demand";
}
