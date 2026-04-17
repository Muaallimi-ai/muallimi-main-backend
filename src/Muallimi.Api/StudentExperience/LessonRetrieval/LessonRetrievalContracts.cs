using System;
using System.Collections.Generic;

namespace Muallimi.Api.StudentExperience.LessonRetrieval;

/// <summary>
/// T047 / T048 (US2) — Response DTOs for the Phase 3 Study mode lesson
/// viewer retrieval contract. Shapes mirror
/// <c>specs/005-student-learning-experience/contracts/lesson-viewer-retrieval-contract.md</c>
/// and the names align with the contract's snake_case fields via the
/// pipeline-wide JSON naming policy.
/// </summary>
public sealed record SubjectsListResponse(
    Guid SessionId,
    IReadOnlyList<SubjectListItem> Subjects);

public sealed record SubjectListItem(
    Guid SubjectId,
    string DisplayNameAr,
    string DisplayNameEn,
    int ChapterCount,
    string PlanGate);

public sealed record ChaptersListResponse(
    Guid SubjectId,
    IReadOnlyList<ChapterListItem> Chapters);

public sealed record ChapterListItem(
    Guid ChapterId,
    string DisplayNameAr,
    string DisplayNameEn,
    IReadOnlyList<TopicListItem> Topics);

public sealed record TopicListItem(
    Guid TopicId,
    string DisplayNameAr,
    string DisplayNameEn,
    int LessonCount);

public sealed record LessonViewerResponse(
    Guid LessonId,
    Guid SubjectId,
    Guid ChapterId,
    Guid TopicId,
    string DisplayNameAr,
    string DisplayNameEn,
    IReadOnlyList<LessonContentBlock> ContentBlocks,
    string TeacherVoiceProfileId,
    string TeacherVoiceProfileSource,
    IReadOnlyList<LessonEvidenceRef> EvidenceRefs,
    string ApprovalState,
    Guid CorrelationId);

public sealed record LessonContentBlock(
    string BlockType,
    string Language,
    string? TextPayload,
    string? MediaReference,
    string? CaptionTrackReference,
    string? TranscriptReference);

public sealed record LessonEvidenceRef(
    Guid ChunkId,
    string SourceUri);
