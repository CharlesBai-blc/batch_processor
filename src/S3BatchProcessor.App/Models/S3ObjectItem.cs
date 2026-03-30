using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace S3BatchProcessor.App.Models;

public class S3ObjectItem : INotifyPropertyChanged
{
    private bool _isStaged;

    public string BucketName { get; set; } = string.Empty;
    public string BucketRegion { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsFolder { get; set; }
    public S3ItemType ItemType { get; set; }

    public bool IsStaged
    {
        get => _isStaged;
        set
        {
            if (_isStaged == value) return;
            _isStaged = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
