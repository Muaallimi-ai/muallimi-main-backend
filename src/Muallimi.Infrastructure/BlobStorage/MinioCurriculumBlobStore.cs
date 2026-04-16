using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace Muallimi.Infrastructure.BlobStorage;

public sealed class MinioCurriculumBlobStore : ICurriculumBlobStore
{
    private readonly IMinioClient _client;
    private readonly ILogger<MinioCurriculumBlobStore> _logger;

    public MinioCurriculumBlobStore(
        IMinioClient client,
        IOptions<MinioOptions> options,
        ILogger<MinioCurriculumBlobStore> logger)
    {
        _client = client;
        _logger = logger;
        BucketName = options.Value.CurriculumBucket;
    }

    public string BucketName { get; }

    public async Task<string> UploadAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);

        var args = new PutObjectArgs()
            .WithBucket(BucketName)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(content.Length)
            .WithContentType(contentType);

        await _client.PutObjectAsync(args, ct);
        _logger.LogInformation("Uploaded {ObjectKey} to bucket {Bucket}", objectKey, BucketName);
        return objectKey;
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        var exists = await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(BucketName), ct);
        if (!exists)
        {
            await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(BucketName), ct);
            _logger.LogInformation("Created bucket {Bucket}", BucketName);
        }
    }
}

public sealed class MinioOptions
{
    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = "muallimi";
    public string SecretKey { get; set; } = "muallimi_local";
    public bool UseSsl { get; set; } = false;
    public string CurriculumBucket { get; set; } = "muallimi-curriculum-local";
}
