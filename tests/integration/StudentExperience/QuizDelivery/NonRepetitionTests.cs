using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.QuizDelivery;

/// <summary>
/// T092 (US5) — Non-repetition integration test.
///
/// Solve Questions promises every question within a snapshot is unique and
/// never re-served after it has been answered. The player steps through the
/// snapshot using <c>QuizSession.Progress</c> as the cursor, and the
/// contract forbids the same <c>question_id</c> appearing twice either in
/// the snapshot itself or across the answered log.
///
/// This test exercises the deterministic question bank projection and the
/// cursor logic in <c>QuizDeliveryService.NextUnansweredQuestion</c>
/// directly so it can run without a Postgres dependency.
/// </summary>
public class NonRepetitionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static IReadOnlyList<QuizChunkSource> BuildUniqueChunkSources(int count)
    {
        var sources = new List<QuizChunkSource>(count);
        for (var i = 0; i < count; i++)
        {
            sources.Add(new QuizChunkSource(
                ChunkId: Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                Sequence: i,
                Text: $"الفقرة المعتمدة رقم {i}. This is approved passage number {i}."));
        }
        return sources;
    }

    [Fact]
    public void Deterministic_Question_Bank_Produces_Unique_Question_Ids()
    {
        var bank = new DeterministicQuizQuestionBank();
        var sources = BuildUniqueChunkSources(10);

        var records = bank.ProjectFromChunks(sources, 10);

        Assert.Equal(10, records.Count);
        var ids = records.Select(r => r.QuestionId).ToList();
        Assert.Equal(10, ids.Distinct().Count());
    }

    [Fact]
    public void Deterministic_Question_Bank_Produces_Unique_Option_Ids_Per_Question()
    {
        var bank = new DeterministicQuizQuestionBank();
        var sources = BuildUniqueChunkSources(5);

        var records = bank.ProjectFromChunks(sources, 5);

        foreach (var record in records)
        {
            var optionIds = record.Options.Select(o => o.OptionId).ToList();
            Assert.Equal(optionIds.Count, optionIds.Distinct().Count());
            Assert.Equal(4, optionIds.Count);
            Assert.Single(record.Options, o => o.IsCorrect);
            Assert.Contains(record.Options, o => o.OptionId == record.CorrectOptionId);
        }
    }

    [Fact]
    public void Deterministic_Question_Bank_Is_Stable_Across_Runs()
    {
        var bank = new DeterministicQuizQuestionBank();
        var sources = BuildUniqueChunkSources(3);

        var first = bank.ProjectFromChunks(sources, 3);
        var second = bank.ProjectFromChunks(sources, 3);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].QuestionId, second[i].QuestionId);
            Assert.Equal(first[i].CorrectOptionId, second[i].CorrectOptionId);
        }
    }

    [Fact]
    public void NextUnansweredQuestion_Skips_Already_Answered_Questions()
    {
        var bank = new DeterministicQuizQuestionBank();
        var snapshot = bank.ProjectFromChunks(BuildUniqueChunkSources(4), 4);

        var quizSession = new QuizSession
        {
            Progress = JsonSerializer.Serialize(new[]
            {
                new QuizProgressEntry(
                    QuestionId: snapshot[0].QuestionId,
                    ChosenOptionId: snapshot[0].CorrectOptionId,
                    IsCorrect: true,
                    AnsweredAt: DateTime.UtcNow),
                new QuizProgressEntry(
                    QuestionId: snapshot[1].QuestionId,
                    ChosenOptionId: snapshot[1].Options.First(o => !o.IsCorrect).OptionId,
                    IsCorrect: false,
                    AnsweredAt: DateTime.UtcNow),
            }, JsonOptions),
        };

        var next = QuizDeliveryService.NextUnansweredQuestion(snapshot, quizSession);
        Assert.NotNull(next);
        Assert.Equal(snapshot[2].QuestionId, next!.QuestionId);
    }

    [Fact]
    public void NextUnansweredQuestion_Returns_Null_When_All_Answered()
    {
        var bank = new DeterministicQuizQuestionBank();
        var snapshot = bank.ProjectFromChunks(BuildUniqueChunkSources(2), 2);

        var quizSession = new QuizSession
        {
            Progress = JsonSerializer.Serialize(snapshot.Select(s => new QuizProgressEntry(
                QuestionId: s.QuestionId,
                ChosenOptionId: s.CorrectOptionId,
                IsCorrect: true,
                AnsweredAt: DateTime.UtcNow)).ToList(), JsonOptions),
        };

        var next = QuizDeliveryService.NextUnansweredQuestion(snapshot, quizSession);
        Assert.Null(next);
    }

    [Fact]
    public void Snapshot_Order_Is_Chunk_Sequence_Not_Chunk_Id()
    {
        var bank = new DeterministicQuizQuestionBank();
        // Reverse-inserting sequences to verify ordering by Sequence, not insertion.
        var sources = new[]
        {
            new QuizChunkSource(Guid.Parse("00000000-0000-0000-0000-000000000003"), 3, "ج"),
            new QuizChunkSource(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, "أ"),
            new QuizChunkSource(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2, "ب"),
        };

        var records = bank.ProjectFromChunks(sources, 3);

        Assert.Equal("أ", ExtractLeadSentence(records[0].ExplanationTextAr));
        Assert.Equal("ب", ExtractLeadSentence(records[1].ExplanationTextAr));
        Assert.Equal("ج", ExtractLeadSentence(records[2].ExplanationTextAr));
    }

    [Fact]
    public void Every_Question_Id_Appears_At_Most_Once_Across_Snapshot_And_Progress()
    {
        var bank = new DeterministicQuizQuestionBank();
        var snapshot = bank.ProjectFromChunks(BuildUniqueChunkSources(6), 6);

        // Walk through answering every question once.
        var progress = new List<QuizProgressEntry>();
        foreach (var record in snapshot)
        {
            progress.Add(new QuizProgressEntry(
                QuestionId: record.QuestionId,
                ChosenOptionId: record.CorrectOptionId,
                IsCorrect: true,
                AnsweredAt: DateTime.UtcNow));
        }

        var answeredIds = progress.Select(p => p.QuestionId).ToList();
        var snapshotIds = snapshot.Select(s => s.QuestionId).ToList();

        // Snapshot IDs are unique (promise of non-repetition).
        Assert.Equal(snapshotIds.Count, snapshotIds.Distinct().Count());
        // Answered IDs are unique (no double-answer within a session).
        Assert.Equal(answeredIds.Count, answeredIds.Distinct().Count());
        // And answered ids are a subset of snapshot ids.
        foreach (var id in answeredIds) Assert.Contains(id, snapshotIds);
    }

    private static string ExtractLeadSentence(string explanation)
    {
        var marker = ": ";
        var idx = explanation.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return explanation;
        var tail = explanation.Substring(idx + marker.Length).TrimEnd('.', ' ');
        return tail;
    }
}
