using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Application.Identity.Commands;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T123 — Contract test for session self-service endpoints:
///   • GET    /api/auth/sessions
///   • DELETE /api/auth/sessions/{id}
///   • DELETE /api/auth/sessions
/// </summary>
public class SessionsContractTests
{
    [Fact]
    public void Sessions_List_Route_Is_Pinned()
    {
        Assert.Equal("/sessions", AuthenticatedEndpoints.SessionsRoute);
    }

    [Fact]
    public void Sessions_Revoke_Single_Route_Is_Pinned()
    {
        Assert.Equal("/sessions/{id:guid}", AuthenticatedEndpoints.RevokeSessionRoute);
    }

    [Fact]
    public void Sessions_Revoke_All_Route_Is_Pinned()
    {
        Assert.Equal("/sessions", AuthenticatedEndpoints.RevokeAllSessionsRoute);
    }

    [Fact]
    public void ListSessionsQuery_Shape_Has_Required_Fields()
    {
        var props = typeof(ListSessionsQuery).GetProperties();
        var names = props.Select(p => p.Name).ToArray();
        Assert.Contains("UserId", names);
    }

    [Fact]
    public void RevokeSessionCommand_Shape_Has_Required_Fields()
    {
        var props = typeof(RevokeSessionCommand).GetProperties();
        var names = props.Select(p => p.Name).ToArray();
        Assert.Contains("UserId", names);
        Assert.Contains("TargetSessionId", names);
        Assert.Contains("CorrelationId", names);
    }
}
