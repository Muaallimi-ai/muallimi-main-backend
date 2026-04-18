using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolReports.ReportAggregation;

namespace Muallimi.Api.SchoolReports;

/// <summary>
/// Phase 5 — SchoolReports module marker. Subfolders (ReportAggregation,
/// ReportExport) own their endpoints, services, repositories, and background
/// services.
/// </summary>
public static class SchoolReportsModule
{
    public static IEndpointRouteBuilder MapSchoolReports(this IEndpointRouteBuilder routes)
    {
        routes.MapSchoolReportAdmin();
        return routes;
    }
}
