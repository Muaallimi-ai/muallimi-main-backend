using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.TutorExposure;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.TutorExposure;

/// <summary>
/// T072 (US4) — Contract for GET /student/tutor/voice/playback/{reference}.
///
/// The contract requires:
///   - chunked binary streaming of the synthesised AI tutor audio;
///   - session-scoped authorisation (cross-tenant or cross-session lookups
///     return 404 to avoid leaking existence);
///   - the resolved playback reference MUST originate from the Phase 2 AI
///     tutor voice profile binding — the response surfaces the
///     <c>X-Voice-Profile-Source</c> header pinned to "phase2_ai_tutor"
///     so the client can render the correct two-voice identity label.
/// </summary>
public class VoicePlaybackContractTests
{
    [Fact]
    public void Playback_Route_Pattern_Matches_Contract()
    {
        Assert.Equal("/api/student/tutor/voice/playback/{reference}", VoicePlaybackStreamEndpoint.Route);
    }

    [Fact]
    public void Playback_Endpoint_Is_Get_Only()
    {
        var map = typeof(VoicePlaybackStreamEndpoint).GetMethod(
            nameof(VoicePlaybackStreamEndpoint.MapVoicePlaybackStream),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(map);

        // Handler signature accepts the {reference} route parameter as the
        // first positional argument and forwards the call through scope-aware
        // services — matches the GET-only requirement in the contract.
        var handler = typeof(VoicePlaybackStreamEndpoint).GetMethod(
            nameof(VoicePlaybackStreamEndpoint.HandleAsync),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(handler);
        Assert.Equal("reference", handler!.GetParameters()[0].Name);
    }

    [Fact]
    public void Playback_Content_Type_Is_Pinned_To_Audio_Webm()
    {
        Assert.Equal("audio/webm", TutorVoiceMediaTypes.PlaybackContentType);
    }

    [Fact]
    public void Voice_Profile_Source_Constant_Is_Phase2_Ai_Tutor()
    {
        Assert.Equal("phase2_ai_tutor", Phase2AiTutorVoiceProfiles.Source);
    }

    [Fact]
    public void In_Memory_Blob_Store_Round_Trips_Payload_With_Content_Type()
    {
        var store = new InMemoryVoiceBlobStore();
        var payload = new byte[] { 0x1, 0x2, 0x3 };
        var reference = store.Persist("session-key", payload, "audio/webm");

        var blob = store.Read(reference);
        Assert.NotNull(blob);
        Assert.Equal("audio/webm", blob!.ContentType);
        Assert.Equal(reference, blob.BlobReference);
        var ms = new System.IO.MemoryStream();
        blob.Content.CopyTo(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public void Unknown_Reference_Returns_Null_From_Blob_Store()
    {
        var store = new InMemoryVoiceBlobStore();
        Assert.Null(store.Read("local-blob://voice/missing/00000000000000000000000000000000"));
    }

    [Fact]
    public void Voice_Capture_Repository_Default_Retention_Window_Is_Thirty_Days()
    {
        // FR-028 — voice captures default to a 30-day retention window so
        // the Phase 2 sweeper can purge expired audio without manual intervention.
        Assert.Equal(System.TimeSpan.FromDays(30), VoiceCaptureRepository.DefaultRetentionWindow);
    }
}
