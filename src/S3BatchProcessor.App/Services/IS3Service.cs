using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public interface IS3Service
{
    IReadOnlyCollection<string> KnownBucketRegions { get; }
    Task<List<S3BucketItem>> ListBucketsAsync(CancellationToken cancellationToken = default);
    Task<List<S3ObjectItem>> ListObjectsAsync(string bucketName, string prefix, CancellationToken cancellationToken = default);
    Task<string> GetObjectContentAsync(string bucketName, string key, CancellationToken cancellationToken = default);
    Task<string> GetBucketRegionAsync(string bucketName, CancellationToken cancellationToken = default);
}
