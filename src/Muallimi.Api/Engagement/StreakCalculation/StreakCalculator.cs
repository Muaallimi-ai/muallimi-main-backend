using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.StreakCalculation;

/// <summary>
/// T038 (US4) — Streak calculator.
///
/// Computes the student's daily-return streak in the family's IANA
/// timezone. A qualifying day is any calendar day in the family timezone
/// during which at least one <see cref="ProgressRecord"/> exists with a
/// qualifying event kind. The current length is the longest trailing run of
/// consecutive calendar days ending at the most recent qualifying day.
///
/// This is a pure function of the stored PR set — incremental updates and
/// out-of-order replays converge to the same answer (covered by T030 + T032).
///
/// Reset semantics: a missed day resets <c>current_length</c> to the length
/// of the next run forward (zero if there is no next qualifying day yet).
/// No punitive language — the reset is a neutral state change.
/// </summary>
public interface IStreakCalculator
{
    Task<StreakRecomputeResult> RecomputeAsync(
        Guid tenantId,
        Guid studentId,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record StreakRecomputeResult(
    StreakState State,
    int PriorLength,
    int NewLength,
    bool Reset,
    bool Changed,
    string FamilyTimezone);

public sealed class StreakCalculator : IStreakCalculator
{
    private static readonly HashSet<string> QualifyingKinds = new()
    {
        Phase3EventKinds.SessionStart,
        Phase3EventKinds.LessonView,
        Phase3EventKinds.ContentPlay,
        Phase3EventKinds.QuizAnswered,
        Phase3EventKinds.MockTest,
        Phase3EventKinds.HomeworkHelpUsed,
        Phase3EventKinds.WhiteboardSession,
        Phase3EventKinds.SessionEnd,
    };

    private readonly IProgressRecordRepository _records;
    private readonly IStreakStateRepository _states;
    private readonly IFamilyTimezoneResolver _timezone;

    public StreakCalculator(
        IProgressRecordRepository records,
        IStreakStateRepository states,
        IFamilyTimezoneResolver timezone)
    {
        _records = records;
        _states = states;
        _timezone = timezone;
    }

    public async Task<StreakRecomputeResult> RecomputeAsync(
        Guid tenantId,
        Guid studentId,
        string correlationId,
        CancellationToken ct = default)
    {
        var tz = await _timezone.GetTimezoneAsync(studentId, ct);
        var prs = await _records.ForStudentAsync(tenantId, studentId, ct);

        var days = new SortedSet<DateOnly>();
        foreach (var r in prs)
        {
            if (!QualifyingKinds.Contains(r.EventKind)) continue;
            var day = FamilyTimezoneResolver.CalendarDay(r.OccurredAt, tz);
            days.Add(day);
        }

        var currentLength = TrailingRunLength(days);
        var longestLength = LongestRunLength(days);
        var lastDay = days.Count == 0 ? DateOnly.MinValue : days.Max;

        var existing = await _states.GetAsync(tenantId, studentId, ct);
        var priorLength = existing?.CurrentLength ?? 0;
        var reset = existing != null && currentLength == 0 && priorLength > 0;

        if (existing is null)
        {
            var row = new StreakState
            {
                StreakStateId = Guid.NewGuid(),
                TenantId = tenantId,
                StudentId = studentId,
                CurrentLength = currentLength,
                LongestLength = longestLength,
                LastQualifyingDay = days.Count == 0 ? DateTime.MinValue : lastDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                FamilyTimezone = tz,
                ResetHistory = "[]",
                LastUpdatedAt = DateTime.UtcNow,
            };
            await _states.AddAsync(row, ct);
            return new StreakRecomputeResult(row, priorLength, currentLength, reset, currentLength != priorLength, tz);
        }

        var changed = existing.CurrentLength != currentLength
                      || existing.LongestLength != longestLength
                      || existing.FamilyTimezone != tz;

        var resetHistory = existing.ResetHistory;
        if (existing.CurrentLength > currentLength && currentLength < existing.CurrentLength)
        {
            resetHistory = AppendResetEntry(existing.ResetHistory, existing.CurrentLength);
        }

        existing.CurrentLength = currentLength;
        existing.LongestLength = Math.Max(existing.LongestLength, longestLength);
        existing.LastQualifyingDay = days.Count == 0 ? DateTime.MinValue : lastDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        existing.FamilyTimezone = tz;
        existing.ResetHistory = resetHistory;
        existing.LastUpdatedAt = DateTime.UtcNow;

        return new StreakRecomputeResult(existing, priorLength, currentLength, reset, changed, tz);
    }

    internal static int TrailingRunLength(SortedSet<DateOnly> days)
    {
        if (days.Count == 0) return 0;
        var ordered = days.ToList();
        var length = 1;
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            if (ordered[i].DayNumber - ordered[i - 1].DayNumber == 1)
            {
                length++;
            }
            else
            {
                break;
            }
        }
        return length;
    }

    internal static int LongestRunLength(SortedSet<DateOnly> days)
    {
        if (days.Count == 0) return 0;
        var ordered = days.ToList();
        var best = 1;
        var current = 1;
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].DayNumber - ordered[i - 1].DayNumber == 1)
            {
                current++;
                if (current > best) best = current;
            }
            else
            {
                current = 1;
            }
        }
        return best;
    }

    private static string AppendResetEntry(string existingJson, int priorLength)
    {
        var entries = new List<object>();
        if (!string.IsNullOrWhiteSpace(existingJson) && existingJson != "[]")
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        entries.Add(JsonSerializer.Deserialize<Dictionary<string, object>>(el.GetRawText()) ?? new());
                    }
                }
            }
            catch
            {
                // treat malformed existing history as empty
            }
        }
        entries.Add(new { reset_at = DateTime.UtcNow.ToString("O"), prior_length = priorLength });
        return JsonSerializer.Serialize(entries);
    }
}

public static class StreakCalculatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4StreakCalculator(this IServiceCollection services)
    {
        services.AddScoped<IStreakCalculator, StreakCalculator>();
        return services;
    }
}
