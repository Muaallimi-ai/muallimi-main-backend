namespace Muallimi.Infrastructure.BlobStorage;

/// <summary>
/// Writes curriculum source files to the shared blob store (MinIO locally,
/// Azure Blob in production) so the ingestion worker can pull them from the
/// same logical bucket.
/// </summary>
public interface ICurriculumBlobStore
{
    /// <summary>
    /// Upload a stream to the curriculum bucket.
    /// Returns the object key (relative to the bucket).
    /// </summary>
    Task<string> UploadAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Best-effort delete of a previously uploaded object.
    /// Does NOT throw if the object is missing.
    /// </summary>
    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    string BucketName { get; }
}
