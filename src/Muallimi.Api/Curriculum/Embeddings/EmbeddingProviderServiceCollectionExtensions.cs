using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Curriculum.Embeddings;

public static class EmbeddingProviderServiceCollectionExtensions
{
    public static IServiceCollection AddEmbeddingProvider(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmbeddingOptions>(config.GetSection("Embedding"));

        services.AddHttpClient<VoyageEmbeddingProvider>();
        services.AddHttpClient<OpenAiEmbeddingProvider>();
        services.AddHttpClient<LocalEmbeddingProvider>();

        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmbeddingOptions>>().Value;
            return opts.Provider?.ToLowerInvariant() switch
            {
                "voyage" => sp.GetRequiredService<VoyageEmbeddingProvider>(),
                "openai" => sp.GetRequiredService<OpenAiEmbeddingProvider>(),
                "local" => sp.GetRequiredService<LocalEmbeddingProvider>(),
                _ => ActivatorUtilities.CreateInstance<NullEmbeddingProvider>(sp),
            };
        });

        return services;
    }
}
