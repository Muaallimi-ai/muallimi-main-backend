using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;
using Muallimi.MainBackend.Tests.Contract.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.Tenancy;

/// <summary>
/// T126 — Cross-surface tenant-isolation contract for the full Phase 3
/// entity set.
///
/// Every tenant-scoped Phase 3 entity MUST disappear from queries when the
/// ambient tenant context changes. This test seeds rows for two tenants
/// across every student-facing surface (profile, session, lesson viewer,
/// tutor chat, voice capture, quiz, mock test, homework help, whiteboard,
/// session events) and then re-scopes the DbContext to each tenant in turn,
/// asserting the other tenant's rows are invisible without
/// <c>IgnoreQueryFilters()</c>.
///
/// Phase 3 layers session state on top of the existing tenant query filter.
/// A drift in the filter configuration would leak one student's homework
/// submission or voice capture into another tenant's dashboard — a
/// constitution-level violation.
/// </summary>
public class CrossSurfaceIsolationTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaa1-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Every_Phase3_Entity_Filters_By_Ambient_TenantId()
    {
        var options = Phase3TestDbContextFactory.BuildOptions($"tenant-iso-{Guid.NewGuid():N}");

        var ambient = new AmbientTenantAccessor { CurrentTenantId = TenantA };

        await using (var db = new Phase3TestDbContext(options, ambient))
        {
            SeedEverySurfaceFor(db, TenantA);
            SeedEverySurfaceFor(db, TenantB);
            await db.SaveChangesAsync();
        }

        // Tenant A ambient: only tenant A rows surface.
        ambient.CurrentTenantId = TenantA;
        await using (var db = new Phase3TestDbContext(options, ambient))
        {
            await AssertOnlyTenantVisibleAsync(db, TenantA, TenantB);
        }

        // Tenant B ambient: only tenant B rows surface.
        ambient.CurrentTenantId = TenantB;
        await using (var db = new Phase3TestDbContext(options, ambient))
        {
            await AssertOnlyTenantVisibleAsync(db, TenantB, TenantA);
        }
    }

    [Fact]
    public async Task Null_Tenant_Context_Is_Admin_Scope_Used_By_Background_Services_Only()
    {
        // The global filter: `CurrentTenantId == null || TenantId ==
        // CurrentTenantId`. When the ambient tenant is null, the filter
        // short-circuits to `true` (all rows visible). This is the
        // "admin scope" used by background services like the
        // SessionEventDispatcher that must drain across tenants.
        //
        // The contract guarantee is therefore TWO-SIDED:
        //   - Every HTTP-serving student request MUST carry X-Tenant-Id so
        //     the accessor resolves to a concrete GUID (covered by the
        //     previous test).
        //   - Only background services with no HttpContext are permitted
        //     the null-tenant admin scope. This test pins down that the
        //     null ambient behaves as admin scope today so the dispatcher
        //     does not silently break.
        var options = Phase3TestDbContextFactory.BuildOptions($"tenant-iso-null-{Guid.NewGuid():N}");

        var ambient = new AmbientTenantAccessor { CurrentTenantId = TenantA };
        await using (var db = new Phase3TestDbContext(options, ambient))
        {
            SeedEverySurfaceFor(db, TenantA);
            SeedEverySurfaceFor(db, TenantB);
            await db.SaveChangesAsync();
        }

        ambient.CurrentTenantId = null;
        await using (var db = new Phase3TestDbContext(options, ambient))
        {
            // Admin scope: both tenants' rows visible without
            // IgnoreQueryFilters — exactly what a cross-tenant dispatcher
            // relies on.
            var profiles = await db.StudentProfiles.ToListAsync();
            Assert.Contains(profiles, p => p.TenantId == TenantA);
            Assert.Contains(profiles, p => p.TenantId == TenantB);

            var events = await db.SessionEvents.ToListAsync();
            Assert.Contains(events, e => e.TenantId == TenantA);
            Assert.Contains(events, e => e.TenantId == TenantB);
        }
    }

    [Fact]
    public async Task Session_Events_Are_Written_Tenant_Scoped_So_Phase4_Never_Crosses_Tenants()
    {
        var options = Phase3TestDbContextFactory.BuildOptions($"event-iso-{Guid.NewGuid():N}");

        var ambient = new AmbientTenantAccessor { CurrentTenantId = TenantA };

        await using (var db = new Phase3TestDbContext(options, ambient))
        {
            var writer = new SessionEventOutboxWriter(db);
            await writer.EnqueueAsync(
                SessionEventKind.session_start, TenantA, Guid.NewGuid(), Guid.NewGuid(),
                new { device_class = "mobile_small", preferred_language = "ar" },
                new CurriculumScope("Moe", "Grade7", null, null, null, null), "free");
            await writer.EnqueueAsync(
                SessionEventKind.lesson_view, TenantB, Guid.NewGuid(), Guid.NewGuid(),
                new { lesson_id = Guid.NewGuid(), opened_from = "home" },
                new CurriculumScope("Moe", "Grade7", null, null, null, null), "free");
            await db.SaveChangesAsync();
        }

        // Tenant A view: only the session_start event.
        ambient.CurrentTenantId = TenantA;
        await using (var db = new Phase3TestDbContext(options, ambient))
        {
            var rows = await db.SessionEvents.ToListAsync();
            Assert.Single(rows);
            Assert.Equal(TenantA, rows[0].TenantId);
            Assert.Equal("session_start", rows[0].EventKind);
        }

        // Tenant B view: only the lesson_view event.
        ambient.CurrentTenantId = TenantB;
        await using (var db = new Phase3TestDbContext(options, ambient))
        {
            var rows = await db.SessionEvents.ToListAsync();
            Assert.Single(rows);
            Assert.Equal(TenantB, rows[0].TenantId);
            Assert.Equal("lesson_view", rows[0].EventKind);
        }
    }

    private static void SeedEverySurfaceFor(MuallimiDbContext db, Guid tenantId)
    {
        var studentProfileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        db.StudentProfiles.Add(new StudentProfile
        {
            Id = studentProfileId,
            TenantId = tenantId,
            DisplayName = $"Student {tenantId}",
            CurriculumType = "Moe",
            Grade = "Grade7",
            PreferredLanguage = "ar",
            PlanTier = "premium",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        db.StudentSessions.Add(new StudentSession
        {
            Id = sessionId,
            TenantId = tenantId,
            StudentProfileId = studentProfileId,
            CorrelationId = Guid.NewGuid(),
            ActiveMode = "home",
            SessionStartedAt = DateTime.UtcNow,
            SessionLastActivityAt = DateTime.UtcNow,
        });

        db.LessonViewerStates.Add(new LessonViewerState
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = sessionId,
            LessonId = Guid.NewGuid(),
            LastInteractionAt = DateTime.UtcNow,
        });

        db.TutorChatMessages.Add(new TutorChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = sessionId,
            TurnNumber = 1,
            Role = "student",
            Modality = "text",
            Language = "ar",
            ContentText = "مثال سؤال",
            CreatedAt = DateTime.UtcNow,
        });

        db.VoiceCaptures.Add(new VoiceCapture
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = sessionId,
            BlobReference = $"voice/{tenantId}",
            DurationMs = 1500,
            RetentionUntil = DateTime.UtcNow.AddDays(30),
        });

        db.QuizSessions.Add(new QuizSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = sessionId,
            SubjectId = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow,
        });

        db.MockTestSessions.Add(new MockTestSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = sessionId,
            SubjectId = Guid.NewGuid(),
            TimeLimitSeconds = 3600,
            ServerStartedAt = DateTime.UtcNow,
            ServerDeadlineAt = DateTime.UtcNow.AddHours(1),
        });

        db.HomeworkHelpSubmissions.Add(new HomeworkHelpSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = sessionId,
            InputModality = "text",
            TextPayload = "نص الواجب",
            RetentionUntil = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
        });

        db.WhiteboardSessions.Add(new WhiteboardSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = sessionId,
            SubjectId = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow,
        });

        db.SessionEvents.Add(new SessionEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = sessionId,
            CorrelationId = Guid.NewGuid(),
            EventKind = "session_start",
            EventPayload = "{}",
            CurriculumScope = "{}",
            PlanTierSnapshot = "premium",
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static async Task AssertOnlyTenantVisibleAsync(
        MuallimiDbContext db, Guid visible, Guid hidden)
    {
        var profiles = await db.StudentProfiles.ToListAsync();
        Assert.All(profiles, p => Assert.Equal(visible, p.TenantId));
        Assert.DoesNotContain(profiles, p => p.TenantId == hidden);

        var sessions = await db.StudentSessions.ToListAsync();
        Assert.All(sessions, s => Assert.Equal(visible, s.TenantId));

        var viewers = await db.LessonViewerStates.ToListAsync();
        Assert.All(viewers, v => Assert.Equal(visible, v.TenantId));

        var chats = await db.TutorChatMessages.ToListAsync();
        Assert.All(chats, c => Assert.Equal(visible, c.TenantId));

        var voices = await db.VoiceCaptures.ToListAsync();
        Assert.All(voices, v => Assert.Equal(visible, v.TenantId));

        var quizzes = await db.QuizSessions.ToListAsync();
        Assert.All(quizzes, q => Assert.Equal(visible, q.TenantId));

        var mocks = await db.MockTestSessions.ToListAsync();
        Assert.All(mocks, m => Assert.Equal(visible, m.TenantId));

        var homework = await db.HomeworkHelpSubmissions.ToListAsync();
        Assert.All(homework, h => Assert.Equal(visible, h.TenantId));

        var whiteboards = await db.WhiteboardSessions.ToListAsync();
        Assert.All(whiteboards, w => Assert.Equal(visible, w.TenantId));

        var events = await db.SessionEvents.ToListAsync();
        Assert.All(events, e => Assert.Equal(visible, e.TenantId));

        // And prove the hidden rows exist when filters are bypassed, so a
        // green pass above isn't just an empty database.
        var all = await db.StudentProfiles.IgnoreQueryFilters().ToListAsync();
        Assert.Contains(all, p => p.TenantId == hidden);
    }

    private sealed class AmbientTenantAccessor : IDbTenantContextAccessor
    {
        public Guid? CurrentTenantId { get; set; }
    }
}
