namespace S3BatchProcessor.App.Models;

public class S3BucketItem
{
    public string Name { get; set; } = string.Empty;
    public DateTime CreationDate { get; set; }
    public string Region { get; set; } = string.Empty;
}
