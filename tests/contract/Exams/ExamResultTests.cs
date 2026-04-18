using System;
using Muallimi.Api.Exams.ExamResults;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Exams;

/// <summary>
/// T118 (US6) — Contract test for exam result publication.
///
/// Pins the teacher-facing result routes and verifies that the publish
/// endpoint's route template matches the exam lifecycle contract. The
/// transition from graded to published is exercised via the
/// state-machine contract test (T116); here we just pin the surface so a
/// rename does not silently break the frontend.
/// </summary>
public class ExamResultTests
{
    [Fact]
    public void Routes_Are_Pinned()
    {
        Assert.Equal("/api/teacher/exams/{examId:guid}/results", ExamResultEndpoints.ResultsRoute);
        Assert.Equal("/api/teacher/exams/{examId:guid}/publish", ExamResultEndpoints.PublishRoute);
    }
}
