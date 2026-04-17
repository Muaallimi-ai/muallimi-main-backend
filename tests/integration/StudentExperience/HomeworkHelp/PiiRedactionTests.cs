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
/// T114 (US7) — PII redaction red-team for homework images.
///
/// FR-027 (constitution): face-flag metadata is a UX nudge, not a privacy
/// guarantee. The server MUST apply its own redaction policy before any
/// problem text leaves the facade for the tutor runtime. This test
/// exercises:
///   - the structural <see cref="HomeworkTextRedactor"/> for the high-frequency
///     PII shapes (email, phone, national id, URL, handle).
///   - the end-to-end image path: the OCR text gets redacted before the
///     facade calls the upstream tutor runtime; the request body MUST NOT
///     contain the original PII string.
///   - the EXIF safety check: an image submission with
///     <c>exif_stripped = false</c> MUST be refused with reason
///     <c>safety_pii</c>, even if the rest of the metadata looks valid.
/// </summary>
public class PiiRedactionTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000011");
    private static readonly Guid SessionId = Guid.Parse("00000000-0000-0000-0000-000000000012");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000013");

    [Theory]
    [InlineData("Contact me at student@example.com if stuck.", "email")]
    [InlineData("Call 0512345678 for help.", "phone")]
    [InlineData("See https://leak.example.com/answers for the key.", "url")]
    [InlineData("Ping @studenthandle on the forum.", "handle")]
    public void Redactor_Strips_Common_Pii_Categories(string input, string expectedCategory)
    {
        var (text, categories) = HomeworkTextRedactor.Redact(input);
        Assert.Contains("[REDACTED]", text);
        Assert.Contains(expectedCategory, categories);
    }

    [Fact]
    public void Redactor_Strips_Standalone_National_Id_As_A_Phone_Like_Number()
    {
        // A 10-digit Gulf national ID is structurally indistinguishable from
        // a 10-digit Gulf mobile, so the redactor strips it via the phone
        // pattern. The safety property (no PII reaches the runtime) holds
        // regardless of which category label the redactor chose.
        var (text, categories) = HomeworkTextRedactor.Redact("My ID number is 1098765432.");
        Assert.DoesNotContain("1098765432", text);
        Assert.Contains("[REDACTED]", text);
        Assert.NotEmpty(categories);
    }

    [Fact]
    public void Redactor_Returns_Input_When_No_Pii_Present()
    {
        var input = "What is the value of x in the equation 2x + 3 = 11?";
        var (text, categories) = HomeworkTextRedactor.Redact(input);
        Assert.Equal(input, text);
        Assert.Empty(categories);
    }

    [Fact]
    public void Redactor_Handles_Multiple_Categories_In_One_String()
    {
        var input = "Email me at hi@x.com or call +966512345678 — see https://x.com/answers.";
        var (text, categories) = HomeworkTextRedactor.Redact(input);
        Assert.DoesNotContain("hi@x.com", text);
        Assert.DoesNotContain("+966512345678", text);
        Assert.DoesNotContain("https://x.com/answers", text);
        Assert.Contains("email", categories);
        Assert.Contains("phone", categories);
        Assert.Contains("url", categories);
    }

    [Fact]
    public async Task Image_Submission_With_Exif_Not_Stripped_Is_Refused_As_SafetyPii()
    {
        var (session, profile) = MakeSessionAndProfile();
        var runtime = new RecordingTutorRuntimeClient(AnswerEnvelope("ok"));
        var repo = new InMemoryRepo();
        var service = new HomeworkHelpService(repo, new LocalEchoOcrAdapter(), runtime);

        var metadata = new HomeworkImagePreprocessMetadata(
            OriginalWidth: 4032, OriginalHeight: 3024,
            CompressedWidth: 1600, CompressedHeight: 1200,
            ExifStripped: false,
            FaceFlags: Array.Empty<HomeworkFaceFlag>(),
            ClientChecksum: "deadbeef");

        var request = new HomeworkHelpSubmitRequest(
            SessionId: session.Id,
            CorrelationId: session.CorrelationId,
            InputModality: HomeworkHelpModalities.Image,
            TextPayload: null,
            VoiceCaptureId: null,
            SubjectId: Guid.NewGuid(),
            TopicId: null,
            TutorLanguage: "ar",
            ImagePreprocessMetadata: metadata);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("homework problem text"));
        var result = await service.SubmitAsync(session, profile, request, imageStream: stream,
            imageContentType: "image/jpeg");

        Assert.Equal(HomeworkHelpOutcome.Refused, result.Outcome);
        Assert.Equal(HomeworkHelpRefusalReasons.SafetyPii, result.Response!.RefusalReason);
        Assert.False(runtime.WasCalled, "tutor runtime must not be called when EXIF strip is missing.");
    }

    [Fact]
    public async Task Image_Path_Redacts_Pii_From_Ocr_Text_Before_Calling_Tutor_Runtime()
    {
        var (session, profile) = MakeSessionAndProfile();
        var runtime = new RecordingTutorRuntimeClient(AnswerEnvelope("explanation"));
        var repo = new InMemoryRepo();
        var service = new HomeworkHelpService(repo, new LocalEchoOcrAdapter(), runtime);

        var metadata = new HomeworkImagePreprocessMetadata(
            OriginalWidth: 1024, OriginalHeight: 768,
            CompressedWidth: 1024, CompressedHeight: 768,
            ExifStripped: true,
            FaceFlags: Array.Empty<HomeworkFaceFlag>(),
            ClientChecksum: "abcd");

        var problemText = "Email tutor@school.example.com to verify. Solve 5x + 2 = 17.";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(problemText));

        var request = new HomeworkHelpSubmitRequest(
            SessionId: session.Id,
            CorrelationId: session.CorrelationId,
            InputModality: HomeworkHelpModalities.Image,
            TextPayload: null,
            VoiceCaptureId: null,
            SubjectId: Guid.NewGuid(),
            TopicId: null,
            TutorLanguage: "en",
            ImagePreprocessMetadata: metadata);

        await service.SubmitAsync(session, profile, request, imageStream: stream,
            imageContentType: "image/jpeg");

        Assert.True(runtime.WasCalled);
        Assert.False(string.IsNullOrEmpty(runtime.LastRequestBody));
        Assert.DoesNotContain("tutor@school.example.com", runtime.LastRequestBody!);
        Assert.Contains("[REDACTED]", runtime.LastRequestBody!);
    }

    private static (Muallimi.Domain.StudentExperience.StudentSession, StudentProfile) MakeSessionAndProfile() =>
    (
        new Muallimi.Domain.StudentExperience.StudentSession
        {
            Id = SessionId,
            TenantId = TenantId,
            StudentProfileId = ProfileId,
            CorrelationId = Guid.NewGuid(),
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

    private static string AnswerEnvelope(string answer) => JsonSerializer.Serialize(new
    {
        envelope_kind = "answer",
        answer_text = answer,
        confidence_signal = "high_confidence",
        evidence_refs = Array.Empty<object>(),
        routing_metadata = new { record_id = "rec-redact-1" },
    });

    private sealed class RecordingTutorRuntimeClient : ITutorRuntimeClient
    {
        private readonly string _body;
        public bool WasCalled { get; private set; }
        public string? LastRequestBody { get; private set; }

        public RecordingTutorRuntimeClient(string body) { _body = body; }

        public async Task<HttpResponseMessage> AskAsync(Stream requestBody, string contentType, CancellationToken ct = default)
        {
            WasCalled = true;
            using var reader = new StreamReader(requestBody, Encoding.UTF8);
            LastRequestBody = await reader.ReadToEndAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }

        public Task<HttpResponseMessage> StreamAskAsync(Stream requestBody, string contentType, CancellationToken ct = default)
            => AskAsync(requestBody, contentType, ct);

        public Task<HttpResponseMessage> SynthesizeVoiceAsync(Stream requestBody, string contentType, CancellationToken ct = default)
            => AskAsync(requestBody, contentType, ct);
    }

    private sealed class InMemoryRepo : IHomeworkHelpSubmissionRepository
    {
        private readonly Dictionary<string, byte[]> _blobs = new();
        private readonly List<HomeworkHelpSubmission> _rows = new();

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
            _rows.Add(row);
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
            _rows.Add(row);
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

        public Task<HomeworkHelpSubmission?> FindAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_rows.Find(r => r.Id == id));

        public HomeworkImagePreprocessMetadata? ReadImageMetadata(HomeworkHelpSubmission submission) => null;
        public HomeworkHelpSubmitResponse? ReadResponse(HomeworkHelpSubmission submission) => null;

        public string PersistImageBlob(Guid studentSessionId, byte[] payload, string contentType)
        {
            var reference = $"local-blob://homework/{studentSessionId:N}/{Guid.NewGuid():N}";
            _blobs[reference] = payload;
            return reference;
        }

        public VoiceBlob? ReadImageBlob(string blobReference)
            => _blobs.TryGetValue(blobReference, out var bytes)
                ? new VoiceBlob(blobReference, "image/jpeg", new MemoryStream(bytes, writable: false))
                : null;
    }
}
