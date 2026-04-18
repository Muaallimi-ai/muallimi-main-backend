using System;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Curriculum;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// Test-only <see cref="MuallimiDbContext"/> variant that strips the
/// pgvector-backed entities so the EF Core InMemory provider can model the
/// Phase 5 tables. Mirrors the Phase 3 / Phase 4 factories.
/// </summary>
public sealed class Phase5TestDbContext : MuallimiDbContext
{
    public Phase5TestDbContext(DbContextOptions<MuallimiDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Ignore<ContentChunk>();
        modelBuilder.Ignore<QaCacheEntry>();
    }
}

internal static class Phase5TestDbContextFactory
{
    public static MuallimiDbContext Create(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"phase5-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new Phase5TestDbContext(options);
    }
}
