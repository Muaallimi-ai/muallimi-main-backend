using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Application.Identity.Commands;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T124 — Contract test for 2FA self-service endpoints:
///   • POST /api/auth/2fa/enable
///   • POST /api/auth/2fa/verify
///   • POST /api/auth/2fa/disable
/// </summary>
public class TwoFactorContractTests
{
    [Fact]
    public void TwoFactor_Enable_Route_Is_Pinned()
    {
        Assert.Equal("/2fa/enable", AuthenticatedEndpoints.TwoFactorEnableRoute);
    }

    [Fact]
    public void TwoFactor_Verify_Route_Is_Pinned()
    {
        Assert.Equal("/2fa/verify", AuthenticatedEndpoints.TwoFactorVerifyRoute);
    }

    [Fact]
    public void TwoFactor_Disable_Route_Is_Pinned()
    {
        Assert.Equal("/2fa/disable", AuthenticatedEndpoints.TwoFactorDisableRoute);
    }

    [Fact]
    public void EnableTwoFactorCommand_Shape_Has_Required_Fields()
    {
        var props = typeof(EnableTwoFactorCommand).GetProperties();
        var names = props.Select(p => p.Name).ToArray();
        Assert.Contains("UserId", names);
        Assert.Contains("CorrelationId", names);
    }

    [Fact]
    public void VerifyTwoFactorCommand_Shape_Has_Required_Fields()
    {
        var props = typeof(VerifyTwoFactorCommand).GetProperties();
        var names = props.Select(p => p.Name).ToArray();
        Assert.Contains("UserId", names);
        Assert.Contains("Code", names);
        Assert.Contains("CorrelationId", names);
    }

    [Fact]
    public void DisableTwoFactorCommand_Shape_Has_Required_Fields()
    {
        var props = typeof(DisableTwoFactorCommand).GetProperties();
        var names = props.Select(p => p.Name).ToArray();
        Assert.Contains("UserId", names);
        Assert.Contains("CurrentPassword", names);
        Assert.Contains("CorrelationId", names);
    }
}
