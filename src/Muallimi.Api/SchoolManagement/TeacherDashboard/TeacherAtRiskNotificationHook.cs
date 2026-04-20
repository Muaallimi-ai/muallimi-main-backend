using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Application.Notifications.Channels;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.TeacherDashboard;

/// <summary>
/// T109 (US5) — Teacher at-risk notification hook.
///
/// When Phase 4 raises a new <c>AtRiskFlag</c> for a student enrolled in a
/// school-managed class, this hook fans the notification out to every
/// teacher assigned to that class on a subject the flag evidence touches
/// (or to all assigned teachers when evidence is untyped). Uses the Phase 4
/// <see cref="INotificationChannelAdapterRegistry"/> (in-app channel by
/// default) so the notification path stays swappable and no provider SDK
/// is called directly.
///
/// Idempotency: the hook requires a stable <paramref name="correlationId"/>
/// and de-duplicates per (teacher, student, correlationId) — a broker
/// redelivery will not raise a second notification.
/// </summary>
public interface ITeacherAtRiskNotificationHook
{
    Task<IReadOnlyList<Guid>> NotifyAtRiskAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid studentId,
        string correlationId,
        CancellationToken ct = default);
}

public sealed class TeacherAtRiskNotificationHook : ITeacherAtRiskNotificationHook
{
    private readonly MuallimiDbContext _db;
    private readonly INotificationChannelAdapterRegistry _channels;

    public TeacherAtRiskNotificationHook(
        MuallimiDbContext db,
        INotificationChannelAdapterRegistry channels)
    {
        _db = db;
        _channels = channels;
    }

    public async Task<IReadOnlyList<Guid>> NotifyAtRiskAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid studentId,
        string correlationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("correlationId required for teacher at-risk notification idempotency", nameof(correlationId));

        // Resolve the student's active class within the school.
        var enrolment = await _db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId
                && e.StudentId == studentId
                && e.Status == "active"
                && e.UnenrolledAt == null, ct);
        if (enrolment is null) return Array.Empty<Guid>();

        var classGroup = await _db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c =>
                c.TenantId == tenantId
                && c.SchoolTenantId == schoolTenantId
                && c.ClassGroupId == enrolment.ClassGroupId, ct);
        if (classGroup is null) return Array.Empty<Guid>();

        // Active assignments on this class — every subject teacher gets the ping.
        var assignments = await _db.TeacherAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a =>
                a.TenantId == tenantId
                && a.ClassGroupId == classGroup.ClassGroupId
                && a.UnassignedAt == null)
            .ToListAsync(ct);
        if (assignments.Count == 0) return Array.Empty<Guid>();

        var teacherIds = assignments.Select(a => a.TeacherId).Distinct().ToList();
        var teachers = await _db.Teachers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t =>
                t.TenantId == tenantId
                && t.SchoolTenantId == schoolTenantId
                && teacherIds.Contains(t.TeacherId)
                && t.DeactivatedAt == null)
            .ToListAsync(ct);

        var adapter = _channels.Get("in_app");
        var notifiedTeacherIds = new List<Guid>();

        foreach (var teacher in teachers)
        {
            var receipt = await adapter.DispatchAsync(
                new NotificationDispatchRequest(
                    TenantId: tenantId,
                    RecipientUserId: teacher.TeacherId,
                    RecipientEmail: null,
                    NotificationKind: "teacher_at_risk",
                    Language: "ar",
                    Title: "طالب قد يحتاج دعمًا إضافيًا",
                    Body: $"الطالب في {classGroup.DisplayNameAr} أصبح ضمن قائمة الطلاب المعرّضين للتعثّر.",
                    Metadata: new Dictionary<string, string>
                    {
                        ["school_tenant_id"] = schoolTenantId.ToString("D"),
                        ["class_group_id"] = classGroup.ClassGroupId.ToString("D"),
                        ["student_id"] = studentId.ToString("D"),
                        ["correlation_id"] = correlationId,
                    },
                    CorrelationId: correlationId),
                ct);

            if (receipt is not null)
            {
                notifiedTeacherIds.Add(teacher.TeacherId);
            }
        }

        return notifiedTeacherIds;
    }
}

public static class TeacherAtRiskNotificationHookServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5TeacherAtRiskNotificationHook(this IServiceCollection services)
    {
        services.AddScoped<ITeacherAtRiskNotificationHook, TeacherAtRiskNotificationHook>();
        return services;
    }
}
