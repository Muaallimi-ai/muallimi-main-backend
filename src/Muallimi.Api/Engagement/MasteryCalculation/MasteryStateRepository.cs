using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.MasteryCalculation;

/// <summary>
/// T034 (US4) — <see cref="MasteryState"/> repository.
///
/// Keyed by (tenant_id, student_id, subject_id, topic_id, calculation_version).
/// The calculator upserts a row per (student × subject × topic) per version
/// so recomputation under a new version never overwrites history bound to
/// an older award.
/// </summary>
public interface IMasteryStateRepository
{
    Task<MasteryState?> GetAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid? topicId,
        string calculationVersion,
        CancellationToken ct = default);

    Task AddAsync(MasteryState state, CancellationToken ct = default);

    Task<IReadOnlyList<MasteryState>> ForStudentAsync(
        Guid tenantId,
        Guid studentId,
        string calculationVersion,
        CancellationToken ct = default);
}

public sealed class MasteryStateRepository : IMasteryStateRepository
{
    private readonly MuallimiDbContext _db;

    public MasteryStateRepository(MuallimiDbContext db) => _db = db;

    public Task<MasteryState?> GetAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid? topicId,
        string calculationVersion,
        CancellationToken ct = default)
        => _db.MasteryStates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId
                     && s.StudentId == studentId
                     && s.SubjectId == subjectId
                     && s.TopicId == topicId
                     && s.CalculationVersion == calculationVersion,
                ct);

    public Task AddAsync(MasteryState state, CancellationToken ct = default)
    {
        _db.MasteryStates.Add(state);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MasteryState>> ForStudentAsync(
        Guid tenantId,
        Guid studentId,
        string calculationVersion,
        CancellationToken ct = default)
    {
        return await _db.MasteryStates
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId
                        && s.StudentId == studentId
                        && s.CalculationVersion == calculationVersion)
            .ToListAsync(ct);
    }
}

public static class MasteryStateRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4MasteryStateRepository(this IServiceCollection services)
    {
        services.AddScoped<IMasteryStateRepository, MasteryStateRepository>();
        return services;
    }
}
