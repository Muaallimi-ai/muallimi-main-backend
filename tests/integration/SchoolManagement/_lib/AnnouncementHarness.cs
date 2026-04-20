using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Application.Notifications.Channels;
using Muallimi.Domain.Parents;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// Phase 5 US8 announcement test harness.
///
/// Seeds a two-tenant topology with two classes per school (Grade 7A and
/// Grade 7B), four active students per class plus one transferred student
/// (enrolment.Status="transferred") so tests can assert the contract
/// invariants:
///   • targeting resolution across class / grade / school scope.
///   • transferred students excluded from dispatch.
///   • parent-fan-out via effective-window ChildLink rows.
///   • cross-tenant isolation.
/// </summary>
public sealed class AnnouncementHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0801");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0802");
    public static readonly Guid SchoolAlpha = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0801");
    public static readonly Guid SchoolBeta = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0802");
    public static readonly Guid ClassAlpha7A = Guid.Parse("00000003-0000-0000-0000-00000000080A");
    public static readonly Guid ClassAlpha7B = Guid.Parse("00000003-0000-0000-0000-00000000080B");
    public static readonly Guid ClassBeta7A = Guid.Parse("00000003-0000-0000-0000-000000000802");
    public static readonly Guid AdminAlpha = Guid.Parse("00000005-0000-0000-0000-000000000801");
    public static readonly Guid ParentAlpha = Guid.Parse("00000006-0000-0000-0000-000000000801");

    public readonly List<Guid> Alpha7AStudents = new();
    public readonly List<Guid> Alpha7BStudents = new();
    public readonly List<Guid> BetaStudents = new();
    public Guid TransferredStudent = Guid.Parse("00000099-0000-0000-0000-000000000801");

    private readonly MuallimiDbContext _db;

    public AnnouncementHarness(MuallimiDbContext db) => _db = db;

    public async Task SeedAsync()
    {
        var now = DateTime.UtcNow;
        SeedSchool(TenantAlpha, SchoolAlpha, "Alpha", now);

        SeedClass(TenantAlpha, SchoolAlpha, ClassAlpha7A, 7, "A", now);
        for (var i = 0; i < 4; i++)
        {
            var sid = Guid.Parse($"00000007-0000-0000-0000-0A0000000{i:D3}");
            Alpha7AStudents.Add(sid);
            SeedStudent(TenantAlpha, sid, $"Alpha7A Student {i + 1}", now);
            SeedEnrolment(TenantAlpha, ClassAlpha7A, sid, "active", now);
        }

        // One transferred student seeded under ClassAlpha7A but with
        // Status="transferred" — the dispatcher must skip this student.
        SeedStudent(TenantAlpha, TransferredStudent, "Transferred Student", now);
        SeedEnrolment(TenantAlpha, ClassAlpha7A, TransferredStudent, "transferred", now);

        SeedClass(TenantAlpha, SchoolAlpha, ClassAlpha7B, 7, "B", now);
        for (var i = 0; i < 3; i++)
        {
            var sid = Guid.Parse($"00000007-0000-0000-0000-0B0000000{i:D3}");
            Alpha7BStudents.Add(sid);
            SeedStudent(TenantAlpha, sid, $"Alpha7B Student {i + 1}", now);
            SeedEnrolment(TenantAlpha, ClassAlpha7B, sid, "active", now);
        }

        // Parent Alpha linked to the first Alpha7A student.
        _db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = ParentAlpha,
            TenantId = TenantAlpha,
            IdentityId = Guid.NewGuid(),
            PreferredLanguage = "ar",
            Locale = "ar-SA",
            Timezone = "Asia/Dubai",
            NotificationChannels = "{\"in_app\":true}",
            QuietHours = "{}",
            PerChildOverrides = "{}",
            ConsentState = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = TenantAlpha,
            ParentProfileId = ParentAlpha,
            StudentId = Alpha7AStudents[0],
            Role = "guardian",
            EffectiveStart = now.AddDays(-10).Date,
            EffectiveEnd = null,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Beta tenant — cross-tenant isolation fixture.
        SeedSchool(TenantBeta, SchoolBeta, "Beta", now);
        SeedClass(TenantBeta, SchoolBeta, ClassBeta7A, 7, "A", now);
        for (var i = 0; i < 2; i++)
        {
            var sid = Guid.Parse($"00000008-0000-0000-0000-000000000{i:D3}");
            BetaStudents.Add(sid);
            SeedStudent(TenantBeta, sid, $"Beta Student {i + 1}", now);
            SeedEnrolment(TenantBeta, ClassBeta7A, sid, "active", now);
        }

        await _db.SaveChangesAsync();
    }

    private void SeedSchool(Guid tenantId, Guid schoolTenantId, string prefix, DateTime now)
    {
        _db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = $"مدرسة {prefix}",
            SchoolNameEn = $"{prefix} School",
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            SubjectBindings = "[]",
            AcademicCalendar = "{}",
            PreferredLanguage = "ar",
            SubscriptionStatus = "active",
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private void SeedClass(Guid tenantId, Guid schoolTenantId, Guid classId, int grade, string section, DateTime now)
    {
        _db.ClassGroups.Add(new ClassGroup
        {
            ClassGroupId = classId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            Grade = grade,
            SectionLabel = section,
            DisplayNameAr = $"صف {grade}{section}",
            DisplayNameEn = $"Grade {grade}{section}",
            SubjectBindings = "[]",
            AcademicYear = "2026-2027",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private void SeedStudent(Guid tenantId, Guid studentId, string displayName, DateTime now)
    {
        _db.StudentProfiles.Add(new StudentProfile
        {
            Id = studentId,
            TenantId = tenantId,
            DisplayName = displayName,
            CurriculumType = "moe",
            Grade = "7",
            PreferredLanguage = "ar",
            PlanTier = "school",
            ConsentState = "granted",
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private void SeedEnrolment(Guid tenantId, Guid classId, Guid studentId, string status, DateTime now)
    {
        _db.ClassEnrolments.Add(new ClassEnrolment
        {
            ClassEnrolmentId = Guid.NewGuid(),
            TenantId = tenantId,
            ClassGroupId = classId,
            StudentId = studentId,
            EnrolledAt = now,
            Status = status,
        });
    }
}

/// <summary>
/// In-memory <see cref="INotificationChannelAdapterRegistry"/> used by the
/// announcement dispatcher tests — captures every dispatch so assertions
/// can verify channel + metadata without running the HTTP stubs.
/// </summary>
public sealed class RecordingNotificationChannelRegistry : INotificationChannelAdapterRegistry
{
    public List<NotificationDispatchRequest> Dispatched { get; } = new();
    private readonly RecordingAdapter _adapter;

    public RecordingNotificationChannelRegistry()
    {
        _adapter = new RecordingAdapter(Dispatched);
    }

    public INotificationChannelAdapter Get(string channel) => _adapter;

    private sealed class RecordingAdapter : INotificationChannelAdapter
    {
        private readonly List<NotificationDispatchRequest> _sink;
        public RecordingAdapter(List<NotificationDispatchRequest> sink) => _sink = sink;
        public string Channel => "in_app";
        public Task<NotificationDispatchReceipt> DispatchAsync(NotificationDispatchRequest request, System.Threading.CancellationToken ct = default)
        {
            _sink.Add(request);
            return Task.FromResult(new NotificationDispatchReceipt(
                ReceiptId: Guid.NewGuid().ToString("D"),
                Channel: Channel));
        }
    }
}
