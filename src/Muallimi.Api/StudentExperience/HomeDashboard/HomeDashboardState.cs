using System;
using System.Collections.Generic;

namespace Muallimi.Api.StudentExperience.HomeDashboard;

/// <summary>
/// T030 (US1) — Response shape for the home dashboard facade, mirroring the
/// <c>student-experience-contract.md</c>. The frontend binds the streamed
/// response directly into <c>SessionScopeProvider</c> so every downstream
/// mode entry can look up the tenant, curriculum, language, and plan tier
/// without a second round trip.
///
/// Wire format is snake_case per the Phase 3 contract. Enforced globally by
/// `ConfigureHttpJsonOptions` in Program.cs (SnakeCaseLower naming policy).
/// </summary>
public sealed record HomeDashboardState(
    Guid SessionId,
    Guid CorrelationId,
    Guid TenantId,
    string TutorLanguage,
    string CurriculumType,
    string Grade,
    string PlanTierSnapshot,
    string DeviceClass,
    IReadOnlyList<ModeTileState> ModeTileStates,
    ResumeTarget? ResumeTarget,
    IReadOnlyList<RecommendedTopic> RecommendedTopics,
    string GreetingTextAr,
    string GreetingTextEn,
    DateTime RenderedAt);

public sealed record ModeTileState(
    string Mode,
    bool Enabled,
    string? Reason,
    string PlanGate,
    string SubjectGate);

public sealed record ResumeTarget(string Mode, string DeepLink);

public sealed record RecommendedTopic(
    Guid TopicId,
    Guid SubjectId,
    string DisplayNameAr,
    string DisplayNameEn);

public sealed record SessionStartRequest(
    Guid StudentProfileId,
    string DeviceClass,
    string PreferredLanguage);

public sealed record SessionStartResponse(
    Guid SessionId,
    Guid CorrelationId,
    string TutorLanguage,
    string CurriculumType,
    string Grade,
    string PlanTierSnapshot,
    IReadOnlyList<ModeTileState> ModeTileStates,
    ResumeTarget? ResumeTarget,
    IReadOnlyList<RecommendedTopic> RecommendedTopics,
    string GreetingTextAr,
    string GreetingTextEn);

public sealed record SessionModeRequest(
    Guid SessionId,
    string TargetMode,
    ScopeHint? TargetScope);

public sealed record ScopeHint(
    Guid? SubjectId,
    Guid? ChapterId,
    Guid? TopicId,
    Guid? LessonId);

public abstract record SessionModeResponse(Guid SessionId);

public sealed record SessionModeAcceptedResponse(
    Guid SessionId,
    string ActiveMode,
    ScopeHint? ActiveScope,
    DateTime TransitionAt) : SessionModeResponse(SessionId);

public sealed record SessionModeRefusedResponse(
    Guid SessionId,
    string RefusalReason,
    string RefusalTextAr,
    string RefusalTextEn) : SessionModeResponse(SessionId);

public sealed record SessionEndRequest(Guid SessionId, string EndReason);

public sealed record SessionEndResponse(Guid SessionId, DateTime EndedAt);

public sealed record PlanGateSnapshotResponse(
    Guid SessionId,
    string PlanTierSnapshot,
    IReadOnlyList<ModeTileState> ModeTileStates,
    DateTime ExpiresAt);
