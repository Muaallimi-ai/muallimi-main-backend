using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Announcements.AnnouncementDispatch;

/// <summary>
/// T158 (US8) — expand a target scope (class, grade, school) at publish
/// time into a list of student + parent recipients.
///
/// Invariants from the contract:
///   • class scope → all active enrolments of that class.
///   • grade scope → all active enrolments of every active class in that
///     grade under the school tenant.
///   • school scope → every active enrolment across the school tenant.
///   • transferred students (enrolment.Status != "active") are excluded.
///   • parents are included via ChildLink rows whose effective window
///     covers "today" at publish time.
///
/// target_scope values and the encoding of the raw target string:
///   • "class"  → target_raw is a Guid (class_group_id).
///   • "grade"  → target_raw is an int rendered as a string (e.g. "7").
///   • "school" → target_raw is ignored.
/// </summary>
public interface IAnnouncementTargetResolver
{
    Task<AnnouncementTargetResolution> ResolveAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string targetScope,
        string? targetRaw,
        DateTime asOfUtc,
        CancellationToken ct = default);
}

public sealed record AnnouncementRecipient(Guid RecipientId, string RecipientRole);

public sealed record AnnouncementTargetResolution(IReadOnlyList<AnnouncementRecipient> Recipients)
{
    public int StudentCount => Recipients.Count(r => r.RecipientRole == "student");
    public int ParentCount => Recipients.Count(r => r.RecipientRole == "parent");
}

public sealed class AnnouncementTargetResolver : IAnnouncementTargetResolver
{
    private readonly MuallimiDbContext _db;

    public AnnouncementTargetResolver(MuallimiDbContext db) => _db = db;

    public async Task<AnnouncementTargetResolution> ResolveAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string targetScope,
        string? targetRaw,
        DateTime asOfUtc,
        CancellationToken ct = default)
    {
        var classIds = await ResolveClassIdsAsync(tenantId, schoolTenantId, targetScope, targetRaw, ct);
        if (classIds.Count == 0)
        {
            return new AnnouncementTargetResolution(Array.Empty<AnnouncementRecipient>());
        }

        var studentIds = await _db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && classIds.Contains(e.ClassGroupId)
                && e.Status == "active")
            .Select(e => e.StudentId)
            .Distinct()
            .ToListAsync(ct);

        var asOfDate = asOfUtc.Date;
        var parentLinks = await _db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId
                && studentIds.Contains(l.StudentId)
                && l.EffectiveStart <= asOfDate
                && (l.EffectiveEnd == null || l.EffectiveEnd >= asOfDate))
            .Select(l => l.ParentProfileId)
            .Distinct()
            .ToListAsync(ct);

        var recipients = new List<AnnouncementRecipient>(studentIds.Count + parentLinks.Count);
        foreach (var sid in studentIds) recipients.Add(new AnnouncementRecipient(sid, "student"));
        foreach (var pid in parentLinks) recipients.Add(new AnnouncementRecipient(pid, "parent"));
        return new AnnouncementTargetResolution(recipients);
    }

    private async Task<List<Guid>> ResolveClassIdsAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string targetScope,
        string? targetRaw,
        CancellationToken ct)
    {
        switch (targetScope)
        {
            case "class":
                if (!Guid.TryParse(targetRaw, out var classId)) return new List<Guid>();
                var exists = await _db.ClassGroups
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .AnyAsync(c => c.TenantId == tenantId
                        && c.SchoolTenantId == schoolTenantId
                        && c.ClassGroupId == classId
                        && c.IsActive, ct);
                return exists ? new List<Guid> { classId } : new List<Guid>();

            case "grade":
                if (!int.TryParse(targetRaw, out var grade)) return new List<Guid>();
                return await _db.ClassGroups
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(c => c.TenantId == tenantId
                        && c.SchoolTenantId == schoolTenantId
                        && c.IsActive
                        && c.Grade == grade)
                    .Select(c => c.ClassGroupId)
                    .ToListAsync(ct);

            case "school":
                return await _db.ClassGroups
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(c => c.TenantId == tenantId
                        && c.SchoolTenantId == schoolTenantId
                        && c.IsActive)
                    .Select(c => c.ClassGroupId)
                    .ToListAsync(ct);

            default:
                return new List<Guid>();
        }
    }
}

public static class AnnouncementTargetResolverServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5AnnouncementTargetResolver(this IServiceCollection services)
    {
        services.AddScoped<IAnnouncementTargetResolver, AnnouncementTargetResolver>();
        return services;
    }
}
