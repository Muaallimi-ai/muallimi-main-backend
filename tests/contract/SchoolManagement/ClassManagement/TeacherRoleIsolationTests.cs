using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.TeacherAssignment;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.ClassManagement;

/// <summary>
/// T070 (US3) — Role isolation test verifying teachers see only assigned
/// classes and subjects.
///
/// Uses the shared <see cref="RoleIsolationHarness"/> (T028) so every
/// Phase 5 teacher-scoped test follows the same pattern. Asserts that
/// <see cref="ITeacherAssignmentRepository.ListActiveForTeacherAsync"/>
/// — the primary projection behind the teacher dashboard — returns only
/// rows for the teacher's own (class, subject) scopes.
/// </summary>
public class TeacherRoleIsolationTests
{
    [Fact]
    public async Task Teacher_Active_Assignments_Scope_Is_Tight()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new RoleIsolationHarness(db);
        await harness.SeedAsync();

        var repo = new TeacherAssignmentRepository(db);
        var red = await repo.ListActiveForTeacherAsync(RoleIsolationHarness.Tenant, RoleIsolationHarness.TeacherRed, CancellationToken.None);
        var blue = await repo.ListActiveForTeacherAsync(RoleIsolationHarness.Tenant, RoleIsolationHarness.TeacherBlue, CancellationToken.None);

        Assert.Single(red);
        Assert.Single(blue);
        Assert.Equal(RoleIsolationHarness.ClassRed, red[0].ClassGroupId);
        Assert.Equal(RoleIsolationHarness.SubjectRed, red[0].SubjectId);
        Assert.Equal(RoleIsolationHarness.ClassBlue, blue[0].ClassGroupId);
        Assert.Equal(RoleIsolationHarness.SubjectBlue, blue[0].SubjectId);

        await harness.AssertTeacherSeesOnlyAssignedScopeAsync(
            RoleIsolationHarness.TeacherRed,
            RoleIsolationHarness.ClassRed,
            RoleIsolationHarness.SubjectRed);
        await harness.AssertTeacherSeesOnlyAssignedScopeAsync(
            RoleIsolationHarness.TeacherBlue,
            RoleIsolationHarness.ClassBlue,
            RoleIsolationHarness.SubjectBlue);
    }

    [Fact]
    public async Task Removed_Assignment_Drops_Out_Of_Teacher_Active_Scope()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new RoleIsolationHarness(db);
        await harness.SeedAsync();

        var classes = new ClassGroupRepository(db);
        var teachers = new TeacherRepository(db);
        var assignmentsRepo = new TeacherAssignmentRepository(db);
        var service = new TeacherAssignmentService(classes, teachers, assignmentsRepo);

        var redAssignment = await db.TeacherAssignments
            .IgnoreQueryFilters()
            .FirstAsync(a => a.TeacherId == RoleIsolationHarness.TeacherRed);

        var removed = await service.UnassignAsync(
            RoleIsolationHarness.Tenant,
            RoleIsolationHarness.School,
            RoleIsolationHarness.ClassRed,
            redAssignment.TeacherAssignmentId,
            CancellationToken.None);
        Assert.True(removed);

        var redActiveAfter = await assignmentsRepo.ListActiveForTeacherAsync(
            RoleIsolationHarness.Tenant,
            RoleIsolationHarness.TeacherRed,
            CancellationToken.None);
        Assert.Empty(redActiveAfter);

        // Historical row is still present for audit — it just has UnassignedAt set.
        var historical = await db.TeacherAssignments
            .IgnoreQueryFilters()
            .FirstAsync(a => a.TeacherAssignmentId == redAssignment.TeacherAssignmentId);
        Assert.NotNull(historical.UnassignedAt);
    }

    [Fact]
    public async Task Class_List_For_Teacher_Does_Not_Include_Other_Teachers_Classes()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new RoleIsolationHarness(db);
        await harness.SeedAsync();

        var repo = new TeacherAssignmentRepository(db);
        var red = await repo.ListActiveForTeacherAsync(RoleIsolationHarness.Tenant, RoleIsolationHarness.TeacherRed, CancellationToken.None);
        var classIds = red.Select(a => a.ClassGroupId).ToHashSet();

        Assert.Contains(RoleIsolationHarness.ClassRed, classIds);
        Assert.DoesNotContain(RoleIsolationHarness.ClassBlue, classIds);
    }
}
