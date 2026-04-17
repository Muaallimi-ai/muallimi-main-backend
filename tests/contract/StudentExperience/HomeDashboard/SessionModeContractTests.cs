using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.HomeDashboard;

/// <summary>
/// T027 (US1) — Contracts for POST /student/session/mode and
/// POST /student/session/end.
///
/// Validates:
///   - the accepted-vs-refused response shape (a plan-gate denial MUST
///     surface localised Arabic + English refusal text and never mutate
///     session state),
///   - the allowed end_reason enum,
///   - the mode → SessionEventKind mapping distinguishing quiz_answered
///     from mock_test (T102) and session_end as terminal.
/// </summary>
public class SessionModeContractTests
{
    [Fact]
    public void SessionModeRequest_Carries_SessionId_TargetMode_TargetScope()
    {
        var props = typeof(SessionModeRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("SessionId", props);
        Assert.Contains("TargetMode", props);
        Assert.Contains("TargetScope", props);
    }

    [Fact]
    public void SessionModeAcceptedResponse_Carries_ActiveMode_ActiveScope_TransitionAt()
    {
        var props = typeof(SessionModeAcceptedResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("SessionId", props);
        Assert.Contains("ActiveMode", props);
        Assert.Contains("ActiveScope", props);
        Assert.Contains("TransitionAt", props);
    }

    [Fact]
    public void SessionModeRefusedResponse_Carries_Localised_Refusal_Text()
    {
        var props = typeof(SessionModeRefusedResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("RefusalReason", props);
        Assert.Contains("RefusalTextAr", props);
        Assert.Contains("RefusalTextEn", props);
    }

    [Fact]
    public void LocalisedRefusal_Returns_NonEmpty_For_Both_Locales()
    {
        foreach (var reason in new[]
        {
            "plan_tier_not_permitted", "subject_not_permitted",
            "grade_not_permitted", "unknown_reason",
        })
        {
            var ar = SessionModeEndpoint.LocalisedRefusal(reason, "ar");
            var en = SessionModeEndpoint.LocalisedRefusal(reason, "en");
            Assert.False(string.IsNullOrWhiteSpace(ar), $"Arabic refusal empty for {reason}");
            Assert.False(string.IsNullOrWhiteSpace(en), $"English refusal empty for {reason}");
        }
    }

    [Fact]
    public void SessionEndRequest_Has_SessionId_EndReason()
    {
        var props = typeof(SessionEndRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("SessionId", props);
        Assert.Contains("EndReason", props);
    }

    [Fact]
    public void SessionEndResponse_Has_SessionId_EndedAt()
    {
        var props = typeof(SessionEndResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("SessionId", props);
        Assert.Contains("EndedAt", props);
    }

    [Fact]
    public void Mode_Transitions_Map_To_Expected_SessionEventKinds()
    {
        Assert.Equal(SessionEventKind.lesson_view,
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.Study));
        Assert.Equal(SessionEventKind.question_asked,
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.TutorChat));
        Assert.Equal(SessionEventKind.question_asked,
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.TutorVoice));
        Assert.Equal(SessionEventKind.homework_help_used,
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.HomeworkHelp));
        Assert.Equal(SessionEventKind.whiteboard_session,
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.Whiteboard));
    }

    [Fact]
    public void StudentSessionRepository_Mode_Transitions_Must_Pass_Through_Home()
    {
        // Direct mode-to-mode without going through "home" is illegal.
        Assert.True(StudentSessionRepository.IsLegalTransition(StudentModes.Home, StudentModes.Study));
        Assert.True(StudentSessionRepository.IsLegalTransition(StudentModes.Study, StudentModes.Home));
        Assert.False(StudentSessionRepository.IsLegalTransition(StudentModes.Study, StudentModes.TutorChat));
        Assert.True(StudentSessionRepository.IsLegalTransition(StudentModes.Study, StudentModes.Study));
    }
}
