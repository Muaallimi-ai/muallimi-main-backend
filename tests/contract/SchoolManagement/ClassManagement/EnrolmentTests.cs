using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.ClassManagement;

/// <summary>
/// T068 (US3) — Contract test for enrolment and transfer endpoints.
///
/// Invariants:
///   • a student has at most one active enrolment per class at a time
///     (re-enrol is idempotent);
///   • transfers preserve history on the source enrolment with
///     <c>Status=transferred</c> and <c>TransferToClassId</c> set;
///   • unenrol marks <c>UnenrolledAt</c> rather than deleting.
/// </summary>
public class EnrolmentTests
{
    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/school-admin/classes/{classGroupId:guid}/enrolments", EnrolmentEndpoints.EnrolRoute);
        Assert.Equal("/api/school-admin/classes/{classGroupId:guid}/transfers", EnrolmentEndpoints.TransferRoute);
        Assert.Equal("/api/school-admin/classes/{classGroupId:guid}/enrolments/{studentId:guid}", EnrolmentEndpoints.UnenrolRoute);
    }

    [Fact]
    public async Task Enrol_Is_Idempotent_For_Same_Student()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var service = BuildService(db);
        var classRow = await service.CreateClassAsync(NewClassInput(tenantId, schoolTenantId, 7, "A"), CancellationToken.None);
        var studentId = Guid.NewGuid();

        var first = await service.EnrolStudentsAsync(tenantId, schoolTenantId, classRow.ClassGroupId, new[] { studentId }, CancellationToken.None);
        var second = await service.EnrolStudentsAsync(tenantId, schoolTenantId, classRow.ClassGroupId, new[] { studentId }, CancellationToken.None);

        Assert.Equal(1, first.EnrolledCount);
        Assert.Equal(0, second.EnrolledCount);
        Assert.Equal(1, second.AlreadyEnrolledCount);

        var activeCount = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .CountAsync(e => e.ClassGroupId == classRow.ClassGroupId && e.StudentId == studentId && e.Status == "active");
        Assert.Equal(1, activeCount);
    }

    [Fact]
    public async Task Transfer_Preserves_Source_History_And_Creates_Active_Target()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var service = BuildService(db);
        var source = await service.CreateClassAsync(NewClassInput(tenantId, schoolTenantId, 7, "A"), CancellationToken.None);
        var target = await service.CreateClassAsync(NewClassInput(tenantId, schoolTenantId, 7, "B"), CancellationToken.None);
        var studentId = Guid.NewGuid();
        await service.EnrolStudentsAsync(tenantId, schoolTenantId, source.ClassGroupId, new[] { studentId }, CancellationToken.None);

        var outcome = await service.TransferStudentAsync(tenantId, schoolTenantId, source.ClassGroupId, target.ClassGroupId, studentId, CancellationToken.None);

        Assert.True(outcome.Transferred);
        Assert.NotNull(outcome.NewEnrolmentId);

        var sourceRow = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .FirstAsync(e => e.ClassGroupId == source.ClassGroupId && e.StudentId == studentId);
        Assert.Equal("transferred", sourceRow.Status);
        Assert.Equal(target.ClassGroupId, sourceRow.TransferToClassId);
        Assert.NotNull(sourceRow.UnenrolledAt);

        var targetRow = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .FirstAsync(e => e.ClassGroupId == target.ClassGroupId && e.StudentId == studentId);
        Assert.Equal("active", targetRow.Status);
    }

    [Fact]
    public async Task Transfer_Of_Non_Enrolled_Student_Returns_Not_Transferred()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var service = BuildService(db);
        var source = await service.CreateClassAsync(NewClassInput(tenantId, schoolTenantId, 7, "A"), CancellationToken.None);
        var target = await service.CreateClassAsync(NewClassInput(tenantId, schoolTenantId, 7, "B"), CancellationToken.None);

        var outcome = await service.TransferStudentAsync(tenantId, schoolTenantId, source.ClassGroupId, target.ClassGroupId, Guid.NewGuid(), CancellationToken.None);
        Assert.False(outcome.Transferred);
    }

    [Fact]
    public async Task Unenrol_Marks_Row_Without_Deleting()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var service = BuildService(db);
        var classRow = await service.CreateClassAsync(NewClassInput(tenantId, schoolTenantId, 7, "A"), CancellationToken.None);
        var studentId = Guid.NewGuid();
        await service.EnrolStudentsAsync(tenantId, schoolTenantId, classRow.ClassGroupId, new[] { studentId }, CancellationToken.None);

        var removed = await service.UnenrolStudentAsync(tenantId, schoolTenantId, classRow.ClassGroupId, studentId, CancellationToken.None);

        Assert.True(removed);
        var row = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .FirstAsync(e => e.ClassGroupId == classRow.ClassGroupId && e.StudentId == studentId);
        Assert.Equal("unenrolled", row.Status);
        Assert.NotNull(row.UnenrolledAt);
    }

    [Fact]
    public async Task Transfer_Rejects_Same_Source_And_Target()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var service = BuildService(db);
        var classRow = await service.CreateClassAsync(NewClassInput(tenantId, schoolTenantId, 7, "A"), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.TransferStudentAsync(tenantId, schoolTenantId, classRow.ClassGroupId, classRow.ClassGroupId, Guid.NewGuid(), CancellationToken.None));
    }

    private static ClassGroupCreateInput NewClassInput(Guid tenantId, Guid schoolTenantId, int grade, string section)
        => new(
            Grade: grade,
            SectionLabel: section,
            DisplayNameAr: $"صف {grade}{section}",
            DisplayNameEn: $"Grade {grade}{section}",
            SubjectBindings: new List<string>(),
            AcademicYear: "2026-2027",
            TenantId: tenantId,
            SchoolTenantId: schoolTenantId);

    private static ClassManagementService BuildService(Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        var classes = new ClassGroupRepository(db);
        var enrolments = new ClassEnrolmentRepository(db);
        return new ClassManagementService(classes, enrolments);
    }
}
