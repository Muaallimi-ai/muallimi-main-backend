using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Application.Audit;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Identity.Adapters;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity;

/// <summary>
/// T162 — Contract test for the <c>identity.legacy-backfill</c> contract.
///
/// Uses an in-memory EF provider and a <see cref="FixtureBackfillRunner"/>
/// subclass that returns a static snapshot instead of hitting a real legacy
/// schema. This lets the test suite run without a PostgreSQL instance while
/// still asserting every invariant from
/// <c>specs/009-identity-auth/contracts/identity-legacy-backfill-contract.md</c>.
///
/// Invariants asserted here:
///   1. Every non-deleted legacy user gets an Identity.User with the same Id.
///   2. Password hashes are copied verbatim — no rehash.
///   3. EmailConfirmed=true → User.Status=Active; false → PendingEmailVerification.
///   4. The backfill is idempotent: running it twice produces zero extra rows.
///   5. Deleted legacy users are skipped.
///   6. At least one audit event (<c>backfill_role_granted</c>) is emitted per role.
/// </summary>
public class BackfillContractTests
{
    // ── Deterministic fixture IDs (match legacy-auth-snapshot.sql) ────────
    private static readonly Guid ParentId1 = Guid.Parse("a0000001-0000-0000-0000-000000000001");
    private static readonly Guid ParentId2 = Guid.Parse("a0000002-0000-0000-0000-000000000002");
    private static readonly Guid StudentId1 = Guid.Parse("b0000001-0000-0000-0000-000000000001");
    private static readonly Guid SuperAdminId = Guid.Parse("f0000001-0000-0000-0000-000000000001");
    private static readonly Guid DeletedUserId = Guid.Parse("dead0001-0000-0000-0000-000000000001");
    private static readonly Guid SchoolId = Guid.Parse("99000001-0000-0000-0000-000000000001");
    private static readonly Guid SchoolAdminId = Guid.Parse("c0000001-0000-0000-0000-000000000001");

    private const string ParentHash = "$2a$12$dummyhashparent1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
    private const string SuperAdminHash = "$2a$12$dummyhashsuper1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    // ── Test DB setup ─────────────────────────────────────────────────────

    private sealed class TestDbContext : MuallimiDbContext
    {
        public TestDbContext(DbContextOptions<MuallimiDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Ignore<Muallimi.Domain.Curriculum.ContentChunk>();
            mb.Ignore<Muallimi.Domain.Curriculum.QaCacheEntry>();
        }
    }

    private static (TestDbContext db, CapturingAuditEventEmitter audit) CreateContext()
    {
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase($"backfill-contract-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new TestDbContext(options);
        db.Database.EnsureCreated();
        return (db, new CapturingAuditEventEmitter());
    }

    private static async Task SeedPlatformAndRolesAsync(TestDbContext db, CancellationToken ct = default)
    {
        db.IdentityTenants.Add(new Tenant
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Type = TenantType.Platform,
            DisplayName = "Platform",
            Locale = "ar",
            Status = TenantStatus.Active,
            Metadata = "{}",
        });
        var roleDefs = new (string Name, RoleScope Scope)[]
        {
            ("parent", RoleScope.Family),
            ("student", RoleScope.Family),
            ("school-admin", RoleScope.School),
            ("teacher", RoleScope.School),
            ("curriculum-admin", RoleScope.Platform),
            ("subject-expert", RoleScope.Platform),
            ("super-admin", RoleScope.Platform),
        };
        foreach (var (n, s) in roleDefs)
        {
            db.IdentityRoles.Add(new Role
            {
                Id = Guid.NewGuid(),
                Name = n,
                Scope = s,
                IsSystem = true,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    // ── Fixture runner subclass ───────────────────────────────────────────

    /// <summary>
    /// Overrides snapshot loading and domain-entity linking to work with
    /// the EF InMemory provider.
    /// </summary>
    private sealed class FixtureBackfillRunner : BackfillScriptRunner
    {
        public FixtureBackfillRunner(
            MuallimiDbContext db,
            AuditEventEmitter audit)
            : base(db, audit, NullLogger<BackfillScriptRunner>.Instance) { }

        protected override Task<LegacySnapshot> LoadLegacySnapshotAsync(
            string _, CancellationToken ct)
        {
            var snapshot = new LegacySnapshot
            {
                Users =
                [
                    new LegacyUser
                    {
                        Id = ParentId1,
                        Email = "parent1@example.com",
                        NormalizedEmail = "PARENT1@EXAMPLE.COM",
                        PasswordHash = ParentHash,
                        FullName = "أحمد الشامة",
                        Role = "Parent",
                        EmailConfirmed = true,
                        CreatedAt = new DateTime(2024, 1, 10, 10, 0, 0, DateTimeKind.Utc),
                    },
                    new LegacyUser
                    {
                        Id = ParentId2,
                        Email = "parent2@example.com",
                        NormalizedEmail = "PARENT2@EXAMPLE.COM",
                        PasswordHash = "$2a$12$dummyhashparent2xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                        FullName = "فاطمة حسن",
                        Role = "Parent",
                        EmailConfirmed = false,
                        CreatedAt = new DateTime(2024, 2, 1, 8, 0, 0, DateTimeKind.Utc),
                    },
                    new LegacyUser
                    {
                        Id = StudentId1,
                        Email = null,
                        NormalizedEmail = null,
                        PasswordHash = "$2a$12$dummyhashstudent1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                        FullName = "محمد أحمد",
                        Role = "Student",
                        EmailConfirmed = false,
                        CreatedAt = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc),
                    },
                    new LegacyUser
                    {
                        Id = SchoolAdminId,
                        Email = "schooladmin@school1.example.com",
                        NormalizedEmail = "SCHOOLADMIN@SCHOOL1.EXAMPLE.COM",
                        PasswordHash = "$2a$12$dummyhashschoola1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                        FullName = "مدير المدرسة",
                        Role = "SchoolAdmin",
                        EmailConfirmed = true,
                        CreatedAt = new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc),
                    },
                    new LegacyUser
                    {
                        Id = SuperAdminId,
                        Email = "superadmin@platform.example.com",
                        NormalizedEmail = "SUPERADMIN@PLATFORM.EXAMPLE.COM",
                        PasswordHash = SuperAdminHash,
                        FullName = "المسؤول الأعلى",
                        Role = "SuperAdmin",
                        EmailConfirmed = true,
                        CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    },
                    // Deleted — must be skipped
                    new LegacyUser
                    {
                        Id = DeletedUserId,
                        Email = "deleted@example.com",
                        NormalizedEmail = "DELETED@EXAMPLE.COM",
                        PasswordHash = "$2a$12$dummyhashdeleted1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                        FullName = "Deleted User",
                        Role = "Parent",
                        EmailConfirmed = true,
                        CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        DeletedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    },
                ],
                StudentProfiles =
                [
                    new LegacyStudentProfile
                    {
                        Id = Guid.Parse("ba000001-0000-0000-0000-000000000001"),
                        UserId = StudentId1,
                        ParentUserId = ParentId1,
                    },
                ],
                SchoolIds = new Dictionary<Guid, Guid>
                {
                    [SchoolAdminId] = SchoolId,
                },
                DistinctSchoolIds = [SchoolId],
            };
            return Task.FromResult(snapshot);
        }

        // InMemory provider cannot run raw UPDATE SQL — no-op for testing.
        protected override Task LinkDomainEntitiesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_Creates_Identity_User_For_Every_NonDeleted_LegacyUser()
    {
        var (db, audit) = CreateContext();
        await SeedPlatformAndRolesAsync(db);
        var runner = new FixtureBackfillRunner(db, audit);

        var result = await runner.RunAsync("legacy_auth", dryRun: false);

        Assert.Equal(0, result.Errors.Count);

        // 5 non-deleted users: parent1, parent2, student1, school-admin, super-admin
        var allUsers = await db.IdentityUsers.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(5, allUsers.Count);

        Assert.Contains(allUsers, u => u.Id == ParentId1);
        Assert.Contains(allUsers, u => u.Id == ParentId2);
        Assert.Contains(allUsers, u => u.Id == StudentId1);
        Assert.Contains(allUsers, u => u.Id == SchoolAdminId);
        Assert.Contains(allUsers, u => u.Id == SuperAdminId);

        // Deleted user must be absent
        Assert.DoesNotContain(allUsers, u => u.Id == DeletedUserId);
    }

    [Fact]
    public async Task Backfill_Copies_PasswordHash_Verbatim_Without_Rehash()
    {
        var (db, audit) = CreateContext();
        await SeedPlatformAndRolesAsync(db);
        var runner = new FixtureBackfillRunner(db, audit);

        await runner.RunAsync("legacy_auth", dryRun: false);

        var parent1 = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == ParentId1);
        Assert.Equal(ParentHash, parent1.PasswordHash);

        var superAdmin = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == SuperAdminId);
        Assert.Equal(SuperAdminHash, superAdmin.PasswordHash);
    }

    [Fact]
    public async Task Backfill_Sets_Active_Status_For_EmailConfirmed_Users()
    {
        var (db, audit) = CreateContext();
        await SeedPlatformAndRolesAsync(db);
        var runner = new FixtureBackfillRunner(db, audit);

        await runner.RunAsync("legacy_auth", dryRun: false);

        var parent1 = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == ParentId1);
        Assert.Equal(UserStatus.Active, parent1.Status);
        Assert.True(parent1.EmailVerified);

        var parent2 = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == ParentId2);
        Assert.Equal(UserStatus.PendingEmailVerification, parent2.Status);
        Assert.False(parent2.EmailVerified);
    }

    [Fact]
    public async Task Backfill_Is_Idempotent_Running_Twice_Produces_No_Extra_Rows()
    {
        var (db, audit) = CreateContext();
        await SeedPlatformAndRolesAsync(db);
        var runner = new FixtureBackfillRunner(db, audit);

        await runner.RunAsync("legacy_auth", dryRun: false);
        var usersAfterFirst = await db.IdentityUsers.IgnoreQueryFilters().CountAsync();
        var rolesAfterFirst = await db.IdentityUserRoles.IgnoreQueryFilters().CountAsync();

        // Second run — no new rows must appear.
        await runner.RunAsync("legacy_auth", dryRun: false);
        var usersAfterSecond = await db.IdentityUsers.IgnoreQueryFilters().CountAsync();
        var rolesAfterSecond = await db.IdentityUserRoles.IgnoreQueryFilters().CountAsync();

        Assert.Equal(usersAfterFirst, usersAfterSecond);
        Assert.Equal(rolesAfterFirst, rolesAfterSecond);
    }

    [Fact]
    public async Task Backfill_Emits_One_AuditEvent_Per_RoleGranted()
    {
        var (db, audit) = CreateContext();
        await SeedPlatformAndRolesAsync(db);
        var runner = new FixtureBackfillRunner(db, audit);

        await runner.RunAsync("legacy_auth", dryRun: false);

        // Every audit event action must be backfill_role_granted.
        Assert.All(audit.Events, e => Assert.Equal("backfill_role_granted", e.Action));
        Assert.All(audit.Events, e => Assert.Equal("succeeded", e.Outcome));
        // At least 5 users → at least 5 role grants.
        Assert.True(audit.Events.Count >= 5,
            $"Expected ≥ 5 audit events, got {audit.Events.Count}.");
    }

    [Fact]
    public async Task Backfill_DryRun_Does_Not_Write_Any_Users()
    {
        var (db, audit) = CreateContext();
        await SeedPlatformAndRolesAsync(db);
        var runner = new FixtureBackfillRunner(db, audit);

        var result = await runner.RunAsync("legacy_auth", dryRun: true);

        Assert.True(result.DryRun);
        var allUsers = await db.IdentityUsers.IgnoreQueryFilters().ToListAsync();
        Assert.Empty(allUsers);
    }

    [Fact]
    public async Task Backfill_Student_Placed_In_Parents_Family_Tenant()
    {
        var (db, audit) = CreateContext();
        await SeedPlatformAndRolesAsync(db);
        var runner = new FixtureBackfillRunner(db, audit);

        await runner.RunAsync("legacy_auth", dryRun: false);

        var parent = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == ParentId1);
        var student = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == StudentId1);

        // Student must be in parent's Family tenant.
        Assert.Equal(parent.TenantId, student.TenantId);
        Assert.Equal(AccountType.Managed, student.AccountType);
        Assert.Equal(ParentId1, student.ManagedByUserId);
    }

    [Fact]
    public async Task Backfill_Locale_Defaults_To_Arabic()
    {
        var (db, audit) = CreateContext();
        await SeedPlatformAndRolesAsync(db);
        var runner = new FixtureBackfillRunner(db, audit);

        await runner.RunAsync("legacy_auth", dryRun: false);

        var allUsers = await db.IdentityUsers.IgnoreQueryFilters().ToListAsync();
        Assert.All(allUsers, u => Assert.Equal("ar", u.Locale));
    }
}

/// <summary>
/// Shared capturing emitter for audit assertions.
/// </summary>
public sealed class CapturingAuditEventEmitter : AuditEventEmitter
{
    public List<AuditEvent> Events { get; } = [];
    public CapturingAuditEventEmitter() : base(Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditEventEmitter>.Instance) { }
    public override void Emit(AuditEvent e) { Events.Add(e); base.Emit(e); }
}
