using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Exams.ExamCreation;

/// <summary>
/// T122 (US6) — <see cref="Exam"/> repository.
///
/// Every lookup is tenant + school-tenant scoped. The in-memory path
/// uses <c>IgnoreQueryFilters</c> so the explicit tenant predicate is
/// what actually enforces isolation — matching the Phase 5 US1–US5
/// repositories.
/// </summary>
public interface IExamRepository
{
    Task AddAsync(Exam row, CancellationToken ct = default);

    Task<Exam?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid examId, CancellationToken ct = default);

    Task<IReadOnlyList<Exam>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task<IReadOnlyList<Exam>> ListForTeacherAsync(Guid tenantId, Guid schoolTenantId, Guid teacherId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class ExamRepository : IExamRepository
{
    private readonly MuallimiDbContext _db;

    public ExamRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(Exam row, CancellationToken ct = default)
    {
        _db.Exams.Add(row);
        return Task.CompletedTask;
    }

    public Task<Exam?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid examId, CancellationToken ct = default)
        => _db.Exams
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId
                && e.SchoolTenantId == schoolTenantId
                && e.ExamId == examId,
                ct);

    public async Task<IReadOnlyList<Exam>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => await _db.Exams
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.SchoolTenantId == schoolTenantId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Exam>> ListForTeacherAsync(Guid tenantId, Guid schoolTenantId, Guid teacherId, CancellationToken ct = default)
        => await _db.Exams
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && e.SchoolTenantId == schoolTenantId
                && e.CreatedByTeacherId == teacherId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class ExamRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ExamRepository(this IServiceCollection services)
    {
        services.AddScoped<IExamRepository, ExamRepository>();
        return services;
    }
}
