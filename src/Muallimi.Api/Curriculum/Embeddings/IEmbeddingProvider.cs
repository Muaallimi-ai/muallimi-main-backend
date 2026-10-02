using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Api.Curriculum.Embeddings;

public sealed record EmbeddingResult(
    float[] Vector,
    string ProviderKey,
    string ModelName,
    int Dim);

public interface IEmbeddingProvider
{
    string ProviderKey { get; }
    string ModelName { get; }
    int Dim { get; }

    Task<EmbeddingResult?> EmbedAsync(string text, CancellationToken ct = default);
}

public sealed class EmbeddingOptions
{
    public string Provider { get; set; } = "null";
    public VoyageOptions Voyage { get; set; } = new();
    public OpenAiOptions OpenAI { get; set; } = new();
    public LocalOptions Local { get; set; } = new();

    public sealed class VoyageOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = "voyage-3-large";
        public int Dim { get; set; } = 1024;
        public string InputType { get; set; } = "document";
        public string Endpoint { get; set; } = "https://api.voyageai.com/v1/embeddings";
    }

    public sealed class OpenAiOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = "text-embedding-3-large";
        public int Dim { get; set; } = 3072;
        public string Endpoint { get; set; } = "https://api.openai.com/v1/embeddings";
    }

    public sealed class LocalOptions
    {
        public string Model { get; set; } = "intfloat/multilingual-e5-small";
        public int Dim { get; set; } = 384;
        public string InputType { get; set; } = "document";
        public string Endpoint { get; set; } = "http://embedding-sidecar:8080/embeddings";
    }
}
