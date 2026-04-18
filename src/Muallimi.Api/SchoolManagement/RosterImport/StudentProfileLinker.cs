using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Parents;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.RosterImport;

/// <summary>
/// T056 (US2) — <c>StudentProfileLinker</c>.
///
/// Per validated roster row:
///   1. Derive a deterministic <c>parent_identity_id</c> from
///      (school_tenant_id, parent_email). This lets re-imports and
///      existing family-account parents collapse to the same
///      <see cref="ParentProfile"/> row without needing a separate email
///      column (the parent email column was intentionally omitted from the
///      Phase 4 schema — identity + consent_state carry email in
///      production).
///   2. If a <see cref="ParentProfile"/> row already exists for that
///      identity id inside the school's tenant, reuse it. Otherwise
///      create one (linked, not duplicated).
///   3. Derive a deterministic student-identity hash from (school_tenant_id,
///      normalised_name_ar, grade, parent_identity_id) and look it up on
///      <see cref="StudentProfile"/> via the identity-derived id stored in
///      <c>Id</c>. If found, reuse; otherwise create a new profile whose
///      <c>Id</c> is the deterministic hash so subsequent re-imports collapse
///      to the same student record.
///   4. Ensure a <see cref="ChildLink"/> row binds parent ↔ student for the
///      school's tenant.
///
/// The linker does NOT enrol the student into a class — class enrolment
/// is owned by US3 (T077).
/// </summary>
public sealed record RosterLinkOutcome(
    Guid StudentId,
    Guid ParentProfileId,
    bool StudentExisted,
    bool ParentLinked);

public interface IStudentProfileLinker
{
    Task<RosterLinkOutcome> LinkAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string curriculumType,
        ValidatedRosterRow row,
        CancellationToken ct = default);
}

public sealed class StudentProfileLinker : IStudentProfileLinker
{
    private readonly MuallimiDbContext _db;

    public StudentProfileLinker(MuallimiDbContext db) => _db = db;

    public async Task<RosterLinkOutcome> LinkAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string curriculumType,
        ValidatedRosterRow row,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var email = (row.Source.ParentEmail ?? string.Empty).Trim().ToLowerInvariant();
        var parentIdentityId = DeriveGuid($"{schoolTenantId}|parent|{email}");

        // Parent: reuse if exists, otherwise create.
        var parent = await _db.ParentProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId && p.IdentityId == parentIdentityId,
                ct);
        var parentLinked = parent is not null;
        if (parent is null)
        {
            parent = new ParentProfile
            {
                ParentProfileId = Guid.NewGuid(),
                TenantId = tenantId,
                IdentityId = parentIdentityId,
                PreferredLanguage = "ar",
                Locale = "ar-SA",
                Timezone = "Asia/Dubai",
                ConsentState = JsonSerializer.Serialize(new
                {
                    source = "phase5_roster_import",
                    contact_email = email,
                    contact_name = row.Source.ParentName,
                }),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.ParentProfiles.Add(parent);
        }

        // Student: deterministic id for re-import idempotency.
        var studentKey = RosterRowValidator.ComputeDedupKey(row.Source, row.Grade);
        var studentId = DeriveGuid($"{schoolTenantId}|student|{studentKey}");

        var student = await _db.StudentProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.Id == studentId,
                ct);
        var studentExisted = student is not null;
        if (student is null)
        {
            student = new StudentProfile
            {
                Id = studentId,
                TenantId = tenantId,
                DisplayName = row.Source.StudentNameAr, // full fidelity — no normalisation
                CurriculumType = string.IsNullOrWhiteSpace(curriculumType) ? "moe" : curriculumType,
                Grade = row.Grade.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PreferredLanguage = "ar",
                PlanTier = "school",
                SubjectsEnrolled = "[]",
                ConsentState = "granted_by_school",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.StudentProfiles.Add(student);
        }

        // ChildLink: ensure there is one active link per (parent, student).
        var existingLink = await _db.ChildLinks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                l => l.TenantId == tenantId
                     && l.ParentProfileId == parent.ParentProfileId
                     && l.StudentId == student.Id,
                ct);
        if (existingLink is null)
        {
            _db.ChildLinks.Add(new ChildLink
            {
                ChildLinkId = Guid.NewGuid(),
                TenantId = tenantId,
                ParentProfileId = parent.ParentProfileId,
                StudentId = student.Id,
                Role = "guardian",
                EffectiveStart = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        return new RosterLinkOutcome(student.Id, parent.ParentProfileId, studentExisted, parentLinked);
    }

    /// <summary>
    /// Deterministic Guid v5-style: SHA-256 of the input, narrowed to 16
    /// bytes. Stable across processes, machines, and re-imports.
    /// </summary>
    public static Guid DeriveGuid(string seed)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
        var bytes = hash.Take(16).ToArray();
        // Set RFC 4122 variant bits so the Guid parses as a well-formed UUID.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

public static class StudentProfileLinkerServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5StudentProfileLinker(this IServiceCollection services)
    {
        services.AddScoped<IStudentProfileLinker, StudentProfileLinker>();
        return services;
    }
}
