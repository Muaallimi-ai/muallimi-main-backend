using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.ClassManagement;

/// <summary>
/// T072 (US3) — <see cref="ClassEnrolment"/> repository.
///
/// Enforces the invariant that a student has at most one active enrolment
/// per class at a time. Transfers preserve historical rows by marking the
/// source enrolment unenrolled (with <c>TransferToClassId</c>) rather than
/// deleting it.
/// </summary>
public interface IClassEnrolmentRepository
{
    Task AddAsync(ClassEnrolment row, CancellationToken ct = default);

    Task<ClassEnrolment?> GetActiveAsync(Guid tenantId, Guid classGroupId, Guid studentId, CancellationToken ct = default);

    Task<IReadOnlyList<ClassEnrolment>> ListActiveForClassAsync(Guid tenantId, Guid classGroupId, CancellationToken ct = default);

    Task<IReadOnlyList<ClassEnrolment>> ListForStudentAsync(Guid tenantId, Guid studentId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class ClassEnrolmentRepository : IClassEnrolmentRepository
{
    private readonly MuallimiDbContext _db;

    public ClassEnrolmentRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(ClassEnrolment row, CancellationToken ct = default)
    {
        _db.ClassEnrolments.Add(row);
        return Task.CompletedTask;
    }

    public Task<ClassEnrolment?> GetActiveAsync(Guid tenantId, Guid classGroupId, Guid studentId, CancellationToken ct = default)
        => _db.ClassEnrolments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId
                && e.ClassGroupId == classGroupId
                && e.StudentId == studentId
                && e.Status == "active",
                ct);

    public async Task<IReadOnlyList<ClassEnrolment>> ListActiveForClassAsync(Guid tenantId, Guid classGroupId, CancellationToken ct = default)
        => await _db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ClassGroupId == classGroupId && e.Status == "active")
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ClassEnrolment>> ListForStudentAsync(Guid tenantId, Guid studentId, CancellationToken ct = default)
        => await _db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.StudentId == studentId)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class ClassEnrolmentRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ClassEnrolmentRepository(this IServiceCollection services)
    {
        services.AddScoped<IClassEnrolmentRepository, ClassEnrolmentRepository>();
        return services;
    }
}
