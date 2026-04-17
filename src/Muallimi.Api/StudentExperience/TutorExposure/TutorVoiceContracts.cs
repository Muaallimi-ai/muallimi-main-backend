using System;
using System.Collections.Generic;

namespace Muallimi.Api.StudentExperience.TutorExposure;

/// <summary>
/// T073/T071/T072 (US4) — Wire shapes for the student-facing voice tutor
/// surface (POST /student/tutor/voice and GET /student/tutor/voice/playback).
///
/// Mirrors the contract in
/// <c>specs/005-student-learning-experience/contracts/student-tutor-chat-contract.md</c>.
/// The facade never generates audio itself — it routes through the Phase 2
/// AI tutor voice profile binding (FR-019). The voice_profile_source field is
/// pinned to the constant <see cref="Phase2AiTutorVoiceProfiles.Source"/> so
/// the analytics dashboard can key on it without parsing heuristics.
/// </summary>
public sealed record TutorVoiceResponse(
    int TurnNumber,
    string TranscriptText,
    string AnswerText,
    string VoicePlaybackReference,
    string VoiceProfileId,
    string VoiceProfileSource,
    string FinalOutcome,
    string ConfidenceSignal,
    IReadOnlyList<TutorTextEvidenceRef> EvidenceRefs,
    string AiRequestRecordId,
    string? RefusalTextAr,
    string? RefusalTextEn);

/// <summary>
/// Multipart form parts accepted by POST /student/tutor/voice.
///
/// Surfaced as a discrete record so the contract test can assert on the same
/// field names the endpoint reads from the multipart body.
/// </summary>
public sealed record TutorVoiceRequestParts(
    Guid SessionId,
    Guid CorrelationId,
    int TurnNumber,
    string Codec,
    string TutorLanguage,
    string AudioBlobFieldName)
{
    public const string DefaultAudioBlobFieldName = "audio_blob";
}

/// <summary>
/// Pinned constants for the Phase 2 AI tutor voice profile that the Phase 3
/// facade rebinds to on every voice answer. The identifier mirrors the
/// ai-service <c>LocalVoiceProfileAdapter.AiTutorVoiceProfileId</c> so any
/// drift trips the cross-phase voice-profile-identity test (T080).
///
/// FR-019: this set MUST remain disjoint from
/// <c>Phase1TeacherVoiceProfiles.All</c> at all times.
/// </summary>
public static class Phase2AiTutorVoiceProfiles
{
    public const string Source = "phase2_ai_tutor";
    public const string DefaultProfileId = "ai-tutor-voice-v1";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        DefaultProfileId,
    };

    public static bool IsAiTutorProfileId(string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) && All.Contains(candidate);
}

/// <summary>
/// Allowed audio MIME types for the voice playback stream. The facade always
/// emits the resolved type back on the playback response; the constant set
/// is exposed so the contract test can pin the surface.
/// </summary>
public static class TutorVoiceMediaTypes
{
    public const string PlaybackContentType = "audio/webm";

    public static readonly IReadOnlySet<string> AcceptedCaptureCodecs = new HashSet<string>
    {
        "audio/webm;codecs=opus",
        "audio/webm",
        "audio/ogg;codecs=opus",
    };

    public static bool IsAcceptedCaptureCodec(string? codec) =>
        !string.IsNullOrWhiteSpace(codec) && AcceptedCaptureCodecs.Contains(codec);
}
