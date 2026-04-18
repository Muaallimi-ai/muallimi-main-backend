using System;
using System.Globalization;

namespace Muallimi.Api.StudentProgressSurface;

/// <summary>
/// T051 (US1) — Subject / topic label resolver for the student progress
/// surface.
///
/// Phase 4 needs an Arabic + English label pair for every subject and topic
/// referenced in the mastery breakdown. Those labels live on the Phase 1
/// curriculum node tree (stored as JSON on <c>CurriculumStructure</c>).
///
/// The MVP resolver returns the id as a bilingual pair so the surface
/// renders without a Phase 1 node lookup on every request. Phase 1 label
/// lookup is swapped in via DI by replacing this binding without changing
/// <see cref="IStudentProgressService"/>.
/// </summary>
public interface ICurriculumLabelResolver
{
    (string Ar, string En) ResolveSubject(Guid subjectId);

    (string Ar, string En) ResolveTopic(Guid subjectId, Guid topicId);
}

public sealed class DefaultCurriculumLabelResolver : ICurriculumLabelResolver
{
    public (string Ar, string En) ResolveSubject(Guid subjectId)
    {
        var token = ShortToken(subjectId);
        return ($"مادة {token}", $"Subject {token}");
    }

    public (string Ar, string En) ResolveTopic(Guid subjectId, Guid topicId)
    {
        var token = ShortToken(topicId);
        return ($"موضوع {token}", $"Topic {token}");
    }

    private static string ShortToken(Guid id)
    {
        var hex = id.ToString("N", CultureInfo.InvariantCulture);
        return hex.Substring(0, 6);
    }
}
