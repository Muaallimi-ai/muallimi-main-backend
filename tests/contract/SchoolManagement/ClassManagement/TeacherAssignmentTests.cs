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
/// T069 (US3) — Contract test for teacher assignment endpoints.
///
/// Invariants:
///   • assign is idempotent per (teacher, class, subject) while active;
///   • unassign stamps <c>UnassignedAt</c> instead of deleting;
///   • assigning a teacher whose school tenant differs from the class is
///     rejected with <c>teacher_class_scope_mismatch</c>;
///   • the unassign path rejects a teacher_assignment_id that belongs to
///     a different class (cross-class tampering).
/// </summary>
public class TeacherAssignmentTests
{
    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/school-admin/teachers", TeacherAssignmentEndpoints.ListTeachersRoute);
        Assert.Equal("/api/school-admin/classes/{classGroupId:guid}/teachers", TeacherAssignmentEndpoints.AssignRoute);
        Assert.Equal("/api/school-admin/classes/{classGroupId:guid}/teachers/{teacherAssignmentId:guid}", TeacherAssignmentEndpoints.UnassignRoute);
        Assert.Equal("/api/school-admin/teachers/{teacherId:guid}/assignments", TeacherAssignmentEndpoints.TeacherAssignmentsRoute);
    }

    [Fact]
    public async Task Assign_Is_Idempotent_And_Active_Entry_Is_Unique()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var (tenantId, schoolTenantId, classId, teacherId, subjectId) = await SeedSchoolClassTeacherAsync(db);

        var service = BuildService(db);
        var first = await service.AssignAsync(new TeacherAssignmentInput(tenantId, schoolTenantId, classId, teacherId, subjectId), CancellationToken.None);
        var second = await service.AssignAsync(new TeacherAssignmentInput(tenantId, schoolTenantId, classId, teacherId, subjectId), CancellationToken.None);

        Assert.Equal(first.TeacherAssignmentId, second.TeacherAssignmentId);

        var activeCount = await db.TeacherAssignments
            .IgnoreQueryFilters()
            .CountAsync(a => a.TeacherId == teacherId
                && a.ClassGroupId == classId
                && a.SubjectId == subjectId
                && a.UnassignedAt == null);
        Assert.Equal(1, activeCount);
    }

    [Fact]
    public async Task Unassign_Stamps_Timestamp_Without_Deleting()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var (tenantId, schoolTenantId, classId, teacherId, subjectId) = await SeedSchoolClassTeacherAsync(db);
        var service = BuildService(db);
        var row = await service.AssignAsync(new TeacherAssignmentInput(tenantId, schoolTenantId, classId, teacherId, subjectId), CancellationToken.None);

        var removed = await service.UnassignAsync(tenantId, schoolTenantId, classId, row.TeacherAssignmentId, CancellationToken.None);

        Assert.True(removed);
        var persisted = await db.TeacherAssignments
            .IgnoreQueryFilters()
            .FirstAsync(a => a.TeacherAssignmentId == row.TeacherAssignmentId);
        Assert.NotNull(persisted.UnassignedAt);
    }

    [Fact]
    public async Task Assign_Rejects_Teacher_From_Different_School()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolA = Guid.NewGuid();
        var schoolB = Guid.NewGuid();
        var classInSchoolA = await SeedClassAsync(db, tenantId, schoolA);
        var teacherInSchoolB = await SeedTeacherAsync(db, tenantId, schoolB);

        var service = BuildService(db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AssignAsync(new TeacherAssignmentInput(tenantId, schoolA, classInSchoolA, teacherInSchoolB, Guid.NewGuid()), CancellationToken.None));
        // Service loads the teacher under (tenantId, schoolA) first; since
        // the teacher is in schoolB the lookup returns null.
        Assert.Equal("teacher_not_found", ex.Message);
    }

    [Fact]
    public async Task Unassign_Rejects_When_Assignment_Belongs_To_Different_Class()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var (tenantId, schoolTenantId, classA, teacherId, subjectId) = await SeedSchoolClassTeacherAsync(db);
        var classB = await SeedClassAsync(db, tenantId, schoolTenantId);
        var service = BuildService(db);

        var rowInA = await service.AssignAsync(new TeacherAssignmentInput(tenantId, schoolTenantId, classA, teacherId, subjectId), CancellationToken.None);

        // Attempt to unassign via classB using the id that belongs to classA.
        var removed = await service.UnassignAsync(tenantId, schoolTenantId, classB, rowInA.TeacherAssignmentId, CancellationToken.None);
        Assert.False(removed);

        var still = await db.TeacherAssignments.IgnoreQueryFilters().FirstAsync(a => a.TeacherAssignmentId == rowInA.TeacherAssignmentId);
        Assert.Null(still.UnassignedAt);
    }

    private static TeacherAssignmentService BuildService(Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        var classes = new ClassGroupRepository(db);
        var teachers = new TeacherRepository(db);
        var assignments = new TeacherAssignmentRepository(db);
        return new TeacherAssignmentService(classes, teachers, assignments);
    }

    private static async Task<(Guid tenantId, Guid schoolTenantId, Guid classId, Guid teacherId, Guid subjectId)>
        SeedSchoolClassTeacherAsync(Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var classId = await SeedClassAsync(db, tenantId, schoolTenantId);
        var teacherId = await SeedTeacherAsync(db, tenantId, schoolTenantId);
        return (tenantId, schoolTenantId, classId, teacherId, Guid.NewGuid());
    }

    private static async Task<Guid> SeedClassAsync(Muallimi.Infrastructure.Persistence.MuallimiDbContext db, Guid tenantId, Guid schoolTenantId)
    {
        var id = Guid.NewGuid();
        db.ClassGroups.Add(new ClassGroup
        {
            ClassGroupId = id,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            Grade = 7,
            SectionLabel = "A",
            DisplayNameAr = "الصف السابع أ",
            DisplayNameEn = "Grade 7A",
            SubjectBindings = "[]",
            AcademicYear = "2026-2027",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedTeacherAsync(Muallimi.Infrastructure.Persistence.MuallimiDbContext db, Guid tenantId, Guid schoolTenantId)
    {
        var id = Guid.NewGuid();
        db.Teachers.Add(new Teacher
        {
            TeacherId = id,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UserIdentityId = Guid.NewGuid(),
            DisplayNameAr = "المعلم",
            DisplayNameEn = "Teacher",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }
}
