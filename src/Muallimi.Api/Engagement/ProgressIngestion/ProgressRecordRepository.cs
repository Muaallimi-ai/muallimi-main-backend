using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.ProgressIngestion;

/// <summary>
/// T033 (US4) — <see cref="ProgressRecord"/> repository.
///
/// Thin read/write surface over <c>progress_records</c>. All calls use
/// <c>IgnoreQueryFilters()</c> because the ingestion worker runs in the
/// null-tenant admin scope and MUST filter by <c>tenant_id</c> explicitly
/// from the Phase 3 envelope.
/// </summary>
public interface IProgressRecordRepository
{
    Task<bool> ExistsAsync(Guid tenantId, string sourceEventId, CancellationToken ct = default);
    Task AddAsync(ProgressRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<ProgressRecord>> ForStudentScopeAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid? topicId,
        CancellationToken ct = default);
    Task<IReadOnlyList<ProgressRecord>> ForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default);
}

public sealed class ProgressRecordRepository : IProgressRecordRepository
{
    private readonly MuallimiDbContext _db;

    public ProgressRecordRepository(MuallimiDbContext db) => _db = db;

    public Task<bool> ExistsAsync(Guid tenantId, string sourceEventId, CancellationToken ct = default)
        => _db.ProgressRecords
            .IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == tenantId && r.SourceEventId == sourceEventId, ct);

    public Task AddAsync(ProgressRecord record, CancellationToken ct = default)
    {
        _db.ProgressRecords.Add(record);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ProgressRecord>> ForStudentScopeAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid? topicId,
        CancellationToken ct = default)
    {
        var query = _db.ProgressRecords
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.StudentId == studentId);
        var list = await query.ToListAsync(ct);
        return list
            .Where(r => ScopeMatches(r.CurriculumScope, subjectId, topicId))
            .OrderBy(r => r.OccurredAt)
            .ToList();
    }

    public async Task<IReadOnlyList<ProgressRecord>> ForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default)
    {
        return await _db.ProgressRecords
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.StudentId == studentId)
            .OrderBy(r => r.OccurredAt)
            .ToListAsync(ct);
    }

    internal static bool ScopeMatches(string curriculumScopeJson, Guid subjectId, Guid? topicId)
    {
        if (string.IsNullOrWhiteSpace(curriculumScopeJson) || curriculumScopeJson == "{}") return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(curriculumScopeJson);
            var root = doc.RootElement;
            if (!TryReadGuid(root, "subject_id", out var subject) || subject != subjectId) return false;
            if (topicId is null) return true;
            if (!TryReadGuid(root, "topic_id", out var topic)) return false;
            return topic == topicId.Value;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadGuid(System.Text.Json.JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        if (!root.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind != System.Text.Json.JsonValueKind.String) return false;
        return Guid.TryParse(prop.GetString(), out value);
    }
}

public static class ProgressRecordRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ProgressRecordRepository(this IServiceCollection services)
    {
        services.AddScoped<IProgressRecordRepository, ProgressRecordRepository>();
        return services;
    }
}
