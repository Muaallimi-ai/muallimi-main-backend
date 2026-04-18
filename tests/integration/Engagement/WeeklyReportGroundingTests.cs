using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T085 (US3) — Integration test for grounding / no-fabrication.
///
/// Every <c>evidence_refs</c> entry and every <c>top_focus_areas</c>
/// entry persisted on a weekly report MUST resolve to a Phase 1
/// curriculum node / Phase 3 session event the child actually touched.
/// We seed progress records + focus areas for the window, generate the
/// report, and assert every evidence id and focus-area id round-trips
/// back to the seeded rows — no fabricated ids.
/// </summary>
public class WeeklyReportGroundingTests
{
    [Fact]
    public async Task Evidence_Refs_Only_Cite_Progress_Records_The_Student_Touched()
    {
        var harness = new WeeklyReportTestHarness();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var strangerStudentId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var seededIds = new[]
        {
            SeedProgress(harness, tenantId, studentId, start.AddDays(1), "session_start"),
            SeedProgress(harness, tenantId, studentId, start.AddDays(2), "quiz_answered"),
            SeedProgress(harness, tenantId, studentId, start.AddDays(3), "lesson_view"),
        };
        // Out-of-window noise: same tenant, different student.
        SeedProgress(harness, tenantId, strangerStudentId, start.AddDays(2), "quiz_answered");
        // Out-of-window same student but before the window opens.
        SeedProgress(harness, tenantId, studentId, start.AddDays(-3), "session_start");
        await harness.Db.SaveChangesAsync();

        var result = await harness.Generator.GenerateAsync(
            tenantId, studentId, start, end,
            correlationId: "corr-grounding",
            forceRegenerate: false);

        var report = await harness.Reports.GetByIdAsync(tenantId, result.WeeklyReportId);
        Assert.NotNull(report);

        using var doc = JsonDocument.Parse(report!.EvidenceRefs);
        var citedIds = doc.RootElement.EnumerateArray()
            .Select(el => el.GetProperty("progress_record_id").GetGuid())
            .ToArray();

        Assert.Equal(3, citedIds.Length);
        foreach (var id in citedIds)
        {
            Assert.Contains(id, seededIds);
        }
    }

    [Fact]
    public async Task Focus_Areas_Snapshotted_Are_Real_Rows_For_The_Student()
    {
        var harness = new WeeklyReportTestHarness();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var focusId = Guid.NewGuid();
        harness.Db.FocusAreas.Add(new FocusArea
        {
            FocusAreaId = focusId,
            TenantId = tenantId,
            StudentId = studentId,
            CurriculumType = "moe",
            SubjectId = Guid.NewGuid(),
            ChapterId = Guid.NewGuid(),
            TopicId = Guid.NewGuid(),
            SignalSummary = "{}",
            RationaleAr = "تدريب إضافي على الكسور",
            RationaleEn = "More practice on fractions",
            SuggestedNextStep = "{\"phase3_mode\":\"solve_questions\",\"deep_link\":\"/solve-questions\"}",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            ComputedAt = start.AddDays(2),
            ValidUntil = end.AddDays(7),
            CorrelationId = Guid.NewGuid().ToString("D"),
        });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Generator.GenerateAsync(
            tenantId, studentId, start, end,
            correlationId: "corr-focus-grounding",
            forceRegenerate: false);

        var report = await harness.Reports.GetByIdAsync(tenantId, result.WeeklyReportId);
        Assert.NotNull(report);

        using var doc = JsonDocument.Parse(report!.TopFocusAreas);
        var ids = doc.RootElement.EnumerateArray()
            .Select(el => el.GetProperty("focus_area_id").GetGuid())
            .ToArray();
        Assert.Single(ids);
        Assert.Equal(focusId, ids[0]);
    }

    private static Guid SeedProgress(
        WeeklyReportTestHarness harness,
        Guid tenantId,
        Guid studentId,
        DateTime occurredAt,
        string kind)
    {
        var id = Guid.NewGuid();
        harness.Db.ProgressRecords.Add(new ProgressRecord
        {
            ProgressRecordId = id,
            TenantId = tenantId,
            StudentId = studentId,
            SourceEventId = Guid.NewGuid().ToString("D"),
            EventKind = kind,
            CurriculumScope = "{\"curriculum_type\":\"moe\"}",
            Payload = "{}",
            CorrelationId = Guid.NewGuid().ToString("D"),
            OccurredAt = DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc),
            IngestedAt = DateTime.UtcNow,
        });
        return id;
    }
}
