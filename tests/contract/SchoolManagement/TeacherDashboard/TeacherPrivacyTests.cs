using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.TeacherDashboard;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.TeacherDashboard;

/// <summary>
/// T105 (US5) — Role privacy test for the teacher surface.
///
/// The contract (specs/007-school-management-b2b/contracts/school-dashboard-contract.md,
/// FR-018, CR-001) forbids surfacing billing / plan-tier / family-private
/// fields to teachers. This test enforces that invariant by:
///   • sweeping every field name returned by the teacher dashboard service
///     and the student detail response and asserting none of them match
///     the forbidden vocabulary;
///   • asserting the seeded <see cref="Muallimi.Domain.StudentExperience.StudentProfile.PlanTier"/>
///     value never appears in any string property of the response
///     (positive evidence that the field is filtered, not just renamed).
/// </summary>
public class TeacherPrivacyTests
{
    private static readonly string[] ForbiddenPropertyNames =
    {
        "billing",
        "plan",
        "plantier",
        "plan_tier",
        "invoice",
        "subscription",
        "parent_email",
        "parentemail",
        "family_email",
        "familyemail",
        "parent_phone",
        "guardian",
        "address",
        "payment",
    };

    [Fact]
    public async Task Dashboard_Response_Has_No_Billing_Or_PlanTier_Fields()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetTeacherDashboardAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            CancellationToken.None);

        Assert.NotNull(response);
        AssertNoForbiddenFieldNames(response!);
    }

    [Fact]
    public async Task Student_Detail_Response_Has_No_Billing_Or_PlanTier_Fields()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetStudentDetailAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            harness.AlphaClassAStudents[1],
            CancellationToken.None);

        Assert.NotNull(response);
        AssertNoForbiddenFieldNames(response!);
    }

    [Fact]
    public async Task ClassSubject_Detail_Response_Has_No_Billing_Or_PlanTier_Fields()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetClassSubjectDetailAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            TeacherDashboardHarness.ClassAlpha,
            TeacherDashboardHarness.SubjectMath,
            CancellationToken.None);

        Assert.NotNull(response);
        AssertNoForbiddenFieldNames(response!);
        // Positive evidence: seeded PlanTier="premium" must NOT appear in the payload.
        Assert.DoesNotContain("premium", SerializeStringLeaves(response!));
    }

    private static void AssertNoForbiddenFieldNames(object node)
    {
        foreach (var name in EnumerateFieldNames(node))
        {
            foreach (var forbidden in ForbiddenPropertyNames)
            {
                Assert.False(
                    name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"Teacher dashboard leaked forbidden field: {name}");
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateFieldNames(object? node, int depth = 0)
    {
        if (node is null || depth > 6) yield break;
        var type = node.GetType();
        if (type.IsPrimitive || type == typeof(string) || type == typeof(Guid) || type == typeof(DateTime)
            || type == typeof(decimal) || Nullable.GetUnderlyingType(type) is not null)
        {
            yield break;
        }

        if (node is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                foreach (var name in EnumerateFieldNames(item, depth + 1))
                    yield return name;
            }
            yield break;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            yield return prop.Name;
            var value = prop.GetValue(node);
            foreach (var name in EnumerateFieldNames(value, depth + 1))
                yield return name;
        }
    }

    private static string SerializeStringLeaves(object node)
    {
        var sb = new System.Text.StringBuilder();
        Walk(node, sb, 0);
        return sb.ToString();

        static void Walk(object? n, System.Text.StringBuilder sb, int depth)
        {
            if (n is null || depth > 6) return;
            if (n is string s) { sb.Append(s).Append('|'); return; }
            var type = n.GetType();
            if (type.IsPrimitive || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(decimal)) return;
            if (n is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable) Walk(item, sb, depth + 1);
                return;
            }
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Walk(prop.GetValue(n), sb, depth + 1);
            }
        }
    }
}
