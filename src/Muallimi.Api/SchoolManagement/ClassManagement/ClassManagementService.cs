using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.SchoolManagement.ClassManagement;

/// <summary>
/// T074 (US3) — <c>ClassManagementService</c>.
///
/// Orchestrates class create, enrol, transfer, and unenrol operations.
/// Every write is tenant + school-tenant scoped. Enrolment is idempotent
/// (re-enrolling an active student is a no-op). Transfers mark the source
/// enrolment as <c>transferred</c> with <c>TransferToClassId</c> so
/// historical data is preserved.
/// </summary>
public sealed record ClassGroupCreateInput(
    int Grade,
    string SectionLabel,
    string DisplayNameAr,
    string DisplayNameEn,
    IReadOnlyList<string> SubjectBindings,
    string AcademicYear,
    Guid TenantId,
    Guid SchoolTenantId);

public sealed record EnrolmentOutcome(int EnrolledCount, int AlreadyEnrolledCount, IReadOnlyList<Guid> StudentIds);

public sealed record TransferOutcome(bool Transferred, Guid? NewEnrolmentId);

public interface IClassManagementService
{
    Task<ClassGroup> CreateClassAsync(ClassGroupCreateInput input, CancellationToken ct = default);

    Task<EnrolmentOutcome> EnrolStudentsAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        IReadOnlyList<Guid> studentIds,
        CancellationToken ct = default);

    Task<TransferOutcome> TransferStudentAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid sourceClassGroupId,
        Guid targetClassGroupId,
        Guid studentId,
        CancellationToken ct = default);

    Task<bool> UnenrolStudentAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        Guid studentId,
        CancellationToken ct = default);
}

public sealed class ClassManagementService : IClassManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IClassGroupRepository _classes;
    private readonly IClassEnrolmentRepository _enrolments;

    public ClassManagementService(
        IClassGroupRepository classes,
        IClassEnrolmentRepository enrolments)
    {
        _classes = classes;
        _enrolments = enrolments;
    }

    public async Task<ClassGroup> CreateClassAsync(ClassGroupCreateInput input, CancellationToken ct = default)
    {
        if (input.Grade <= 0) throw new ArgumentException("grade must be > 0", nameof(input));
        if (string.IsNullOrWhiteSpace(input.DisplayNameAr) || string.IsNullOrWhiteSpace(input.DisplayNameEn))
            throw new ArgumentException("display names required", nameof(input));

        var now = DateTime.UtcNow;
        var row = new ClassGroup
        {
            ClassGroupId = Guid.NewGuid(),
            TenantId = input.TenantId,
            SchoolTenantId = input.SchoolTenantId,
            Grade = input.Grade,
            SectionLabel = input.SectionLabel,
            DisplayNameAr = input.DisplayNameAr,
            DisplayNameEn = input.DisplayNameEn,
            SubjectBindings = JsonSerializer.Serialize(input.SubjectBindings, JsonOptions),
            AcademicYear = input.AcademicYear,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _classes.AddAsync(row, ct);
        await _classes.SaveChangesAsync(ct);
        return row;
    }

    public async Task<EnrolmentOutcome> EnrolStudentsAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        IReadOnlyList<Guid> studentIds,
        CancellationToken ct = default)
    {
        var classGroup = await _classes.GetByIdAsync(tenantId, schoolTenantId, classGroupId, ct)
            ?? throw new InvalidOperationException("class_not_found");

        var enrolled = new List<Guid>();
        var alreadyEnrolled = 0;
        var now = DateTime.UtcNow;

        foreach (var studentId in studentIds.Distinct())
        {
            var existing = await _enrolments.GetActiveAsync(tenantId, classGroup.ClassGroupId, studentId, ct);
            if (existing is not null)
            {
                alreadyEnrolled++;
                continue;
            }

            await _enrolments.AddAsync(new ClassEnrolment
            {
                ClassEnrolmentId = Guid.NewGuid(),
                TenantId = tenantId,
                ClassGroupId = classGroup.ClassGroupId,
                StudentId = studentId,
                EnrolledAt = now,
                Status = "active",
            }, ct);
            enrolled.Add(studentId);
        }

        await _enrolments.SaveChangesAsync(ct);
        return new EnrolmentOutcome(enrolled.Count, alreadyEnrolled, enrolled);
    }

    public async Task<TransferOutcome> TransferStudentAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid sourceClassGroupId,
        Guid targetClassGroupId,
        Guid studentId,
        CancellationToken ct = default)
    {
        if (sourceClassGroupId == targetClassGroupId)
            throw new ArgumentException("source_and_target_must_differ");

        var source = await _classes.GetByIdAsync(tenantId, schoolTenantId, sourceClassGroupId, ct)
            ?? throw new InvalidOperationException("source_class_not_found");
        var target = await _classes.GetByIdAsync(tenantId, schoolTenantId, targetClassGroupId, ct)
            ?? throw new InvalidOperationException("target_class_not_found");

        var activeSource = await _enrolments.GetActiveAsync(tenantId, source.ClassGroupId, studentId, ct);
        if (activeSource is null) return new TransferOutcome(false, null);

        var now = DateTime.UtcNow;
        activeSource.Status = "transferred";
        activeSource.UnenrolledAt = now;
        activeSource.TransferToClassId = target.ClassGroupId;

        var alreadyActiveInTarget = await _enrolments.GetActiveAsync(tenantId, target.ClassGroupId, studentId, ct);
        Guid? newEnrolmentId = null;
        if (alreadyActiveInTarget is null)
        {
            var next = new ClassEnrolment
            {
                ClassEnrolmentId = Guid.NewGuid(),
                TenantId = tenantId,
                ClassGroupId = target.ClassGroupId,
                StudentId = studentId,
                EnrolledAt = now,
                Status = "active",
            };
            await _enrolments.AddAsync(next, ct);
            newEnrolmentId = next.ClassEnrolmentId;
        }

        await _enrolments.SaveChangesAsync(ct);
        return new TransferOutcome(true, newEnrolmentId);
    }

    public async Task<bool> UnenrolStudentAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        Guid studentId,
        CancellationToken ct = default)
    {
        _ = await _classes.GetByIdAsync(tenantId, schoolTenantId, classGroupId, ct)
            ?? throw new InvalidOperationException("class_not_found");

        var active = await _enrolments.GetActiveAsync(tenantId, classGroupId, studentId, ct);
        if (active is null) return false;

        active.Status = "unenrolled";
        active.UnenrolledAt = DateTime.UtcNow;
        await _enrolments.SaveChangesAsync(ct);
        return true;
    }
}

public static class ClassManagementServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ClassManagementService(this IServiceCollection services)
    {
        services.AddScoped<IClassManagementService, ClassManagementService>();
        return services;
    }
}
