using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace S3BatchProcessor.App.Models;

public class JobResult : INotifyPropertyChanged
{
    private JobStatus _status;
    private string? _output;
    private DateTime? _startTime;
    private DateTime? _endTime;

    public S3ObjectItem File { get; set; } = null!;
    public Ec2InstanceItem Instance { get; set; } = null!;
    public string? CommandId { get; set; }

    public JobStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(Duration)); } }
    }

    public DateTime? StartTime
    {
        get => _startTime;
        set { if (_startTime != value) { _startTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(Duration)); } }
    }

    public DateTime? EndTime
    {
        get => _endTime;
        set { if (_endTime != value) { _endTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(Duration)); } }
    }

    public string? Output
    {
        get => _output;
        set { if (_output != value) { _output = value; OnPropertyChanged(); } }
    }

    public string Duration
    {
        get
        {
            if (StartTime is null) return "";
            var end = EndTime ?? DateTime.UtcNow;
            var span = end - StartTime.Value;
            return span.TotalSeconds < 60
                ? $"{span.TotalSeconds:0.0}s"
                : $"{span.TotalMinutes:0.0}m";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
