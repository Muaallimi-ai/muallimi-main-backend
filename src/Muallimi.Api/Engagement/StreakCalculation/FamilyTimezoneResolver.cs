using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.StreakCalculation;

/// <summary>
/// T021 — FamilyTimezoneResolver.
///
/// Streak calculation is authoritative in the family's local timezone, not
/// UTC (see data-model.md, FR-005). This resolver reads the timezone from
/// the student's linked parent profile and exposes a "calendar day in
/// family timezone" helper that <c>StreakCalculator</c> uses to decide
/// whether a qualifying event lands on a new day, the same day, or crosses
/// a rollover.
///
/// Falls back to the configured default (<c>Asia/Dubai</c>) when no linked
/// parent profile exists yet — the fallback never shortens a streak, and
/// the actual timezone is picked up on the next ingestion once the parent
/// profile is created.
/// </summary>
public interface IFamilyTimezoneResolver
{
    Task<string> GetTimezoneAsync(Guid studentId, CancellationToken ct = default);
    Task<DateOnly> CalendarDayForAsync(Guid studentId, DateTime instantUtc, CancellationToken ct = default);
}

public sealed class FamilyTimezoneResolver : IFamilyTimezoneResolver
{
    public const string DefaultTimezone = "Asia/Dubai";

    private readonly MuallimiDbContext _db;

    public FamilyTimezoneResolver(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<string> GetTimezoneAsync(Guid studentId, CancellationToken ct = default)
    {
        var tz = await (
            from link in _db.ChildLinks.AsNoTracking()
            join parent in _db.ParentProfiles.AsNoTracking() on link.ParentProfileId equals parent.ParentProfileId
            where link.StudentId == studentId && (link.EffectiveEnd == null || link.EffectiveEnd >= DateTime.UtcNow.Date)
            orderby link.EffectiveStart descending
            select parent.Timezone
        ).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(tz) ? DefaultTimezone : tz!;
    }

    public async Task<DateOnly> CalendarDayForAsync(Guid studentId, DateTime instantUtc, CancellationToken ct = default)
    {
        var tz = await GetTimezoneAsync(studentId, ct);
        return CalendarDay(instantUtc, tz);
    }

    public static DateOnly CalendarDay(DateTime instantUtc, string ianaTimezone)
    {
        var tzInfo = ResolveTimezone(ianaTimezone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(instantUtc.ToUniversalTime(), DateTimeKind.Utc),
            tzInfo);
        return DateOnly.FromDateTime(local);
    }

    private static TimeZoneInfo ResolveTimezone(string iana)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(iana);
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimezone);
        }
    }
}

public static class FamilyTimezoneResolverServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4FamilyTimezoneResolver(this IServiceCollection services)
    {
        services.AddScoped<IFamilyTimezoneResolver, FamilyTimezoneResolver>();
        return services;
    }
}
