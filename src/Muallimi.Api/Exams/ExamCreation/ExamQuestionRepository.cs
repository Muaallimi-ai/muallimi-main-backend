using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Exams.ExamCreation;

/// <summary>
/// T123 (US6) — <see cref="ExamQuestion"/> and <see cref="ExamAssignment"/>
/// repositories.
///
/// Paired because every exam-creation flow also persists the class-group
/// assignments atomically. Questions are loaded in <c>DisplayOrder</c> for
/// the student player; assignments are projected by class for listing and
/// scoping.
/// </summary>
public interface IExamQuestionRepository
{
    Task AddAsync(ExamQuestion row, CancellationToken ct = default);

    Task<IReadOnlyList<ExamQuestion>> ListForExamAsync(Guid tenantId, Guid examId, CancellationToken ct = default);

    Task<ExamQuestion?> GetAsync(Guid tenantId, Guid examQuestionId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class ExamQuestionRepository : IExamQuestionRepository
{
    private readonly MuallimiDbContext _db;

    public ExamQuestionRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(ExamQuestion row, CancellationToken ct = default)
    {
        _db.ExamQuestions.Add(row);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ExamQuestion>> ListForExamAsync(Guid tenantId, Guid examId, CancellationToken ct = default)
        => await _db.ExamQuestions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(q => q.TenantId == tenantId && q.ExamId == examId)
            .OrderBy(q => q.DisplayOrder)
            .ToListAsync(ct);

    public Task<ExamQuestion?> GetAsync(Guid tenantId, Guid examQuestionId, CancellationToken ct = default)
        => _db.ExamQuestions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(q => q.TenantId == tenantId && q.ExamQuestionId == examQuestionId, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public interface IExamAssignmentRepository
{
    Task AddAsync(ExamAssignment row, CancellationToken ct = default);

    Task<IReadOnlyList<ExamAssignment>> ListForExamAsync(Guid tenantId, Guid examId, CancellationToken ct = default);

    Task<IReadOnlyList<ExamAssignment>> ListForClassAsync(Guid tenantId, Guid classGroupId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class ExamAssignmentRepository : IExamAssignmentRepository
{
    private readonly MuallimiDbContext _db;

    public ExamAssignmentRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(ExamAssignment row, CancellationToken ct = default)
    {
        _db.ExamAssignments.Add(row);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ExamAssignment>> ListForExamAsync(Guid tenantId, Guid examId, CancellationToken ct = default)
        => await _db.ExamAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ExamId == examId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ExamAssignment>> ListForClassAsync(Guid tenantId, Guid classGroupId, CancellationToken ct = default)
        => await _db.ExamAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ClassGroupId == classGroupId)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class ExamQuestionRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ExamQuestionRepository(this IServiceCollection services)
    {
        services.AddScoped<IExamQuestionRepository, ExamQuestionRepository>();
        services.AddScoped<IExamAssignmentRepository, ExamAssignmentRepository>();
        return services;
    }
}
