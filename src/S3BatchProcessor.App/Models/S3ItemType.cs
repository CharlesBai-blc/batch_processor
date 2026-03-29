namespace S3BatchProcessor.App.Models;

public enum S3ItemType
{
    Bucket,
    Folder,
    TiffFile,
    LasFile,
    TextFile,
    OtherFile
}
