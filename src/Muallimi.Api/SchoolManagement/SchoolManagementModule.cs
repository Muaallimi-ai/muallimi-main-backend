using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolManagement.AdminOnboarding;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.Licensing;
using Muallimi.Api.SchoolManagement.RosterImport;
using Muallimi.Api.SchoolManagement.SchoolDashboard;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.SchoolManagement.TeacherAssignment;
using Muallimi.Api.SchoolManagement.TeacherDashboard;

namespace Muallimi.Api.SchoolManagement;

/// <summary>
/// Phase 5 — SchoolManagement module marker. Subfolders
/// (SchoolTenantProvisioning, AdminOnboarding, RosterImport, ClassManagement,
/// TeacherAssignment, SchoolDashboard, TeacherDashboard, Licensing,
/// Phase4EventConsumer, DownstreamEvents) own their endpoints, services,
/// repositories, and background services. Endpoint registration is wired per
/// user story as the endpoints ship.
/// </summary>
public static class SchoolManagementModule
{
    public static IEndpointRouteBuilder MapSchoolManagement(this IEndpointRouteBuilder routes)
    {
        // US1 — operator school tenant creation + admin invite/onboarding + school config surface.
        routes.MapOperatorSchoolCreation();
        routes.MapAdminOnboarding();
        routes.MapSchoolAdminConfig();

        // US2 — roster upload, import status, error report download, student list.
        routes.MapRosterImportUpload();
        routes.MapRosterImportQueries();

        // US3 — class / enrolment / teacher assignment management.
        routes.MapClassManagement();
        routes.MapEnrolments();
        routes.MapTeacherAssignments();

        // US4 — school admin dashboard (aggregate overview + class drill-down).
        routes.MapSchoolAdminDashboard();
        routes.MapSchoolAdminClassDetail();

        // US5 — teacher dashboard (assigned classes + per-student detail).
        routes.MapTeacherDashboard();
        routes.MapTeacherDashboardDetail();

        // US10 — licensing, seat management, entitlement enforcement.
        routes.MapLicenseStatus();
        routes.MapOperatorLicenses();
        return routes;
    }
}
