namespace S3BatchProcessor.App.Models;

public class JobAssignment
{
    public Ec2InstanceItem Instance { get; set; } = null!;
    public List<S3ObjectItem> Files { get; set; } = new();
}
