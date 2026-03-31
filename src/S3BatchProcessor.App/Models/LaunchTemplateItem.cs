namespace S3BatchProcessor.App.Models;

public class LaunchTemplateItem
{
    public string LaunchTemplateId { get; set; } = string.Empty;
    public string LaunchTemplateName { get; set; } = string.Empty;
    public long DefaultVersionNumber { get; set; }
    public DateTime? CreateTime { get; set; }

    public string DisplayName => $"{LaunchTemplateName} ({LaunchTemplateId})";
}
