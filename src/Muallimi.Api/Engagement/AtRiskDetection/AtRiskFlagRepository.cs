using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.AtRiskDetection;

/// <summary>
/// T143 (US8) — <see cref="AtRiskFlag"/> repository.
///
/// Flags are tenant + student scoped. Only one row per student is "active"
/// at a time (<c>ClearedAt == null</c>). Recovery is computed in
/// <see cref="AtRiskEvaluator"/> and lands here as
/// <see cref="ClearAsync"/>; manual clear is not exposed to parents per
/// the contract invariant.
/// </summary>
public interface IAtRiskFlagRepository
{
    Task<AtRiskFlag?> GetActiveAsync(Guid tenantId, Guid studentId, CancellationToken ct = default);

    Task<AtRiskFlag?> GetByIdAsync(Guid tenantId, Guid atRiskFlagId, CancellationToken ct = default);

    Task<IReadOnlyList<AtRiskFlag>> ListForStudentAsync(Guid tenantId, Guid studentId, CancellationToken ct = default);

    Task AddAsync(AtRiskFlag flag, CancellationToken ct = default);

    Task ClearAsync(AtRiskFlag flag, DateTime clearedAtUtc, CancellationToken ct = default);

    Task LinkInterventionPromptAsync(AtRiskFlag flag, Guid interventionPromptId, CancellationToken ct = default);

    Task AcknowledgeAsync(AtRiskFlag flag, Guid parentProfileId, DateTime acknowledgedAtUtc, CancellationToken ct = default);
}

public sealed class AtRiskFlagRepository : IAtRiskFlagRepository
{
    private readonly MuallimiDbContext _db;

    public AtRiskFlagRepository(MuallimiDbContext db) => _db = db;

    public Task<AtRiskFlag?> GetActiveAsync(Guid tenantId, Guid studentId, CancellationToken ct = default)
    {
        return _db.AtRiskFlags
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.StudentId == studentId && f.ClearedAt == null)
            .OrderByDescending(f => f.RaisedAt)
            .FirstOrDefaultAsync(ct);
    }

    public Task<AtRiskFlag?> GetByIdAsync(Guid tenantId, Guid atRiskFlagId, CancellationToken ct = default)
    {
        return _db.AtRiskFlags
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.AtRiskFlagId == atRiskFlagId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<AtRiskFlag>> ListForStudentAsync(Guid tenantId, Guid studentId, CancellationToken ct = default)
    {
        return await _db.AtRiskFlags
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.StudentId == studentId)
            .OrderByDescending(f => f.RaisedAt)
            .ToListAsync(ct);
    }

    public Task AddAsync(AtRiskFlag flag, CancellationToken ct = default)
    {
        _db.AtRiskFlags.Add(flag);
        return Task.CompletedTask;
    }

    public Task ClearAsync(AtRiskFlag flag, DateTime clearedAtUtc, CancellationToken ct = default)
    {
        flag.ClearedAt = DateTime.SpecifyKind(clearedAtUtc, DateTimeKind.Utc);
        _db.AtRiskFlags.Update(flag);
        return Task.CompletedTask;
    }

    public Task LinkInterventionPromptAsync(AtRiskFlag flag, Guid interventionPromptId, CancellationToken ct = default)
    {
        flag.LinkedInterventionPromptId = interventionPromptId;
        _db.AtRiskFlags.Update(flag);
        return Task.CompletedTask;
    }

    public Task AcknowledgeAsync(AtRiskFlag flag, Guid parentProfileId, DateTime acknowledgedAtUtc, CancellationToken ct = default)
    {
        flag.AcknowledgedAt = DateTime.SpecifyKind(acknowledgedAtUtc, DateTimeKind.Utc);
        flag.AcknowledgedByParentProfileId = parentProfileId;
        _db.AtRiskFlags.Update(flag);
        return Task.CompletedTask;
    }
}

public static class AtRiskFlagRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4AtRiskFlagRepository(this IServiceCollection services)
    {
        services.AddScoped<IAtRiskFlagRepository, AtRiskFlagRepository>();
        return services;
    }
}
