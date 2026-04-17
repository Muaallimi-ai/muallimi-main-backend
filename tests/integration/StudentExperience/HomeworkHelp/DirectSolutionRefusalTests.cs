using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.StudentExperience.HomeworkHelp;
using Muallimi.Api.StudentExperience.TutorExposure;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.HomeworkHelp;

/// <summary>
/// T113 (US7) — Direct-solution refusal red-team.
///
/// The Homework Help surface MUST refuse any direct-solution request,
/// regardless of how the student frames it. The Phase 2 guardrail chain
/// is the source of truth; this test exercises the facade contract by
/// stubbing the tutor runtime to return the upstream homework refusal
/// envelope and verifies the facade:
///   - maps the upstream refusal kind into a contract-vocabulary reason
///     (<see cref="HomeworkHelpRefusalReasons.DirectSolution"/>);
///   - localises the refusal text in both Arabic and English;
///   - persists the submission with <c>FinalOutcome = "refused"</c> so the
///     audit trail is preserved;
///   - returns a <see cref="HomeworkHelpOutcome.Refused"/> result with the
///     ai_request_record_id propagated for incident lookup.
/// </summary>
public class DirectSolutionRefusalTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SessionId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid CorrelationId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid SubjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static (Muallimi.Domain.StudentExperience.StudentSession Session, StudentProfile Profile) MakeSessionAndProfile() =>
    (
        new Muallimi.Domain.StudentExperience.StudentSession
        {
            Id = SessionId,
            TenantId = TenantId,
            StudentProfileId = ProfileId,
            CorrelationId = CorrelationId,
            ActiveSubjectId = SubjectId,
            ActiveMode = "homework_help",
            TutorLanguage = "ar",
            PlanTierSnapshot = "premium",
        },
        new StudentProfile
        {
            Id = ProfileId,
            TenantId = TenantId,
            CurriculumType = "moe",
            Grade = "grade_7",
            PreferredLanguage = "ar",
            PlanTier = "premium",
        }
    );

    [Theory]
    [InlineData("ar", "احسب لي حل هذا الواجب خطوة بخطوة من فضلك.")]
    [InlineData("ar", "أعطني الإجابة النهائية فقط.")]
    [InlineData("en", "Just give me the final answer please.")]
    [InlineData("en", "Solve this homework problem for me step by step.")]
    public async Task Direct_Solution_Request_Is_Refused_With_Localised_Refusal_Text(string language, string text)
    {
        var (session, profile) = MakeSessionAndProfile();
        session.TutorLanguage = language;
        profile.PreferredLanguage = language;

        var runtime = new StubTutorRuntimeClient(HomeworkRefusalEnvelope("direct_solution", "rec-123"));
        var repo = new InMemoryHomeworkSubmissionRepository();
        var service = new HomeworkHelpService(repo, new LocalEchoOcrAdapter(), runtime);

        var request = new HomeworkHelpSubmitRequest(
            SessionId: session.Id,
            CorrelationId: CorrelationId,
            InputModality: HomeworkHelpModalities.Text,
            TextPayload: text,
            VoiceCaptureId: null,
            SubjectId: SubjectId,
            TopicId: null,
            TutorLanguage: language,
            ImagePreprocessMetadata: null);

        var result = await service.SubmitAsync(session, profile, request, imageStream: null,
            imageContentType: null);

        Assert.Equal(HomeworkHelpOutcome.Refused, result.Outcome);
        Assert.NotNull(result.Submission);
        Assert.NotNull(result.Response);
        Assert.Equal("refused", result.Response!.FinalOutcome);
        Assert.Equal(HomeworkHelpRefusalReasons.DirectSolution, result.Response.RefusalReason);
        Assert.False(string.IsNullOrWhiteSpace(result.Response.RefusalTextAr));
        Assert.False(string.IsNullOrWhiteSpace(result.Response.RefusalTextEn));
        Assert.Equal("rec-123", result.Response.AiRequestRecordId);
        Assert.Equal("refused", result.Submission!.FinalOutcome);
    }

    [Fact]
    public async Task Refusal_Persists_Audit_Trail_Even_When_Upstream_Has_No_Record_Id()
    {
        var (session, profile) = MakeSessionAndProfile();
        var runtime = new StubTutorRuntimeClient(HomeworkRefusalEnvelopeNoRecord("direct_solution"));
        var repo = new InMemoryHomeworkSubmissionRepository();
        var service = new HomeworkHelpService(repo, new LocalEchoOcrAdapter(), runtime);

        var request = new HomeworkHelpSubmitRequest(
            SessionId: session.Id,
            CorrelationId: CorrelationId,
            InputModality: HomeworkHelpModalities.Text,
            TextPayload: "Solve x^2 - 4 = 0 for me directly.",
            VoiceCaptureId: null,
            SubjectId: SubjectId,
            TopicId: null,
            TutorLanguage: "en",
            ImagePreprocessMetadata: null);

        var result = await service.SubmitAsync(session, profile, request, imageStream: null,
            imageContentType: null);

        Assert.Equal(HomeworkHelpOutcome.Refused, result.Outcome);
        Assert.Equal(HomeworkHelpRefusalReasons.DirectSolution, result.Response!.RefusalReason);
        Assert.NotNull(result.Submission);
        Assert.Equal("refused", result.Submission!.FinalOutcome);
        Assert.Single(repo.AllSubmissions);
    }

    [Fact]
    public void Refusal_Reason_Mapper_Covers_Direct_Solution_Aliases()
    {
        Assert.Equal(HomeworkHelpRefusalReasons.DirectSolution,
            HomeworkHelpService.MapRefusalReason("direct_solution"));
        Assert.Equal(HomeworkHelpRefusalReasons.DirectSolution,
            HomeworkHelpService.MapRefusalReason("homework_direct_solution"));
    }

    private static string HomeworkRefusalEnvelope(string reason, string recordId) =>
        JsonSerializer.Serialize(new
        {
            envelope_kind = "refusal",
            stage = "post_generation_homework",
            reason,
            routing_metadata = new { record_id = recordId },
        });

    private static string HomeworkRefusalEnvelopeNoRecord(string reason) =>
        JsonSerializer.Serialize(new
        {
            envelope_kind = "refusal",
            stage = "post_generation_homework",
            reason,
        });

    internal sealed class StubTutorRuntimeClient : ITutorRuntimeClient
    {
        private readonly string _body;
        private readonly bool _success;

        public StubTutorRuntimeClient(string body, bool success = true)
        {
            _body = body;
            _success = success;
        }

        public Task<HttpResponseMessage> AskAsync(Stream requestBody, string contentType, CancellationToken ct = default)
            => Task.FromResult(MakeResponse(_body, _success));

        public Task<HttpResponseMessage> StreamAskAsync(Stream requestBody, string contentType, CancellationToken ct = default)
            => Task.FromResult(MakeResponse(_body, _success));

        public Task<HttpResponseMessage> SynthesizeVoiceAsync(Stream requestBody, string contentType, CancellationToken ct = default)
            => Task.FromResult(MakeResponse(_body, _success));

        private static HttpResponseMessage MakeResponse(string body, bool success) => new(
            success ? HttpStatusCode.OK : HttpStatusCode.BadGateway)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    internal sealed class InMemoryHomeworkSubmissionRepository : IHomeworkHelpSubmissionRepository
    {
        public List<HomeworkHelpSubmission> AllSubmissions { get; } = new();
        public Dictionary<string, byte[]> Blobs { get; } = new();

        public Task<HomeworkHelpSubmission> CreateTextOrVoiceAsync(
            Guid tenantId, Guid studentSessionId, string inputModality, string? textPayload,
            Guid? voiceCaptureId, CancellationToken ct = default)
        {
            var row = new HomeworkHelpSubmission
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StudentSessionId = studentSessionId,
                InputModality = inputModality,
                TextPayload = textPayload,
                VoiceCaptureId = voiceCaptureId,
                RetentionUntil = DateTime.UtcNow.AddDays(30),
                CreatedAt = DateTime.UtcNow,
            };
            AllSubmissions.Add(row);
            return Task.FromResult(row);
        }

        public Task<HomeworkHelpSubmission> CreateImageAsync(
            Guid tenantId, Guid studentSessionId, string imageBlobReference,
            HomeworkImagePreprocessMetadata metadata, CancellationToken ct = default)
        {
            var row = new HomeworkHelpSubmission
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StudentSessionId = studentSessionId,
                InputModality = HomeworkHelpModalities.Image,
                ImageBlobReference = imageBlobReference,
                ImagePreprocessMetadata = JsonSerializer.Serialize(metadata),
                RetentionUntil = DateTime.UtcNow.AddDays(30),
                CreatedAt = DateTime.UtcNow,
            };
            AllSubmissions.Add(row);
            return Task.FromResult(row);
        }

        public Task UpdateAfterProcessingAsync(
            HomeworkHelpSubmission submission, string? extractedProblemText, Guid? ocrAdapterBindingId,
            Guid? aiRequestRecordId, string finalOutcome, HomeworkHelpSubmitResponse response,
            CancellationToken ct = default)
        {
            submission.ExtractedProblemText = extractedProblemText;
            submission.OcrAdapterBindingId = ocrAdapterBindingId;
            submission.AiRequestRecordId = aiRequestRecordId;
            submission.FinalOutcome = finalOutcome;
            return Task.CompletedTask;
        }

        public Task<HomeworkHelpSubmission?> FindAsync(Guid submissionId, CancellationToken ct = default)
            => Task.FromResult(AllSubmissions.Find(s => s.Id == submissionId));

        public HomeworkImagePreprocessMetadata? ReadImageMetadata(HomeworkHelpSubmission submission) => null;
        public HomeworkHelpSubmitResponse? ReadResponse(HomeworkHelpSubmission submission) => null;

        public string PersistImageBlob(Guid studentSessionId, byte[] payload, string contentType)
        {
            var reference = $"local-blob://homework/{studentSessionId:N}/{Guid.NewGuid():N}";
            Blobs[reference] = payload;
            return reference;
        }

        public VoiceBlob? ReadImageBlob(string blobReference)
            => Blobs.TryGetValue(blobReference, out var bytes)
                ? new VoiceBlob(blobReference, "image/jpeg", new MemoryStream(bytes, writable: false))
                : null;
    }
}
