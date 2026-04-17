using System;
using System.Collections.Generic;
using Muallimi.Domain.Shared;

namespace Muallimi.Api.StudentExperience.LessonRetrieval;

/// <summary>
/// T047 (US2) — Phase 1 teacher-voice profile registry mirrored in the
/// main-backend for Study mode narration. The four identifiers match the
/// <c>LocalVoiceProfileAdapter</c> registry in the ai-service. The
/// <c>ai-tutor-voice-v1</c> identifier used for Phase 2 tutor voice MUST
/// be disjoint from every entry here; contract test T046 and integration
/// test T058 enforce it.
///
/// The chooser is deterministic on subject + tutor language so repeated
/// retrievals for the same lesson always resolve the same voice, which is
/// what the <c>LessonViewerState.TeacherVoiceProfileId</c> column mirrors.
/// </summary>
public static class Phase1TeacherVoiceProfiles
{
    public const string Source = "phase1_curriculum";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        "teacher-voice-msa-female-v1",
        "teacher-voice-msa-male-v1",
        "teacher-voice-gulf-female-v1",
        "teacher-voice-gulf-male-v1",
    };

    public static string Resolve(Subject subject, TutorLanguage tutorLanguage)
    {
        // Deterministic picker — same input, same id. Subjects split along
        // female/male to mirror Phase 1 narrator casting; Gulf variants are
        // reserved for Arabic-language subjects, MSA for English + general.
        var isArabicSubject = subject == Subject.ArabicLanguage;
        var isFemaleCast =
            subject is Subject.Mathematics or Subject.ArabicLanguage;

        if (isArabicSubject || tutorLanguage == TutorLanguage.Ar)
        {
            return isFemaleCast
                ? "teacher-voice-gulf-female-v1"
                : "teacher-voice-gulf-male-v1";
        }

        return isFemaleCast
            ? "teacher-voice-msa-female-v1"
            : "teacher-voice-msa-male-v1";
    }
}
