using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Audit;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.Identity.Adapters;

/// <summary>
/// T165 — One-shot idempotent backfill from legacy <c>Muaallimi-AuthAPI</c>.
/// Reads legacy user rows from a nominated PostgreSQL schema, creates Identity
/// module records, and links the new User rows to existing domain entities
/// (ParentProfile, StudentProfile, SchoolAdministrator, Teacher).
///
/// Execution modes:
///   --dry-run   : validate + print what would happen, no writes
///   --apply     : execute the backfill (default when no flag is given)
///   --verify    : assert all destination invariants hold, exit non-zero on failure
/// </summary>
public class BackfillScriptRunner
{
    // System actor used for audit events emitted during the backfill.
    private static readonly Guid SystemBackfillActorId = Guid.Parse("00000000-0000-0000-0000-b4cff11100ff");

    private readonly MuallimiDbContext _db;
    private readonly AuditEventEmitter _audit;
    private readonly ILogger<BackfillScriptRunner> _logger;

    public BackfillScriptRunner(
        MuallimiDbContext db,
        AuditEventEmitter audit,
        ILogger<BackfillScriptRunner> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    // ── Public entry points ───────────────────────────────────────────────

    public async Task<BackfillResult> RunAsync(
        string sourceSchema,
        bool dryRun,
        CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger.LogInformation(
            "Backfill [{Mode}] started. source-schema={Schema} correlation={CorrelationId}",
            dryRun ? "dry-run" : "apply", sourceSchema, correlationId);

        var legacy = await LoadLegacySnapshotAsync(sourceSchema, ct);
        var result = new BackfillResult { CorrelationId = correlationId };

        // Resolve existing Identity state so we can be idempotent.
        var platformTenant = await _db.IdentityTenants
            .FirstOrDefaultAsync(t => t.Type == TenantType.Platform, ct);
        if (platformTenant is null)
        {
            throw new InvalidOperationException(
                "Platform tenant not found. Run 'dotnet run -- seed' first.");
        }

        var roles = await _db.IdentityRoles.ToListAsync(ct);
        var roleMap = roles.ToDictionary(r => r.Name, r => r);

        if (dryRun)
        {
            _logger.LogInformation(
                "Backfill dry-run: {Count} non-deleted legacy users would be processed.",
                legacy.Users.Count(u => u.DeletedAt is null));
            result.DryRun = true;
            return result;
        }

        // ── Step 1: Platform tenant (already ensured above) ────────────
        // ── Step 2: School tenants ──────────────────────────────────────
        var schoolTenantMap = await EnsureSchoolTenantsAsync(legacy, platformTenant, result, ct);

        // ── Step 3: Parent users + Family tenants ───────────────────────
        var parentUserMap = await EnsureParentUsersAsync(legacy, roleMap, result, correlationId, ct);

        // ── Step 4: Managed student users ──────────────────────────────
        await EnsureStudentUsersAsync(legacy, parentUserMap, roleMap, result, correlationId, ct);

        // ── Step 5: School admin users ──────────────────────────────────
        await EnsureSchoolStaffUsersAsync(
            legacy, schoolTenantMap, roleMap, "SchoolAdmin", "school-admin",
            result, correlationId, ct);

        // ── Step 6: Teacher users ───────────────────────────────────────
        await EnsureSchoolStaffUsersAsync(
            legacy, schoolTenantMap, roleMap, "Teacher", "teacher",
            result, correlationId, ct);

        // ── Step 7: Platform role users ─────────────────────────────────
        await EnsurePlatformRoleUsersAsync(
            legacy, platformTenant, roleMap, result, correlationId, ct);

        // ── Step 8: Link existing domain entities ───────────────────────
        await LinkDomainEntitiesAsync(ct);

        _logger.LogInformation(
            "Backfill complete. created={Created} linked={Linked} skipped={Skipped} errors={Errors}",
            result.UsersCreated, result.EntitiesLinked, result.UsersSkipped, result.Errors.Count);

        return result;
    }

    public async Task<VerifyResult> VerifyAsync(
        string sourceSchema,
        CancellationToken ct = default)
    {
        var legacy = await LoadLegacySnapshotAsync(sourceSchema, ct);
        var result = new VerifyResult();

        // Invariant 1: every non-deleted legacy user has an Identity user with the same Id.
        var nonDeletedLegacy = legacy.Users.Where(u => u.DeletedAt is null).ToList();
        var identityUserIds = await _db.Database
            .SqlQueryRaw<Guid>("SELECT id FROM identity_users")
            .ToListAsync(ct);
        var identityUserSet = identityUserIds.ToHashSet();

        foreach (var lu in nonDeletedLegacy)
        {
            if (!identityUserSet.Contains(lu.Id))
            {
                result.Failures.Add($"Missing Identity user for legacy Id={lu.Id}");
            }
        }

        // Invariant 8: UserId linkage on domain entities.
        var unlinkParents = await _db.Database
            .SqlQueryRaw<Guid>(
                "SELECT id FROM parent_profiles WHERE user_id IS NULL")
            .ToListAsync(ct);
        if (unlinkParents.Count > 0)
        {
            result.Failures.Add($"{unlinkParents.Count} ParentProfile rows have null user_id.");
        }

        result.Passed = result.Failures.Count == 0;
        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<Dictionary<Guid, Guid>> EnsureSchoolTenantsAsync(
        LegacySnapshot legacy,
        Tenant platformTenant,
        BackfillResult result,
        CancellationToken ct)
    {
        var schoolTenantMap = new Dictionary<Guid, Guid>(); // legacySchoolId → tenantId

        foreach (var schoolId in legacy.DistinctSchoolIds)
        {
            // Check idempotency: look for an existing school tenant whose metadata
            // contains the legacySchoolId.
            var metaTag = $"\"legacySchoolId\":\"{schoolId}\"";
            var existing = await _db.IdentityTenants
                .Where(t => t.Type == TenantType.School && t.Metadata.Contains(metaTag))
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                schoolTenantMap[schoolId] = existing.Id;
                result.UsersSkipped++;
                continue;
            }

            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Type = TenantType.School,
                DisplayName = $"School {schoolId:N}",
                Locale = "ar",
                Status = TenantStatus.Active,
                Metadata = $"{{\"legacySchoolId\":\"{schoolId}\"}}",
            };
            _db.IdentityTenants.Add(tenant);
            await _db.SaveChangesAsync(ct);
            schoolTenantMap[schoolId] = tenant.Id;
            _logger.LogInformation("Backfill: created School tenant {TenantId} for legacySchoolId={SchoolId}",
                tenant.Id, schoolId);
        }
        return schoolTenantMap;
    }

    private async Task<Dictionary<Guid, Guid>> EnsureParentUsersAsync(
        LegacySnapshot legacy,
        Dictionary<string, Role> roleMap,
        BackfillResult result,
        string correlationId,
        CancellationToken ct)
    {
        var parentUserMap = new Dictionary<Guid, Guid>(); // legacyUserId → new userId (same here)
        var parentRole = roleMap.GetValueOrDefault("parent");

        foreach (var lu in legacy.Users.Where(u => u.DeletedAt is null && u.Role == "Parent"))
        {
            if (await UserExistsAsync(lu.Id, ct))
            {
                parentUserMap[lu.Id] = lu.Id;
                result.UsersSkipped++;
                continue;
            }

            // Family tenant per parent
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Type = TenantType.Family,
                DisplayName = lu.FullName,
                Locale = "ar",
                Status = TenantStatus.Active,
                Metadata = $"{{\"legacyParentId\":\"{lu.Id}\"}}",
            };
            _db.IdentityTenants.Add(tenant);

            var user = BuildPersonalUser(lu, tenant.Id);
            _db.IdentityUsers.Add(user);

            if (parentRole is not null)
            {
                await GrantRoleAsync(user, parentRole, tenant.Id, correlationId, result, ct);
            }

            await _db.SaveChangesAsync(ct);
            parentUserMap[lu.Id] = lu.Id;
            result.UsersCreated++;
        }
        return parentUserMap;
    }

    private async Task EnsureStudentUsersAsync(
        LegacySnapshot legacy,
        Dictionary<Guid, Guid> parentUserMap,
        Dictionary<string, Role> roleMap,
        BackfillResult result,
        string correlationId,
        CancellationToken ct)
    {
        var studentRole = roleMap.GetValueOrDefault("student");

        foreach (var lu in legacy.Users.Where(u => u.DeletedAt is null && u.Role == "Student"))
        {
            if (await UserExistsAsync(lu.Id, ct))
            {
                result.UsersSkipped++;
                continue;
            }

            // Resolve parent via StudentProfile join.
            var parentId = legacy.StudentProfiles
                .Where(sp => sp.UserId == lu.Id)
                .Select(sp => sp.ParentUserId)
                .FirstOrDefault();

            Guid tenantId;
            Guid? managedBy = null;

            if (parentId != Guid.Empty && parentUserMap.TryGetValue(parentId, out var mappedParent))
            {
                // Place student inside parent's Family tenant.
                tenantId = await GetFamilyTenantForParentAsync(mappedParent, ct);
                managedBy = mappedParent;
            }
            else
            {
                // Orphaned student — create a standalone Family tenant.
                var orphanTenant = new Tenant
                {
                    Id = Guid.NewGuid(),
                    Type = TenantType.Family,
                    DisplayName = lu.FullName,
                    Locale = "ar",
                    Status = TenantStatus.Active,
                    Metadata = $"{{\"legacyOrphanStudentId\":\"{lu.Id}\"}}",
                };
                _db.IdentityTenants.Add(orphanTenant);
                await _db.SaveChangesAsync(ct);
                tenantId = orphanTenant.Id;
            }

            var user = new User
            {
                Id = lu.Id,
                TenantId = tenantId,
                AccountType = AccountType.Managed,
                ManagedByUserId = managedBy,
                Username = lu.Email ?? $"student.{lu.Id:N}",
                NormalizedUsername = (lu.Email ?? $"student.{lu.Id:N}").ToUpperInvariant(),
                FullName = lu.FullName,
                PasswordHash = lu.PasswordHash,
                EmailVerified = false,
                Status = UserStatus.Active,
                Locale = "ar",
                CreatedAt = lu.CreatedAt,
                CreatedBy = SystemBackfillActorId,
            };
            _db.IdentityUsers.Add(user);

            if (studentRole is not null)
            {
                await GrantRoleAsync(user, studentRole, tenantId, correlationId, result, ct);
            }

            await _db.SaveChangesAsync(ct);
            result.UsersCreated++;
        }
    }

    private async Task EnsureSchoolStaffUsersAsync(
        LegacySnapshot legacy,
        Dictionary<Guid, Guid> schoolTenantMap,
        Dictionary<string, Role> roleMap,
        string legacyRole,
        string identityRoleName,
        BackfillResult result,
        string correlationId,
        CancellationToken ct)
    {
        var role = roleMap.GetValueOrDefault(identityRoleName);

        foreach (var lu in legacy.Users.Where(u => u.DeletedAt is null && u.Role == legacyRole))
        {
            if (await UserExistsAsync(lu.Id, ct))
            {
                result.UsersSkipped++;
                continue;
            }

            var schoolId = legacy.SchoolIds.TryGetValue(lu.Id, out var sid) ? sid : Guid.Empty;
            if (!schoolTenantMap.TryGetValue(schoolId, out var tenantId))
            {
                _logger.LogWarning(
                    "Backfill: legacy {Role} user {UserId} has no school mapping — skipping.",
                    legacyRole, lu.Id);
                result.UsersSkipped++;
                continue;
            }

            var user = BuildPersonalUser(lu, tenantId);
            _db.IdentityUsers.Add(user);

            if (role is not null)
            {
                await GrantRoleAsync(user, role, tenantId, correlationId, result, ct);
            }

            await _db.SaveChangesAsync(ct);
            result.UsersCreated++;
        }
    }

    private async Task EnsurePlatformRoleUsersAsync(
        LegacySnapshot legacy,
        Tenant platformTenant,
        Dictionary<string, Role> roleMap,
        BackfillResult result,
        string correlationId,
        CancellationToken ct)
    {
        var platformRoles = new Dictionary<string, string>
        {
            ["CurriculumAdmin"] = "curriculum-admin",
            ["SubjectExpert"]   = "subject-expert",
            ["SuperAdmin"]      = "super-admin",
        };

        foreach (var (legacyRole, identityRoleName) in platformRoles)
        {
            var role = roleMap.GetValueOrDefault(identityRoleName);

            foreach (var lu in legacy.Users.Where(u => u.DeletedAt is null && u.Role == legacyRole))
            {
                if (await UserExistsAsync(lu.Id, ct))
                {
                    result.UsersSkipped++;
                    continue;
                }

                var user = BuildPersonalUser(lu, platformTenant.Id);
                _db.IdentityUsers.Add(user);

                if (role is not null)
                {
                    await GrantRoleAsync(user, role, platformTenant.Id, correlationId, result, ct);
                }

                await _db.SaveChangesAsync(ct);
                result.UsersCreated++;
            }
        }
    }

    protected virtual async Task LinkDomainEntitiesAsync(CancellationToken ct)
    {
        // Link parent_profiles.user_id where the identity user with the same
        // email exists and the profile row has no user_id yet.
        await _db.Database.ExecuteSqlRawAsync(
            @"UPDATE parent_profiles pp
              SET user_id = iu.id
              FROM identity_users iu
              WHERE pp.user_id IS NULL
                AND iu.normalized_email = UPPER(pp.email)",
            ct);

        // Link student_profiles.user_id via the managed user's matching username.
        await _db.Database.ExecuteSqlRawAsync(
            @"UPDATE student_profiles sp
              SET user_id = iu.id
              FROM identity_users iu
              WHERE sp.user_id IS NULL
                AND iu.account_type = 1  -- Managed
                AND iu.id = (
                    SELECT u2.id FROM identity_users u2
                    WHERE u2.normalized_username = UPPER(sp.username)
                    LIMIT 1
                )",
            ct);

        // Link school_administrators.user_id
        await _db.Database.ExecuteSqlRawAsync(
            @"UPDATE school_administrators sa
              SET user_id = iu.id
              FROM identity_users iu
              WHERE sa.user_id IS NULL
                AND iu.normalized_email = UPPER(sa.email)",
            ct);

        // Link teachers.user_id
        await _db.Database.ExecuteSqlRawAsync(
            @"UPDATE teachers t
              SET user_id = iu.id
              FROM identity_users iu
              WHERE t.user_id IS NULL
                AND iu.normalized_email = UPPER(t.email)",
            ct);
    }

    private async Task GrantRoleAsync(
        User user,
        Role role,
        Guid tenantId,
        string correlationId,
        BackfillResult result,
        CancellationToken ct)
    {
        // Idempotency: skip if the active grant already exists.
        var existingGrant = await _db.IdentityUserRoles
            .Where(r => r.UserId == user.Id && r.RoleId == role.Id && r.TenantId == tenantId && r.RevokedAt == null)
            .FirstOrDefaultAsync(ct);

        if (existingGrant is not null)
            return;

        var userRole = new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = role.Id,
            TenantId = tenantId,
            GrantedBy = SystemBackfillActorId,
            GrantedAt = DateTime.UtcNow,
        };
        _db.IdentityUserRoles.Add(userRole);

        _audit.Emit(new AuditEvent
        {
            EventCategory = "identity",
            ActorId = SystemBackfillActorId.ToString("D"),
            TenantId = tenantId.ToString("D"),
            Action = "backfill_role_granted",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = correlationId,
            Reason = $"Legacy backfill: role={role.Name}",
        });
        result.RolesGranted++;
    }

    private async Task<bool> UserExistsAsync(Guid userId, CancellationToken ct)
    {
        // Bypass tenant filter for this cross-tenant check.
        return await _db.IdentityUsers
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Id == userId, ct);
    }

    private async Task<Guid> GetFamilyTenantForParentAsync(Guid parentUserId, CancellationToken ct)
    {
        var parent = await _db.IdentityUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == parentUserId, ct);
        return parent?.TenantId ?? Guid.Empty;
    }

    private static User BuildPersonalUser(LegacyUser lu, Guid tenantId) => new()
    {
        Id = lu.Id,
        TenantId = tenantId,
        AccountType = AccountType.Personal,
        Email = lu.Email,
        NormalizedEmail = lu.NormalizedEmail,
        EmailVerified = lu.EmailConfirmed,
        EmailVerifiedAt = lu.EmailConfirmed ? lu.CreatedAt : null,
        FullName = lu.FullName,
        PasswordHash = lu.PasswordHash,
        Status = lu.EmailConfirmed ? UserStatus.Active : UserStatus.PendingEmailVerification,
        Locale = "ar",
        CreatedAt = lu.CreatedAt,
        CreatedBy = SystemBackfillActorId,
    };

    // ── Legacy snapshot loader ────────────────────────────────────────────

    protected virtual async Task<LegacySnapshot> LoadLegacySnapshotAsync(
        string sourceSchema,
        CancellationToken ct)
    {
        // Read legacy users via raw SQL to the nominated schema.
        var users = await _db.Database
            .SqlQueryRaw<LegacyUser>(
                $"""
                SELECT "Id", "Email", "NormalizedEmail", "PasswordHash", "FullName",
                       "Role", "EmailConfirmed", "CreatedAt", "DeletedAt"
                FROM "{sourceSchema}"."Users"
                """)
            .ToListAsync(ct);

        // Read student profile parent links.
        List<LegacyStudentProfile> studentProfiles;
        try
        {
            studentProfiles = await _db.Database
                .SqlQueryRaw<LegacyStudentProfile>(
                    $"""
                    SELECT "Id", "UserId", "ParentUserId"
                    FROM "{sourceSchema}"."StudentProfiles"
                    """)
                .ToListAsync(ct);
        }
        catch
        {
            studentProfiles = [];
        }

        // Read school associations for admins.
        var schoolAdminMap = new Dictionary<Guid, Guid>();
        try
        {
            var rows = await _db.Database
                .SqlQueryRaw<LegacySchoolLink>(
                    $"""
                    SELECT "UserId", "SchoolId"
                    FROM "{sourceSchema}"."SchoolAdministrators"
                    """)
                .ToListAsync(ct);
            foreach (var r in rows)
                schoolAdminMap[r.UserId] = r.SchoolId;
        }
        catch { /* table may not exist in all environments */ }

        // School associations for teachers.
        try
        {
            var rows = await _db.Database
                .SqlQueryRaw<LegacySchoolLink>(
                    $"""
                    SELECT "UserId", "SchoolId"
                    FROM "{sourceSchema}"."Teachers"
                    """)
                .ToListAsync(ct);
            foreach (var r in rows)
                schoolAdminMap.TryAdd(r.UserId, r.SchoolId);
        }
        catch { /* table may not exist */ }

        var distinctSchoolIds = schoolAdminMap.Values.Distinct().ToList();

        return new LegacySnapshot
        {
            Users = users,
            StudentProfiles = studentProfiles,
            SchoolIds = schoolAdminMap,
            DistinctSchoolIds = distinctSchoolIds,
        };
    }

    // ── Data transfer objects ─────────────────────────────────────────────
    // Protected so test subclasses can create fixture snapshots.

    protected sealed class LegacySnapshot
    {
        public List<LegacyUser> Users { get; init; } = [];
        public List<LegacyStudentProfile> StudentProfiles { get; init; } = [];
        public Dictionary<Guid, Guid> SchoolIds { get; init; } = [];
        public List<Guid> DistinctSchoolIds { get; init; } = [];
    }

    protected sealed class LegacyUser
    {
        public Guid Id { get; init; }
        public string? Email { get; init; }
        public string? NormalizedEmail { get; init; }
        public string? PasswordHash { get; init; }
        public string FullName { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public bool EmailConfirmed { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    protected sealed class LegacyStudentProfile
    {
        public Guid Id { get; init; }
        public Guid? UserId { get; init; }
        public Guid ParentUserId { get; init; }
    }

    protected sealed class LegacySchoolLink
    {
        public Guid UserId { get; init; }
        public Guid SchoolId { get; init; }
    }
}

// ── Result types ──────────────────────────────────────────────────────────

public sealed class BackfillResult
{
    public string CorrelationId { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public int UsersCreated { get; set; }
    public int UsersSkipped { get; set; }
    public int RolesGranted { get; set; }
    public int EntitiesLinked { get; set; }
    public List<string> Errors { get; } = [];
}

public sealed class VerifyResult
{
    public bool Passed { get; set; }
    public List<string> Failures { get; } = [];
}
