using Muallimi.Api.SchoolManagement.RosterImport;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.RosterImport;

/// <summary>
/// T048 (US2) — Contract test for GET <c>/school-admin/roster/imports/{id}</c>
/// and GET <c>/school-admin/roster/imports/{id}/errors</c>.
///
/// Pins the route constants so the frontend polling loop and the error
/// report download link can bind with confidence.
/// </summary>
public class ImportStatusTests
{
    [Fact]
    public void Status_Route_Is_Pinned()
    {
        Assert.Equal(
            "/api/school-admin/roster/imports/{rosterImportId:guid}",
            RosterQueryEndpoints.StatusRoute);
    }

    [Fact]
    public void Errors_Route_Is_Pinned()
    {
        Assert.Equal(
            "/api/school-admin/roster/imports/{rosterImportId:guid}/errors",
            RosterQueryEndpoints.ErrorsRoute);
    }
}
