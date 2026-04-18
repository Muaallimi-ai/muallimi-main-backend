using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Curriculum;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SaasOperations;

/// <summary>
/// Phase 6 US1 test DbContext — InMemory provider variant that drops the
/// pgvector-backed entities so billing + payment flows can run without
/// Postgres in the test process.
/// </summary>
public sealed class Phase6TestDbContext : MuallimiDbContext
{
    public Phase6TestDbContext(DbContextOptions<MuallimiDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Ignore<ContentChunk>();
        modelBuilder.Ignore<QaCacheEntry>();
    }
}

internal static class Phase6TestDbContextFactory
{
    public static MuallimiDbContext Create(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"phase6-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new Phase6TestDbContext(options);
    }
}
