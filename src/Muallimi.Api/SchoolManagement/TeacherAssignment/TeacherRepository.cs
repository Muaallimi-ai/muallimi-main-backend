using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.TeacherAssignment;

/// <summary>
/// T073 (US3) — <see cref="Teacher"/> and <see cref="TeacherAssignment"/>
/// repositories.
///
/// Paired because every teacher surface also touches assignments. All
/// queries are tenant + school-tenant scoped. Removing an assignment
/// stamps <c>UnassignedAt</c> rather than deleting the row so historical
/// exam ownership remains visible.
/// </summary>
public interface ITeacherRepository
{
    Task AddAsync(Teacher teacher, CancellationToken ct = default);

    Task<Teacher?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid teacherId, CancellationToken ct = default);

    Task<IReadOnlyList<Teacher>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class TeacherRepository : ITeacherRepository
{
    private readonly MuallimiDbContext _db;

    public TeacherRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(Teacher teacher, CancellationToken ct = default)
    {
        _db.Teachers.Add(teacher);
        return Task.CompletedTask;
    }

    public Task<Teacher?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid teacherId, CancellationToken ct = default)
        => _db.Teachers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t =>
                t.TenantId == tenantId
                && t.SchoolTenantId == schoolTenantId
                && t.TeacherId == teacherId,
                ct);

    public async Task<IReadOnlyList<Teacher>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => await _db.Teachers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.SchoolTenantId == schoolTenantId && t.DeactivatedAt == null)
            .OrderBy(t => t.DisplayNameAr)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public interface ITeacherAssignmentRepository
{
    Task AddAsync(Muallimi.Domain.SchoolManagement.TeacherAssignment row, CancellationToken ct = default);

    Task<Muallimi.Domain.SchoolManagement.TeacherAssignment?> GetByIdAsync(Guid tenantId, Guid teacherAssignmentId, CancellationToken ct = default);

    Task<Muallimi.Domain.SchoolManagement.TeacherAssignment?> GetActiveAsync(Guid tenantId, Guid teacherId, Guid classGroupId, Guid subjectId, CancellationToken ct = default);

    Task<IReadOnlyList<Muallimi.Domain.SchoolManagement.TeacherAssignment>> ListForClassAsync(Guid tenantId, Guid classGroupId, CancellationToken ct = default);

    Task<IReadOnlyList<Muallimi.Domain.SchoolManagement.TeacherAssignment>> ListActiveForTeacherAsync(Guid tenantId, Guid teacherId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class TeacherAssignmentRepository : ITeacherAssignmentRepository
{
    private readonly MuallimiDbContext _db;

    public TeacherAssignmentRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(Muallimi.Domain.SchoolManagement.TeacherAssignment row, CancellationToken ct = default)
    {
        _db.TeacherAssignments.Add(row);
        return Task.CompletedTask;
    }

    public Task<Muallimi.Domain.SchoolManagement.TeacherAssignment?> GetByIdAsync(Guid tenantId, Guid teacherAssignmentId, CancellationToken ct = default)
        => _db.TeacherAssignments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a =>
                a.TenantId == tenantId
                && a.TeacherAssignmentId == teacherAssignmentId,
                ct);

    public Task<Muallimi.Domain.SchoolManagement.TeacherAssignment?> GetActiveAsync(
        Guid tenantId,
        Guid teacherId,
        Guid classGroupId,
        Guid subjectId,
        CancellationToken ct = default)
        => _db.TeacherAssignments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a =>
                a.TenantId == tenantId
                && a.TeacherId == teacherId
                && a.ClassGroupId == classGroupId
                && a.SubjectId == subjectId
                && a.UnassignedAt == null,
                ct);

    public async Task<IReadOnlyList<Muallimi.Domain.SchoolManagement.TeacherAssignment>> ListForClassAsync(
        Guid tenantId,
        Guid classGroupId,
        CancellationToken ct = default)
        => await _db.TeacherAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ClassGroupId == classGroupId && a.UnassignedAt == null)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Muallimi.Domain.SchoolManagement.TeacherAssignment>> ListActiveForTeacherAsync(
        Guid tenantId,
        Guid teacherId,
        CancellationToken ct = default)
        => await _db.TeacherAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.TeacherId == teacherId && a.UnassignedAt == null)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class TeacherRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5TeacherRepository(this IServiceCollection services)
    {
        services.AddScoped<ITeacherRepository, TeacherRepository>();
        services.AddScoped<ITeacherAssignmentRepository, TeacherAssignmentRepository>();
        return services;
    }
}
