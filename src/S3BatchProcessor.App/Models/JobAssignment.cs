using System.Collections.ObjectModel;

namespace S3BatchProcessor.App.Models;

public class JobAssignment
{
    public Ec2InstanceItem Instance { get; set; } = null!;
    public ObservableCollection<S3ObjectItem> Files { get; set; } = new();
}
