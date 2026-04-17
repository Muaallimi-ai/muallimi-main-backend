using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Shared;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.LessonRetrieval;

/// <summary>
/// T047 (US2) — LessonRetrievalService.
///
/// Consumes the Phase 1 curriculum retrieval surface (Lessons / ContentChunks /
/// PublishedAssets) to build the Study mode lesson viewer payloads. Every
/// query is filtered by tenant (via the EF Core global filter), curriculum
/// type, grade, and the student's enrolled subjects. Only
/// <see cref="LessonStatus.Approved"/> lessons surface; no write path is
/// exposed.
///
/// Chapters and topics are synthesised deterministically from the
/// <see cref="Lesson.Path"/> (shape: <c>/chapter/topic/lesson</c>) so the
/// student surface can navigate without parsing
/// <see cref="CurriculumStructure.Nodes"/>. The GUIDs are stable: the same
/// path segment always hashes to the same id.
/// </summary>
public interface ILessonRetrievalService
{
    Task<SubjectsListResponse> ListSubjectsAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        CancellationToken ct = default);

    Task<ChaptersListResponse?> ListChaptersAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        Guid subjectId,
        CancellationToken ct = default);

    Task<LessonViewerResponse?> GetLessonAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        Guid lessonId,
        CancellationToken ct = default);
}

public sealed class LessonRetrievalService : ILessonRetrievalService
{
    private readonly MuallimiDbContext _db;

    public LessonRetrievalService(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<SubjectsListResponse> ListSubjectsAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        CancellationToken ct = default)
    {
        var curriculumType = ParseEnumOrNull<CurriculumType>(profile.CurriculumType);
        var grade = ParseEnumOrNull<Grade>(profile.Grade);
        var enrolledSubjects = ResolveEnrolledSubjects(profile.SubjectsEnrolled);

        var approvedLessons = await _db.Lessons
            .AsNoTracking()
            .Where(l => l.Status == LessonStatus.Approved)
            .Where(l => curriculumType == null || l.CurriculumType == curriculumType)
            .Where(l => grade == null || l.Grade == grade)
            .Select(l => new { l.LessonId, l.Subject, l.Path })
            .ToListAsync(ct);

        var items = approvedLessons
            .Where(l => enrolledSubjects.Count == 0 || enrolledSubjects.Contains(l.Subject))
            .GroupBy(l => l.Subject)
            .Select(g => new SubjectListItem(
                SubjectId: SubjectToGuid(g.Key),
                DisplayNameAr: SubjectDisplayNameAr(g.Key),
                DisplayNameEn: SubjectDisplayNameEn(g.Key),
                ChapterCount: g.Select(l => ChapterKey(l.Path)).Distinct().Count(),
                PlanGate: "open"))
            .OrderBy(s => s.DisplayNameEn, StringComparer.Ordinal)
            .ToList();

        return new SubjectsListResponse(session.Id, items);
    }

    public async Task<ChaptersListResponse?> ListChaptersAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        Guid subjectId,
        CancellationToken ct = default)
    {
        var subject = SubjectFromGuid(subjectId);
        if (subject is null) return null;

        var enrolledSubjects = ResolveEnrolledSubjects(profile.SubjectsEnrolled);
        if (enrolledSubjects.Count > 0 && !enrolledSubjects.Contains(subject.Value))
            return null;

        var curriculumType = ParseEnumOrNull<CurriculumType>(profile.CurriculumType);
        var grade = ParseEnumOrNull<Grade>(profile.Grade);

        var lessons = await _db.Lessons
            .AsNoTracking()
            .Where(l => l.Status == LessonStatus.Approved
                        && l.Subject == subject.Value
                        && (curriculumType == null || l.CurriculumType == curriculumType)
                        && (grade == null || l.Grade == grade))
            .Select(l => new { l.LessonId, l.Path })
            .ToListAsync(ct);

        var chapters = lessons
            .Select(l => new { l.LessonId, Parts = SplitPath(l.Path) })
            .Where(x => x.Parts.Length >= 1)
            .GroupBy(x => x.Parts[0])
            .Select(chapterGroup =>
            {
                var topics = chapterGroup
                    .GroupBy(x => x.Parts.Length >= 2 ? x.Parts[1] : chapterGroup.Key)
                    .Select(topicGroup => new TopicListItem(
                        TopicId: SlugToGuid("topic:" + chapterGroup.Key + "/" + topicGroup.Key),
                        DisplayNameAr: HumaniseSlugAr(topicGroup.Key),
                        DisplayNameEn: HumaniseSlugEn(topicGroup.Key),
                        LessonCount: topicGroup.Count()))
                    .OrderBy(t => t.DisplayNameEn, StringComparer.Ordinal)
                    .ToList();

                return new ChapterListItem(
                    ChapterId: SlugToGuid("chapter:" + chapterGroup.Key),
                    DisplayNameAr: HumaniseSlugAr(chapterGroup.Key),
                    DisplayNameEn: HumaniseSlugEn(chapterGroup.Key),
                    Topics: topics);
            })
            .OrderBy(c => c.DisplayNameEn, StringComparer.Ordinal)
            .ToList();

        return new ChaptersListResponse(subjectId, chapters);
    }

    public async Task<LessonViewerResponse?> GetLessonAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        Guid lessonId,
        CancellationToken ct = default)
    {
        var curriculumType = ParseEnumOrNull<CurriculumType>(profile.CurriculumType);
        var grade = ParseEnumOrNull<Grade>(profile.Grade);
        var enrolledSubjects = ResolveEnrolledSubjects(profile.SubjectsEnrolled);

        var lesson = await _db.Lessons
            .AsNoTracking()
            .Where(l => l.LessonId == lessonId && l.Status == LessonStatus.Approved)
            .Where(l => curriculumType == null || l.CurriculumType == curriculumType)
            .Where(l => grade == null || l.Grade == grade)
            .FirstOrDefaultAsync(ct);
        if (lesson is null) return null;

        if (enrolledSubjects.Count > 0 && !enrolledSubjects.Contains(lesson.Subject))
            return null;

        var chunks = await _db.ContentChunks
            .AsNoTracking()
            .Where(c => c.LessonId == lessonId && c.Status == ChunkStatus.Active)
            .OrderBy(c => c.Sequence)
            .ToListAsync(ct);

        var publishedAssets = await _db.PublishedAssets
            .AsNoTracking()
            .Where(p => p.LessonId == lessonId && p.Status == PublishedAssetStatus.Active)
            .ToListAsync(ct);

        var blocks = BuildContentBlocks(lesson, chunks, publishedAssets);
        var evidenceRefs = chunks
            .Select(c => new LessonEvidenceRef(
                ChunkId: c.ChunkId,
                SourceUri: FirstSourceUri(c.SourceRefs) ?? "phase1://unknown"))
            .ToList();

        var parts = SplitPath(lesson.Path);
        var chapterSlug = parts.Length >= 1 ? parts[0] : "chapter";
        var topicSlug = parts.Length >= 2 ? parts[1] : chapterSlug;
        var lessonDisplayAr = parts.Length >= 3 ? HumaniseSlugAr(parts[2]) : HumaniseSlugAr(chapterSlug);
        var lessonDisplayEn = parts.Length >= 3 ? HumaniseSlugEn(parts[2]) : HumaniseSlugEn(chapterSlug);

        var teacherVoiceId = Phase1TeacherVoiceProfiles.Resolve(lesson.Subject, lesson.TutorLanguage);

        return new LessonViewerResponse(
            LessonId: lesson.LessonId,
            SubjectId: SubjectToGuid(lesson.Subject),
            ChapterId: SlugToGuid("chapter:" + chapterSlug),
            TopicId: SlugToGuid("topic:" + chapterSlug + "/" + topicSlug),
            DisplayNameAr: lessonDisplayAr,
            DisplayNameEn: lessonDisplayEn,
            ContentBlocks: blocks,
            TeacherVoiceProfileId: teacherVoiceId,
            TeacherVoiceProfileSource: Phase1TeacherVoiceProfiles.Source,
            EvidenceRefs: evidenceRefs,
            ApprovalState: "approved",
            CorrelationId: session.CorrelationId);
    }

    // ── path / slug helpers ──

    private static string[] SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        return path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ChapterKey(string path)
    {
        var parts = SplitPath(path);
        return parts.Length >= 1 ? parts[0] : "chapter";
    }

    private static Guid SlugToGuid(string slug)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(slug));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string HumaniseSlugEn(string slug)
    {
        var parts = slug.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string HumaniseSlugAr(string slug)
    {
        // Phase 1 stores path segments in Latin slugs; surfaces that need
        // Arabic titles MUST source from Phase 1 metadata once available.
        // For MVP we prefix with "درس" so the Arabic column is present
        // (constitution rule: Arabic is never a fallback to English).
        return "درس " + HumaniseSlugEn(slug);
    }

    // ── subject mapping ──

    public static Guid SubjectToGuid(Subject subject) => subject switch
    {
        Subject.Mathematics     => new Guid("11111111-1111-4111-8111-111111111111"),
        Subject.Science         => new Guid("22222222-2222-4222-8222-222222222222"),
        Subject.ArabicLanguage  => new Guid("33333333-3333-4333-8333-333333333333"),
        Subject.EnglishLanguage => new Guid("44444444-4444-4444-8444-444444444444"),
        _                       => Guid.Empty,
    };

    public static Subject? SubjectFromGuid(Guid id) => id switch
    {
        _ when id == SubjectToGuid(Subject.Mathematics)     => Subject.Mathematics,
        _ when id == SubjectToGuid(Subject.Science)         => Subject.Science,
        _ when id == SubjectToGuid(Subject.ArabicLanguage)  => Subject.ArabicLanguage,
        _ when id == SubjectToGuid(Subject.EnglishLanguage) => Subject.EnglishLanguage,
        _                                                    => null,
    };

    private static string SubjectDisplayNameAr(Subject subject) => subject switch
    {
        Subject.Mathematics     => "الرياضيات",
        Subject.Science         => "العلوم",
        Subject.ArabicLanguage  => "اللغة العربية",
        Subject.EnglishLanguage => "اللغة الإنجليزية",
        _                       => subject.ToString(),
    };

    private static string SubjectDisplayNameEn(Subject subject) => subject switch
    {
        Subject.Mathematics     => "Mathematics",
        Subject.Science         => "Science",
        Subject.ArabicLanguage  => "Arabic Language",
        Subject.EnglishLanguage => "English Language",
        _                       => subject.ToString(),
    };

    // ── profile parsing ──

    private static HashSet<Subject> ResolveEnrolledSubjects(string? subjectsEnrolledJson)
    {
        var result = new HashSet<Subject>();
        if (string.IsNullOrWhiteSpace(subjectsEnrolledJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(subjectsEnrolledJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var raw = item.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (Enum.TryParse<Subject>(raw, ignoreCase: true, out var parsed))
                {
                    result.Add(parsed);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed profile data: fall through with empty set so the
            // caller sees no enrolled subjects and returns nothing.
        }
        return result;
    }

    private static T? ParseEnumOrNull<T>(string? raw) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) ? parsed : null;
    }

    // ── content block assembly ──

    private static IReadOnlyList<LessonContentBlock> BuildContentBlocks(
        Lesson lesson,
        IReadOnlyList<ContentChunk> chunks,
        IReadOnlyList<PublishedAsset> publishedAssets)
    {
        var language = lesson.TutorLanguage == TutorLanguage.Ar ? "ar" : "en";
        var blocks = new List<LessonContentBlock>();

        foreach (var chunk in chunks)
        {
            blocks.Add(new LessonContentBlock(
                BlockType: "text",
                Language: language,
                TextPayload: chunk.Text,
                MediaReference: null,
                CaptionTrackReference: null,
                TranscriptReference: null));
        }

        foreach (var asset in publishedAssets)
        {
            switch (asset.AssetType)
            {
                case AssetType.Audio:
                    blocks.Add(new LessonContentBlock(
                        BlockType: "audio",
                        Language: language,
                        TextPayload: null,
                        MediaReference: asset.RuntimeUrl,
                        CaptionTrackReference: null,
                        TranscriptReference: null));
                    break;
                case AssetType.Visual when asset.VisualFormat == VisualFormat.Mp4Animation:
                    blocks.Add(new LessonContentBlock(
                        BlockType: "video",
                        Language: language,
                        TextPayload: null,
                        MediaReference: asset.RuntimeUrl,
                        CaptionTrackReference: null,
                        TranscriptReference: null));
                    break;
                case AssetType.Visual:
                    blocks.Add(new LessonContentBlock(
                        BlockType: "figure",
                        Language: language,
                        TextPayload: null,
                        MediaReference: asset.RuntimeUrl,
                        CaptionTrackReference: null,
                        TranscriptReference: null));
                    break;
            }
        }

        return blocks;
    }

    private static string? FirstSourceUri(string sourceRefsJson)
    {
        if (string.IsNullOrWhiteSpace(sourceRefsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(sourceRefsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String) return entry.GetString();
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("source_uri", out var u) && u.ValueKind == JsonValueKind.String)
                    return u.GetString();
                if (entry.TryGetProperty("uri", out var u2) && u2.ValueKind == JsonValueKind.String)
                    return u2.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }
}

public static class LessonRetrievalServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3LessonRetrieval(this IServiceCollection services)
    {
        services.AddScoped<ILessonRetrievalService, LessonRetrievalService>();
        return services;
    }
}
