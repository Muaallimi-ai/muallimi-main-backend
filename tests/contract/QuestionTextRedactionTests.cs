using System.Text.Json;
using Muallimi.Api.AiOperations;
using Muallimi.Api.Tenancy;
using Muallimi.Domain.AiOperations;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T097 (US6) — Role-based <c>question_text</c> redaction contract. Only the
/// <c>incident_investigation</c> role sees the minimal
/// <c>question_text_preview</c>; every other role (including the default
/// <c>operator</c>) must receive the literal
/// <c>[redacted]</c> marker. The role is carried on the
/// <c>X-Actor-Role</c> header and resolved via the AI-ops endpoint helper.
/// </summary>
public class QuestionTextRedactionTests
{
    [Fact]
    public void Operator_Role_Gets_Redacted_Preview()
    {
        var record = MakeRecord("ما هو الكسر؟");
        var projected = AiOperationsEndpoints.ProjectRecord(record, AiOperationsAuthorizationFilter.OperatorRole);
        var preview = GetProperty(projected, "question_text_preview");
        Assert.Equal(AiOperationsEndpoints.RedactedPreview, preview);
    }

    [Fact]
    public void IncidentInvestigation_Role_Gets_Raw_Preview()
    {
        const string question = "ما هو الكسر؟";
        var record = MakeRecord(question);
        var projected = AiOperationsEndpoints.ProjectRecord(record, AiOperationsAuthorizationFilter.IncidentInvestigationRole);
        var preview = GetProperty(projected, "question_text_preview");
        Assert.Equal(question, preview);
    }

    [Fact]
    public void Unknown_Role_Falls_Back_To_Redacted()
    {
        var record = MakeRecord("could leak sensitive content");
        var projected = AiOperationsEndpoints.ProjectRecord(record, role: null);
        var preview = GetProperty(projected, "question_text_preview");
        Assert.Equal(AiOperationsEndpoints.RedactedPreview, preview);

        projected = AiOperationsEndpoints.ProjectRecord(record, role: "student");
        preview = GetProperty(projected, "question_text_preview");
        Assert.Equal(AiOperationsEndpoints.RedactedPreview, preview);
    }

    [Fact]
    public void Null_Preview_Is_Still_Redacted_For_Non_Investigation_Role()
    {
        var record = MakeRecord(null);
        var projected = AiOperationsEndpoints.ProjectRecord(record, AiOperationsAuthorizationFilter.OperatorRole);
        var preview = GetProperty(projected, "question_text_preview");
        Assert.Equal(AiOperationsEndpoints.RedactedPreview, preview);
    }

    [Fact]
    public void Projection_Preserves_All_Other_Record_Fields()
    {
        var record = MakeRecord("ما هو الكسر؟");
        var projected = AiOperationsEndpoints.ProjectRecord(record, AiOperationsAuthorizationFilter.OperatorRole);
        Assert.Equal(record.RecordId, GetProperty(projected, "record_id"));
        Assert.Equal(record.CorrelationId, GetProperty(projected, "correlation_id"));
        Assert.Equal(record.CurriculumType, GetProperty(projected, "curriculum_type"));
        Assert.Equal(record.FinalOutcome, GetProperty(projected, "final_outcome"));
        Assert.Equal(record.InputTokenCount, GetProperty(projected, "input_token_count"));
        Assert.Equal(record.OutputTokenCount, GetProperty(projected, "output_token_count"));
    }

    // ── Helpers ──

    private static AiRequestRecord MakeRecord(string? preview) => new()
    {
        RecordId = Guid.NewGuid(),
        CorrelationId = "corr-1",
        SessionId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        CurriculumType = "Moe",
        Grade = "Grade7",
        Subject = "Mathematics",
        TutorLanguage = "Ar",
        SessionMode = "Study",
        Stages = "[]",
        RoutingDecision = "{}",
        InputTokenCount = 120,
        OutputTokenCount = 60,
        LatencyMs = 300,
        CacheMatchScore = null,
        FinalOutcome = "answered",
        QuestionTextPreview = preview,
        PromptVersionsUsed = "[]",
        OccurredAt = DateTime.UtcNow,
    };

    private static object? GetProperty(object anonymous, string propertyName)
    {
        var property = anonymous.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return property!.GetValue(anonymous);
    }
}
