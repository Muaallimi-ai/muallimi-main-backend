using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.MockTest;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Muallimi.Api.StudentExperience.SessionEvents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.SessionEvents;

/// <summary>
/// T102 (US6) — Event label disjointness: <c>mock_test</c> events must
/// never be labelled <c>quiz_answered</c>, and the Solve Questions
/// surface must never fan out a <c>mock_test</c> event.
///
/// Why this matters: Phase 4 engagement + parent surfaces route on the
/// event_kind label; if a mock test emitted <c>quiz_answered</c>, the
/// parent dashboard would count it as practice instead of a
/// high-stakes self-check. These tests pin down that separation in three
/// structural checks so a future refactor can't collapse the labels.
/// </summary>
public class MockTestLabelTests
{
    [Fact]
    public void Mock_Test_And_Quiz_Answered_Are_Distinct_Enum_Members()
    {
        var values = Enum.GetValues<SessionEventKind>().ToList();
        Assert.Contains(SessionEventKind.mock_test, values);
        Assert.Contains(SessionEventKind.quiz_answered, values);
        Assert.NotEqual(SessionEventKind.mock_test, SessionEventKind.quiz_answered);
    }

    [Fact]
    public void Mock_Test_Endpoints_File_Only_Enqueues_Mock_Test_Events()
    {
        var source = ReadEmbeddedSource(
            "src/Muallimi.Api/StudentExperience/MockTest/Endpoints.cs");
        // The MockTest endpoints must not reference the quiz_answered
        // enum member. A drift here would fan a mock-test submission out
        // as a Solve Questions attempt.
        Assert.DoesNotContain("SessionEventKind.quiz_answered", source);
        Assert.Contains("SessionEventKind.mock_test", source);
    }

    [Fact]
    public void Quiz_Delivery_Endpoints_File_Only_Enqueues_Quiz_Answered_Events()
    {
        var source = ReadEmbeddedSource(
            "src/Muallimi.Api/StudentExperience/QuizDelivery/Endpoints.cs");
        // The Solve Questions endpoints must not reference the mock_test
        // enum member. A drift here would mislabel practice attempts as
        // mock-test submissions.
        Assert.DoesNotContain("SessionEventKind.mock_test", source);
        Assert.Contains("SessionEventKind.quiz_answered", source);
    }

    [Fact]
    public void Mock_Test_Service_Does_Not_Reference_Quiz_Answered_Label()
    {
        var source = ReadEmbeddedSource(
            "src/Muallimi.Api/StudentExperience/MockTest/MockTestService.cs");
        Assert.DoesNotContain("SessionEventKind.quiz_answered", source);
        Assert.DoesNotContain("\"quiz_answered\"", source);
    }

    [Fact]
    public void Mock_Test_Endpoints_Emit_Phase_Started_Submitted_Timed_Out()
    {
        // Mock test fan-out is phase-tagged so Phase 4 can distinguish
        // start / submit / timeout / abandonment in a single event_kind.
        // Assert the three terminal phase markers exist in the endpoints
        // source — the contract requires each of the three transitions to
        // emit a distinct mock_test event payload.
        var source = ReadEmbeddedSource(
            "src/Muallimi.Api/StudentExperience/MockTest/Endpoints.cs");
        Assert.Contains("\"started\"", source);
        Assert.Contains("\"submitted\"", source);
        Assert.Contains("\"timed_out\"", source);
        // And the phase tag flows through all three handlers.
        Assert.Contains("phase =", source);
    }

    private static string ReadEmbeddedSource(string relativePath)
    {
        // Walk up from the test binary until the muallimi-main-backend
        // repo root is found, then read the requested source file. Tests
        // run from bin/Debug so three parents are not always enough —
        // probe for the sentinel file instead.
        var directory = AppContext.BaseDirectory;
        var rootGuard = Path.GetPathRoot(directory) ?? "/";
        while (directory is not null
               && !File.Exists(Path.Combine(directory, "Muallimi.MainBackend.sln"))
               && directory != rootGuard)
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        var path = Path.Combine(directory!, relativePath);
        Assert.True(File.Exists(path), $"Source file not found for structural assertion: {path}");
        return File.ReadAllText(path);
    }
}
