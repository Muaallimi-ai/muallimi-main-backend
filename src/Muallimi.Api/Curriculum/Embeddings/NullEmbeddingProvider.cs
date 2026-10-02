using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Muallimi.Api.Curriculum.Embeddings;

public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    private readonly ILogger<NullEmbeddingProvider> _logger;

    public NullEmbeddingProvider(ILogger<NullEmbeddingProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderKey => "null";
    public string ModelName => "null";
    public int Dim => 0;

    public Task<EmbeddingResult?> EmbedAsync(string text, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "NullEmbeddingProvider skipped embed for {Chars} chars — configure Embedding:Provider to voyage or openai to actually embed.",
            text?.Length ?? 0);
        return Task.FromResult<EmbeddingResult?>(null);
    }
}
