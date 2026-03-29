using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S3BatchProcessor.App.Models;
using S3BatchProcessor.App.Services;

namespace S3BatchProcessor.App.ViewModels;

public partial class S3BrowserViewModel : ObservableObject
{
    private readonly IS3Service _s3Service;
    private readonly IAwsConnectionService _connectionService;
    private CancellationTokenSource? _previewCts;
    private readonly HashSet<string> _stagedSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, S3ObjectItem> _stagedItemIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _committedSelectionKeys = new(StringComparer.Ordinal);
    public event Action? ContinueRequested;

    public S3BrowserViewModel(IS3Service s3Service, IAwsConnectionService connectionService)
    {
        _s3Service = s3Service;
        _connectionService = connectionService;

        _connectionService.RegionChanged += () => _ = LoadBucketsAsync();

        _itemsView = CollectionViewSource.GetDefaultView(Items);
        _itemsView.Filter = FilterItems;
    }

    private readonly ICollectionView _itemsView;
    public ICollectionView ItemsView => _itemsView;

    [ObservableProperty]
    private string? _currentBucket;

    [ObservableProperty]
    private string? _currentBucketRegion;

    [ObservableProperty]
    private string _currentPrefix = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _previewContent;

    [ObservableProperty]
    private string? _previewFileName;

    [ObservableProperty]
    private string? _previewFileSize;

    [ObservableProperty]
    private bool _filterTiffLasOnly;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _selectedFileCount;

    [ObservableProperty]
    private string _selectedSizeDisplay = string.Empty;

    [ObservableProperty]
    private int _totalItemCount;

    [ObservableProperty]
    private int _visibleItemCount;

    [ObservableProperty]
    private int _stagedSelectionCount;

    [ObservableProperty]
    private string _stagedSizeDisplay = string.Empty;

    private bool _isUpdatingCheckAll;
    private bool? _isAllChecked = false;
    public bool? IsAllChecked
    {
        get => _isAllChecked;
        set
        {
            if (_isAllChecked == value) return;
            _isAllChecked = value;
            OnPropertyChanged();

            if (_isUpdatingCheckAll) return;

            var target = value == true;
            _isUpdatingCheckAll = true;
            foreach (S3ObjectItem item in _itemsView)
            {
                if (item.IsFolder) continue;
                if (item.IsStaged == target) continue;
                item.IsStaged = target;
                ToggleItemChecked(item);
            }
            _isUpdatingCheckAll = false;
        }
    }

    public ObservableCollection<S3BucketItem> Buckets { get; } = new();

    private ICollectionView? _bucketsView;
    public ICollectionView BucketsView
    {
        get
        {
            if (_bucketsView is null)
            {
                _bucketsView = CollectionViewSource.GetDefaultView(Buckets);
                _bucketsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(S3BucketItem.Region)));
            }
            return _bucketsView;
        }
    }
    public ObservableCollection<S3ObjectItem> Items { get; } = new();
    public ObservableCollection<S3ObjectItem> StagedDisplayItems { get; } = new();
    public ObservableCollection<S3ObjectItem> CommittedSelections { get; } = new();

    private ICollectionView? _stagedDisplayView;
    public ICollectionView StagedDisplayView
    {
        get
        {
            if (_stagedDisplayView is null)
            {
                _stagedDisplayView = CollectionViewSource.GetDefaultView(StagedDisplayItems);
                _stagedDisplayView.GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(S3ObjectItem.BucketName)));
            }
            return _stagedDisplayView;
        }
    }

    private ICollectionView? _committedSelectionsView;
    public ICollectionView CommittedSelectionsView
    {
        get
        {
            if (_committedSelectionsView is null)
            {
                _committedSelectionsView = CollectionViewSource.GetDefaultView(CommittedSelections);
                _committedSelectionsView.GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(S3ObjectItem.BucketName)));
            }
            return _committedSelectionsView;
        }
    }
    public ObservableCollection<BreadcrumbSegment> BreadcrumbSegments { get; } = new();

    private readonly Stack<string> _navigationStack = new();

    partial void OnFilterTiffLasOnlyChanged(bool value)
    {
        _itemsView.Refresh();
        UpdateVisibleCount();
        RefreshCheckAllState();
    }

    private bool FilterItems(object obj)
    {
        if (!FilterTiffLasOnly) return true;
        if (obj is not S3ObjectItem item) return false;
        return item.IsFolder
            || item.ItemType == S3ItemType.TiffFile
            || item.ItemType == S3ItemType.LasFile;
    }

    private void UpdateVisibleCount()
    {
        var count = 0;
        foreach (var _ in _itemsView) count++;
        VisibleItemCount = count;
    }

    private void RefreshCheckAllState()
    {
        _isUpdatingCheckAll = true;
        int files = 0, staged = 0;
        foreach (S3ObjectItem item in _itemsView)
        {
            if (item.IsFolder) continue;
            files++;
            if (item.IsStaged) staged++;
        }

        if (files == 0) _isAllChecked = false;
        else if (staged == 0) _isAllChecked = false;
        else if (staged == files) _isAllChecked = true;
        else _isAllChecked = null;

        OnPropertyChanged(nameof(IsAllChecked));
        _isUpdatingCheckAll = false;
    }

    public bool HasStagedSelections => StagedSelectionCount > 0;

    [RelayCommand]
    private async Task LoadBucketsAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var buckets = await _s3Service.ListBucketsAsync();
            Buckets.Clear();
            foreach (var b in buckets) Buckets.Add(b);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to list buckets: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectBucketAsync(S3BucketItem? bucket)
    {
        if (bucket is null) return;

        CurrentBucket = bucket.Name;
        CurrentBucketRegion = bucket.Region;
        CurrentPrefix = string.Empty;
        _navigationStack.Clear();
        ClearPreview();
        UpdateBreadcrumb();

        await LoadObjectsAsync();
    }

    [RelayCommand]
    private async Task NavigateToFolderAsync(string? prefix)
    {
        if (prefix is null || CurrentBucket is null) return;

        _navigationStack.Push(CurrentPrefix);
        CurrentPrefix = prefix;
        UpdateBreadcrumb();
        ClearPreview();

        await LoadObjectsAsync();
    }

    [RelayCommand]
    private async Task NavigateBackAsync()
    {
        if (_navigationStack.Count == 0 || CurrentBucket is null) return;

        CurrentPrefix = _navigationStack.Pop();
        UpdateBreadcrumb();
        ClearPreview();

        await LoadObjectsAsync();
    }

    [RelayCommand]
    private async Task NavigateToBreadcrumbAsync(string prefix)
    {
        if (CurrentBucket is null) return;

        while (_navigationStack.Count > 0 && _navigationStack.Peek() != prefix)
            _navigationStack.Pop();

        if (_navigationStack.Count > 0)
            _navigationStack.Pop();

        _navigationStack.Push(CurrentPrefix);
        CurrentPrefix = prefix;
        UpdateBreadcrumb();
        ClearPreview();

        await LoadObjectsAsync();
    }

    [RelayCommand]
    private async Task PreviewFileAsync(S3ObjectItem? item)
    {
        if (item is null || item.IsFolder || CurrentBucket is null)
        {
            ClearPreview();
            return;
        }

        PreviewFileName = item.Name;
        PreviewFileSize = FormatSize(item.Size);

        var isTextFile = item.ItemType == S3ItemType.TextFile;
        if (!isTextFile)
        {
            PreviewContent = "(Binary file — no preview available)";
            return;
        }

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        try
        {
            PreviewContent = "Loading preview...";
            var content = await _s3Service.GetObjectContentAsync(CurrentBucket, item.Key, token);
            if (!token.IsCancellationRequested)
                PreviewContent = content;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                PreviewContent = $"Error loading preview: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleItemChecked(S3ObjectItem? item)
    {
        if (item is null || item.IsFolder) return;

        var key = BuildSelectionKey(item);
        if (item.IsStaged)
        {
            _stagedSelections.Add(key);
            _stagedItemIndex[key] = CloneItem(item);
        }
        else
        {
            _stagedSelections.Remove(key);
            _stagedItemIndex.Remove(key);
        }

        StagedSelectionCount = _stagedSelections.Count;
        OnPropertyChanged(nameof(HasStagedSelections));
        if (!_isUpdatingCheckAll) RefreshCheckAllState();
        SyncStagedDisplay();
    }

    [RelayCommand]
    private void SelectForProcessing()
    {
        foreach (var pair in _stagedItemIndex)
        {
            if (_committedSelectionKeys.Add(pair.Key))
            {
                CommittedSelections.Add(CloneItem(pair.Value));
            }
        }

        _stagedSelections.Clear();
        _stagedItemIndex.Clear();
        StagedSelectionCount = 0;
        OnPropertyChanged(nameof(HasStagedSelections));

        foreach (var item in Items)
            item.IsStaged = false;

        SyncStagedDisplay();
        RefreshCheckAllState();
        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void RemoveStagedItem(S3ObjectItem? item)
    {
        if (item is null) return;

        var key = BuildSelectionKey(item);
        _stagedSelections.Remove(key);
        _stagedItemIndex.Remove(key);

        foreach (var viewItem in Items)
        {
            if (!viewItem.IsFolder && BuildSelectionKey(viewItem) == key)
            {
                viewItem.IsStaged = false;
                break;
            }
        }

        StagedSelectionCount = _stagedSelections.Count;
        OnPropertyChanged(nameof(HasStagedSelections));
        SyncStagedDisplay();
        RefreshCheckAllState();
    }

    [RelayCommand]
    private void RemoveCommittedItem(S3ObjectItem? item)
    {
        if (item is null) return;

        var key = BuildSelectionKey(item);
        _committedSelectionKeys.Remove(key);
        CommittedSelections.Remove(item);
        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void ClearStaged()
    {
        _stagedSelections.Clear();
        _stagedItemIndex.Clear();
        StagedSelectionCount = 0;
        OnPropertyChanged(nameof(HasStagedSelections));

        foreach (var item in Items)
            item.IsStaged = false;

        SyncStagedDisplay();
        RefreshCheckAllState();
    }

    [RelayCommand]
    private void ClearCommitted()
    {
        CommittedSelections.Clear();
        _committedSelectionKeys.Clear();
        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void Continue()
    {
        if (SelectedFileCount <= 0) return;
        ContinueRequested?.Invoke();
    }


    private async Task LoadObjectsAsync()
    {
        if (CurrentBucket is null) return;

        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var objects = await _s3Service.ListObjectsAsync(CurrentBucket, CurrentPrefix);
            Items.Clear();
            foreach (var obj in objects)
            {
                obj.IsStaged = _stagedSelections.Contains(BuildSelectionKey(obj));
                Items.Add(obj);
            }

            TotalItemCount = Items.Count;
            _itemsView.Refresh();
            UpdateVisibleCount();
            RefreshCheckAllState();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to list objects: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateBreadcrumb()
    {
        BreadcrumbSegments.Clear();
        BreadcrumbSegments.Add(new BreadcrumbSegment { Label = CurrentBucket ?? "", Prefix = "" });

        if (string.IsNullOrEmpty(CurrentPrefix)) return;

        var parts = CurrentPrefix.TrimEnd('/').Split('/');
        var accumulated = "";
        foreach (var part in parts)
        {
            accumulated += part + "/";
            BreadcrumbSegments.Add(new BreadcrumbSegment { Label = part, Prefix = accumulated });
        }
    }

    private void SyncStagedDisplay()
    {
        StagedDisplayItems.Clear();
        foreach (var item in _stagedItemIndex.Values)
            StagedDisplayItems.Add(item);

        StagedSizeDisplay = FormatSize(_stagedItemIndex.Values.Sum(i => i.Size));
    }

    private void UpdateSelectionSummary()
    {
        SelectedFileCount = CommittedSelections.Count;
        var totalBytes = CommittedSelections.Sum(i => i.Size);
        SelectedSizeDisplay = FormatSize(totalBytes);
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        PreviewContent = null;
        PreviewFileName = null;
        PreviewFileSize = null;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < units.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {units[order]}";
    }

    private static string BuildSelectionKey(S3ObjectItem item)
        => $"{item.BucketName}/{item.Key}";

    private static S3ObjectItem CloneItem(S3ObjectItem item) => new()
    {
        BucketName = item.BucketName,
        Key = item.Key,
        Name = item.Name,
        Size = item.Size,
        LastModified = item.LastModified,
        IsFolder = item.IsFolder,
        ItemType = item.ItemType,
        IsStaged = item.IsStaged
    };
}

public class BreadcrumbSegment
{
    public string Label { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
}
