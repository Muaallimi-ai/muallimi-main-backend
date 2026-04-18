using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.SchoolManagement.RosterImport;

/// <summary>
/// T054 / T057 support — abstraction over roster file storage. The
/// production adapter lands on MinIO (local) / S3 (cloud); tests and the
/// local parity walkthrough use the in-memory implementation below so
/// roster imports work end-to-end with zero managed-cloud dependencies.
/// </summary>
public interface IRosterFileStore
{
    Task WriteAsync(string blobKey, byte[] content, CancellationToken ct = default);

    Task<Stream> OpenAsync(string blobKey, CancellationToken ct = default);

    Task<byte[]?> TryReadAsync(string blobKey, CancellationToken ct = default);
}

public sealed class InMemoryRosterFileStore : IRosterFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    public Task WriteAsync(string blobKey, byte[] content, CancellationToken ct = default)
    {
        _store[blobKey] = content;
        return Task.CompletedTask;
    }

    public Task<Stream> OpenAsync(string blobKey, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(blobKey, out var bytes))
            throw new FileNotFoundException($"roster_blob_not_found:{blobKey}");
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task<byte[]?> TryReadAsync(string blobKey, CancellationToken ct = default)
    {
        return Task.FromResult(_store.TryGetValue(blobKey, out var bytes) ? bytes : null);
    }
}

public static class RosterFileStoreServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5RosterFileStore(this IServiceCollection services)
    {
        services.AddSingleton<IRosterFileStore, InMemoryRosterFileStore>();
        return services;
    }
}
