using System;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Curriculum;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience;

/// <summary>
/// Test-only factory for an in-memory <see cref="MuallimiDbContext"/> that
/// strips the pgvector-backed entities.
///
/// Why this exists: <see cref="ContentChunk"/> and QaCacheEntry both carry a
/// <c>Pgvector.Vector</c> property which the EF Core InMemory provider
/// cannot model (see the <c>Vector</c> PropertyNotMappedException). Phase 3
/// polish tests never touch Phase 1 content chunks — they exercise the
/// student session, outbox, and tenancy surfaces — so a test-only
/// subclass that <c>Ignore()</c>s those two entities is enough to make
/// InMemory work for every Phase 3 query filter scenario.
///
/// Production code is unaffected: <see cref="MuallimiDbContext"/> keeps the
/// pgvector configuration intact for the real Npgsql+pgvector runtime.
/// </summary>
public sealed class Phase3TestDbContext : MuallimiDbContext
{
    public Phase3TestDbContext(DbContextOptions<MuallimiDbContext> options)
        : base(options) { }

    public Phase3TestDbContext(
        DbContextOptions<MuallimiDbContext> options,
        IDbTenantContextAccessor tenantContextAccessor)
        : base(options, tenantContextAccessor) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // The two entities that carry a Pgvector.Vector property — skip
        // them entirely so InMemory model validation succeeds. Phase 3
        // tests never reference these entities.
        modelBuilder.Ignore<ContentChunk>();
        modelBuilder.Ignore<QaCacheEntry>();
    }
}

internal static class Phase3TestDbContextFactory
{
    public static MuallimiDbContext Create(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"phase3-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new Phase3TestDbContext(options);
    }

    public static MuallimiDbContext Create(IDbTenantContextAccessor accessor, string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"phase3-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new Phase3TestDbContext(options, accessor);
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
