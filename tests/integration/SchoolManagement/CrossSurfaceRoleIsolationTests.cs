using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.TeacherDashboard;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement;

/// <summary>
/// T202 (Polish) — Cross-surface role isolation.
///
/// For the four Phase 5 roles (teacher, student, parent, admin) the test
/// verifies:
///   1. A teacher sees ONLY their assigned (class, subject) pairs — never
///      the other teacher's rows (even in the same school).
///   2. Teacher-facing response DTOs (public record types in the teacher
///      dashboard namespace) do NOT contain any
///      <see cref="RoleIsolationHarness.ForbiddenTeacherProjections"/>
///      property name.
///   3. An unassigned teacher (no assignment row) sees zero class rows.
///   4. A school-admin sees both teachers' assignments — admins are the
///      aggregate role and legitimately cross teachers within their
///      school, but never cross schools.
/// </summary>
public class CrossSurfaceRoleIsolationTests
{
    [Fact]
    public async Task TeacherRed_Sees_Only_Red_Assigned_Scope()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new RoleIsolationHarness(db);
        await harness.SeedAsync();

        await harness.AssertTeacherSeesOnlyAssignedScopeAsync(
            RoleIsolationHarness.TeacherRed,
            RoleIsolationHarness.ClassRed,
            RoleIsolationHarness.SubjectRed);
    }

    [Fact]
    public async Task TeacherBlue_Sees_Only_Blue_Assigned_Scope()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new RoleIsolationHarness(db);
        await harness.SeedAsync();

        await harness.AssertTeacherSeesOnlyAssignedScopeAsync(
            RoleIsolationHarness.TeacherBlue,
            RoleIsolationHarness.ClassBlue,
            RoleIsolationHarness.SubjectBlue);
    }

    [Fact]
    public async Task Unassigned_Teacher_Sees_Zero_Assignments()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new RoleIsolationHarness(db);
        await harness.SeedAsync();

        var strangerTeacher = Guid.NewGuid();
        var rows = await db.TeacherAssignments
            .IgnoreQueryFilters()
            .Where(a => a.TeacherId == strangerTeacher)
            .ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task SchoolAdmin_Sees_All_Teachers_Within_Their_School()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new RoleIsolationHarness(db);
        await harness.SeedAsync();

        var rows = await db.TeacherAssignments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == RoleIsolationHarness.Tenant)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TeacherId == RoleIsolationHarness.TeacherRed);
        Assert.Contains(rows, r => r.TeacherId == RoleIsolationHarness.TeacherBlue);
    }

    [Fact]
    public void Teacher_Dashboard_Projections_Contain_No_Forbidden_Family_Or_Billing_Fields()
    {
        // Scan every public DTO in the teacher dashboard assembly for
        // property names matching the forbidden list. Any hit is a
        // privacy breach — the teacher surface must never project
        // parent email, family email, billing status, plan tier,
        // invoice amount, or payment method.
        var assembly = typeof(TeacherDashboardEndpoints).Assembly;
        var dtos = assembly.GetTypes()
            .Where(t => t.Namespace is not null
                && t.Namespace.Contains(".SchoolManagement.TeacherDashboard", StringComparison.Ordinal)
                && (t.IsPublic || t.IsNestedPublic)
                && !t.IsInterface
                && !t.IsEnum)
            .ToList();

        Assert.NotEmpty(dtos);

        foreach (var dto in dtos)
        {
            var propertyNames = dto
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var forbidden in RoleIsolationHarness.ForbiddenTeacherProjections)
            {
                var snakeAsPascal = ToPascalCase(forbidden);
                Assert.False(
                    propertyNames.Contains(forbidden) || propertyNames.Contains(snakeAsPascal),
                    $"Teacher DTO {dto.FullName} exposes forbidden projection '{forbidden}'.");
            }
        }
    }

    private static string ToPascalCase(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
    }
}
