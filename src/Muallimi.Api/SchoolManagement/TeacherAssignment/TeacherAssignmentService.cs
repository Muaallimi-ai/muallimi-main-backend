using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolManagement.ClassManagement;
using TeacherAssignmentEntity = Muallimi.Domain.SchoolManagement.TeacherAssignment;

namespace Muallimi.Api.SchoolManagement.TeacherAssignment;

/// <summary>
/// T075 (US3) — <c>TeacherAssignmentService</c>.
///
/// Orchestrates assign / unassign flows with scope validation. An assign
/// call requires the teacher and class to belong to the same school
/// tenant; otherwise the call fails. Removing an assignment marks
/// <c>UnassignedAt</c> rather than deleting so historical exam ownership
/// is preserved.
/// </summary>
public sealed record TeacherAssignmentInput(
    Guid TenantId,
    Guid SchoolTenantId,
    Guid ClassGroupId,
    Guid TeacherId,
    Guid SubjectId);

public interface ITeacherAssignmentService
{
    Task<TeacherAssignmentEntity> AssignAsync(TeacherAssignmentInput input, CancellationToken ct = default);

    Task<bool> UnassignAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        Guid teacherAssignmentId,
        CancellationToken ct = default);
}

public sealed class TeacherAssignmentService : ITeacherAssignmentService
{
    private readonly IClassGroupRepository _classes;
    private readonly ITeacherRepository _teachers;
    private readonly ITeacherAssignmentRepository _assignments;

    public TeacherAssignmentService(
        IClassGroupRepository classes,
        ITeacherRepository teachers,
        ITeacherAssignmentRepository assignments)
    {
        _classes = classes;
        _teachers = teachers;
        _assignments = assignments;
    }

    public async Task<TeacherAssignmentEntity> AssignAsync(TeacherAssignmentInput input, CancellationToken ct = default)
    {
        var classGroup = await _classes.GetByIdAsync(input.TenantId, input.SchoolTenantId, input.ClassGroupId, ct)
            ?? throw new InvalidOperationException("class_not_found");
        var teacher = await _teachers.GetByIdAsync(input.TenantId, input.SchoolTenantId, input.TeacherId, ct)
            ?? throw new InvalidOperationException("teacher_not_found");
        if (teacher.SchoolTenantId != classGroup.SchoolTenantId)
            throw new InvalidOperationException("teacher_class_scope_mismatch");

        var existing = await _assignments.GetActiveAsync(
            input.TenantId,
            input.TeacherId,
            input.ClassGroupId,
            input.SubjectId,
            ct);
        if (existing is not null) return existing;

        var now = DateTime.UtcNow;
        var row = new TeacherAssignmentEntity
        {
            TeacherAssignmentId = Guid.NewGuid(),
            TenantId = input.TenantId,
            TeacherId = input.TeacherId,
            ClassGroupId = input.ClassGroupId,
            SubjectId = input.SubjectId,
            AssignedAt = now,
        };
        await _assignments.AddAsync(row, ct);
        await _assignments.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> UnassignAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        Guid teacherAssignmentId,
        CancellationToken ct = default)
    {
        // Scope check: the class must belong to the school tenant before
        // we mutate the assignment (prevents cross-school tampering via
        // a stolen assignment id).
        _ = await _classes.GetByIdAsync(tenantId, schoolTenantId, classGroupId, ct)
            ?? throw new InvalidOperationException("class_not_found");

        var existing = await _assignments.GetByIdAsync(tenantId, teacherAssignmentId, ct);
        if (existing is null || existing.UnassignedAt is not null || existing.ClassGroupId != classGroupId)
            return false;

        existing.UnassignedAt = DateTime.UtcNow;
        await _assignments.SaveChangesAsync(ct);
        return true;
    }
}

public static class TeacherAssignmentServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5TeacherAssignmentService(this IServiceCollection services)
    {
        services.AddScoped<ITeacherAssignmentService, TeacherAssignmentService>();
        return services;
    }
}
