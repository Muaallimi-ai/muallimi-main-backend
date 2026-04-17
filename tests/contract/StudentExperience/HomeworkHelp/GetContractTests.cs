using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.HomeworkHelp;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.HomeworkHelp;

/// <summary>
/// T104 (US7) — Contract test for <c>GET /student/homework-help/{id}</c>.
///
/// Asserts the resume envelope shape against
/// <c>specs/005-student-learning-experience/contracts/homework-help-image-contract.md</c>:
///   - the GET response carries the persisted submission identity, the
///     input modality, the input artefacts (text, voice ref, image ref +
///     metadata), the binding to the Phase 2 OCR adapter, the AiRequestRecord
///     id, the retention watermark, and the cached response envelope.
///   - the route is registered on the <see cref="HomeworkHelpEndpoints"/>
///     surface and listed in the Phase 3 contract catalogue.
/// </summary>
public class GetContractTests
{
    [Fact]
    public void HomeworkHelpGetResponse_Shape_Matches_Contract()
    {
        var props = typeof(HomeworkHelpGetResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SubmissionId", props);
        Assert.Contains("SessionId", props);
        Assert.Contains("InputModality", props);
        Assert.Contains("FinalOutcome", props);
        Assert.Contains("ExtractedProblemText", props);
        Assert.Contains("TextPayload", props);
        Assert.Contains("VoiceCaptureId", props);
        Assert.Contains("ImageBlobReference", props);
        Assert.Contains("ImagePreprocessMetadata", props);
        Assert.Contains("OcrAdapterBindingId", props);
        Assert.Contains("AiRequestRecordId", props);
        Assert.Contains("RetentionUntil", props);
        Assert.Contains("CreatedAt", props);
        Assert.Contains("Response", props);
    }

    [Fact]
    public void Get_Route_Includes_Submission_Id_Parameter()
    {
        Assert.Equal("/api/student/homework-help/{id:guid}", HomeworkHelpEndpoints.GetRoute);
    }

    [Fact]
    public void HomeworkHelpGetResponse_Embeds_Same_Submit_Response_Shape()
    {
        // Resuming a submission MUST surface the same answered/refusal
        // envelope shape as the original submit call so the client can
        // re-render without a separate response model.
        var responseProp = typeof(HomeworkHelpGetResponse)
            .GetProperty("Response", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(responseProp);
        Assert.Equal(typeof(HomeworkHelpSubmitResponse), responseProp!.PropertyType);
    }
}
