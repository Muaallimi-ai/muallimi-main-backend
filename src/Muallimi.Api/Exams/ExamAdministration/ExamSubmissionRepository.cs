using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Exams.ExamAdministration;

/// <summary>
/// T124 (US6) — <see cref="ExamSubmission"/> repository.
///
/// A student can submit exactly once per exam. The unique index on
/// <c>(exam_id, student_id)</c> enforces this at the storage layer; the
/// <see cref="GetForStudentAsync"/> call is the single lookup path the
/// endpoint layer uses before writing a new row.
/// </summary>
public interface IExamSubmissionRepository
{
    Task AddAsync(ExamSubmission row, CancellationToken ct = default);

    Task<ExamSubmission?> GetAsync(Guid tenantId, Guid examSubmissionId, CancellationToken ct = default);

    Task<ExamSubmission?> GetForStudentAsync(Guid tenantId, Guid examId, Guid studentId, CancellationToken ct = default);

    Task<IReadOnlyList<ExamSubmission>> ListForExamAsync(Guid tenantId, Guid examId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class ExamSubmissionRepository : IExamSubmissionRepository
{
    private readonly MuallimiDbContext _db;

    public ExamSubmissionRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(ExamSubmission row, CancellationToken ct = default)
    {
        _db.ExamSubmissions.Add(row);
        return Task.CompletedTask;
    }

    public Task<ExamSubmission?> GetAsync(Guid tenantId, Guid examSubmissionId, CancellationToken ct = default)
        => _db.ExamSubmissions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.ExamSubmissionId == examSubmissionId, ct);

    public Task<ExamSubmission?> GetForStudentAsync(Guid tenantId, Guid examId, Guid studentId, CancellationToken ct = default)
        => _db.ExamSubmissions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s =>
                s.TenantId == tenantId
                && s.ExamId == examId
                && s.StudentId == studentId,
                ct);

    public async Task<IReadOnlyList<ExamSubmission>> ListForExamAsync(Guid tenantId, Guid examId, CancellationToken ct = default)
        => await _db.ExamSubmissions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.ExamId == examId)
            .OrderBy(s => s.SubmittedAt)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class ExamSubmissionRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ExamSubmissionRepository(this IServiceCollection services)
    {
        services.AddScoped<IExamSubmissionRepository, ExamSubmissionRepository>();
        return services;
    }
}
