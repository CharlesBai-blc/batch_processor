using System.IO;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using S3BatchProcessor.App.Models;

namespace S3BatchProcessor.App.Services;

public class S3Service : IS3Service
{
    private readonly IAwsConnectionService _connectionService;
    private readonly ILogger<S3Service> _logger;
    private readonly int _maxPreviewBytes;
    private AmazonS3Client _s3Client;

    private readonly Dictionary<string, string> _bucketRegionCache = new();
    private readonly Dictionary<string, AmazonS3Client> _regionalClients = new();

    public IReadOnlyCollection<string> KnownBucketRegions =>
        _bucketRegionCache.Values.Distinct().ToList().AsReadOnly();

    public S3Service(IAwsConnectionService connectionService, IConfiguration configuration, ILogger<S3Service> logger)
    {
        _connectionService = connectionService;
        _logger = logger;
        _maxPreviewBytes = configuration.GetValue("Preview:MaxFileSizeBytes", 1_048_576);
        _s3Client = new AmazonS3Client(_connectionService.CurrentRegion);

        _connectionService.RegionChanged += OnRegionChanged;
    }

    private void OnRegionChanged()
    {
        _s3Client.Dispose();
        _s3Client = new AmazonS3Client(_connectionService.CurrentRegion);

        foreach (var client in _regionalClients.Values)
            client.Dispose();
        _regionalClients.Clear();
        _bucketRegionCache.Clear();

        _logger.LogDebug("S3 client recreated for region {Region}, caches cleared", _connectionService.CurrentRegion.SystemName);
    }

    public async Task<string> GetBucketRegionAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        if (_bucketRegionCache.TryGetValue(bucketName, out var cached))
            return cached;

        _logger.LogDebug("Looking up region for bucket {Bucket}", bucketName);

        var response = await _s3Client.GetBucketLocationAsync(
            new GetBucketLocationRequest { BucketName = bucketName },
            cancellationToken);

        var location = response.Location?.Value;
        var region = string.IsNullOrEmpty(location) ? "us-east-1" : location;

        _bucketRegionCache[bucketName] = region;
        _logger.LogDebug("Bucket {Bucket} is in region {Region}", bucketName, region);

        return region;
    }

    private async Task<AmazonS3Client> GetClientForBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        var region = await GetBucketRegionAsync(bucketName, cancellationToken);

        if (_regionalClients.TryGetValue(region, out var existing))
            return existing;

        var endpoint = RegionEndpoint.GetBySystemName(region);
        var client = new AmazonS3Client(endpoint);
        _regionalClients[region] = client;

        return client;
    }

    public async Task<List<S3BucketItem>> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing S3 buckets");

        var response = await _s3Client.ListBucketsAsync(cancellationToken);

        var buckets = (response.Buckets ?? [])
            .Select(b => new S3BucketItem
            {
                Name = b.BucketName,
                CreationDate = b.CreationDate ?? DateTime.MinValue
            })
            .OrderBy(b => b.Name)
            .ToList();

        var regionTasks = buckets.Select(async b =>
        {
            try
            {
                b.Region = await GetBucketRegionAsync(b.Name, cancellationToken);
            }
            catch
            {
                b.Region = "unknown";
            }
        });
        await Task.WhenAll(regionTasks);

        return buckets;
    }

    public async Task<List<S3ObjectItem>> ListObjectsAsync(string bucketName, string prefix, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing objects in {Bucket}/{Prefix}", bucketName, prefix);

        var client = await GetClientForBucketAsync(bucketName, cancellationToken);

        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = prefix,
            Delimiter = "/"
        };

        var items = new List<S3ObjectItem>();
        ListObjectsV2Response response;

        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);

            foreach (var commonPrefix in response.CommonPrefixes ?? [])
            {
                var folderName = commonPrefix.TrimEnd('/');
                if (folderName.Contains('/'))
                    folderName = folderName[(folderName.LastIndexOf('/') + 1)..];

                items.Add(new S3ObjectItem
                {
                    BucketName = bucketName,
                    Key = commonPrefix,
                    Name = folderName + "/",
                    IsFolder = true,
                    ItemType = S3ItemType.Folder
                });
            }

            foreach (var obj in response.S3Objects ?? [])
            {
                if (obj.Key == prefix) continue;

                var name = obj.Key;
                if (name.Contains('/'))
                    name = name[(name.LastIndexOf('/') + 1)..];

                if (string.IsNullOrEmpty(name)) continue;

                items.Add(new S3ObjectItem
                {
                    BucketName = bucketName,
                    Key = obj.Key,
                    Name = name,
                    Size = obj.Size ?? 0,
                    LastModified = obj.LastModified ?? DateTime.MinValue,
                    IsFolder = false,
                    ItemType = ClassifyFile(name)
                });
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return items
            .OrderByDescending(i => i.IsFolder)
            .ThenBy(i => i.Name)
            .ToList();
    }

    public async Task<string> GetObjectContentAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting object content for {Bucket}/{Key}", bucketName, key);

        var client = await GetClientForBucketAsync(bucketName, cancellationToken);
        var response = await client.GetObjectAsync(bucketName, key, cancellationToken);

        using var reader = new StreamReader(response.ResponseStream);
        var buffer = new char[_maxPreviewBytes];
        var memory = buffer.AsMemory();
        var charsRead = await reader.ReadBlockAsync(memory, cancellationToken);

        var content = new string(buffer, 0, charsRead);
        if (!reader.EndOfStream)
            content += "\n\n--- (preview truncated at 1 MB) ---";

        return content;
    }

    private static S3ItemType ClassifyFile(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".tif" or ".tiff" => S3ItemType.TiffFile,
            ".las" => S3ItemType.LasFile,
            ".txt" or ".log" or ".csv" or ".json" or ".xml" => S3ItemType.TextFile,
            _ => S3ItemType.OtherFile
        };
    }
}
