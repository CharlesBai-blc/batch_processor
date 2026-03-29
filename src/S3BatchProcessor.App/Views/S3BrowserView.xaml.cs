using System.Windows.Controls;
using System.Windows.Input;
using S3BatchProcessor.App.Models;
using S3BatchProcessor.App.ViewModels;

namespace S3BatchProcessor.App.Views;

public partial class S3BrowserView : UserControl
{
    public S3BrowserView()
    {
        InitializeComponent();
    }

    private S3BrowserViewModel? ViewModel => DataContext as S3BrowserViewModel;

    private void BucketListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is S3BucketItem bucket)
        {
            ViewModel?.SelectBucketCommand.Execute(bucket);
        }
    }

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView)
        {
            if (listView.SelectedItems.Count == 1 && listView.SelectedItems[0] is S3ObjectItem item)
            {
                ViewModel?.PreviewFileCommand.Execute(item);
            }
        }
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView listView
            && listView.SelectedItem is S3ObjectItem item
            && item.IsFolder)
        {
            ViewModel?.NavigateToFolderCommand.Execute(item.Key);
        }
    }
}
