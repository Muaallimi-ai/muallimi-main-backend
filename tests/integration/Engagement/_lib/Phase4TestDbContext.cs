using System;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Curriculum;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// Test-only <see cref="MuallimiDbContext"/> variant that strips the
/// pgvector-backed entities so the EF Core InMemory provider can model the
/// Phase 4 tables. Mirrors the Phase 3 factory — the rationale is the same:
/// <c>ContentChunk</c> and <c>QaCacheEntry</c> carry a <c>Pgvector.Vector</c>
/// property that InMemory cannot represent. Phase 4 integration tests never
/// reference those entities.
/// </summary>
public sealed class Phase4TestDbContext : MuallimiDbContext
{
    public Phase4TestDbContext(DbContextOptions<MuallimiDbContext> options)
        : base(options) { }

    public Phase4TestDbContext(
        DbContextOptions<MuallimiDbContext> options,
        IDbTenantContextAccessor accessor)
        : base(options, accessor) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Ignore<ContentChunk>();
        modelBuilder.Ignore<QaCacheEntry>();
    }
}

internal static class Phase4TestDbContextFactory
{
    public static MuallimiDbContext Create(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"phase4-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new Phase4TestDbContext(options);
    }

    public static DbContextOptions<MuallimiDbContext> BuildOptions(string databaseName)
    {
        return new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }
}
