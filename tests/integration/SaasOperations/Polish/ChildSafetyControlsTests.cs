using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Muallimi.Api.Security.ChildSafetyControls;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Polish;

/// <summary>
/// T137 (Polish) — Child-safety controls enforcement.
///
/// Invariants (per the Phase 6 constitutional rules + Egypt child-data
/// guardrails):
///   1. A student (child) actor CANNOT bind an external channel
///      (email/push/whatsapp) without parental consent.
///   2. Parental consent must be explicit — absent-or-blank is NOT consent.
///   3. Non-child actors (parents, school admins, operators) are not
///      affected by this middleware — their bindings proceed normally.
///   4. GET/read operations are always allowed regardless of actor role —
///      the policy applies only to mutating requests.
/// </summary>
public class ChildSafetyControlsTests
{
    private static async Task<(int status, bool nextCalled)> RunAsync(
        HttpContext ctx)
    {
        var nextCalled = false;
        var middleware = new ChildSafetyControlsMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        ctx.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(ctx);
        return (ctx.Response.StatusCode, nextCalled);
    }

    private static HttpContext MakeRequest(
        string method,
        string path,
        string? actorType = null,
        string? consent = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (actorType is not null) ctx.Request.Headers["X-Actor-Type"] = actorType;
        if (consent is not null) ctx.Request.Headers["X-Parental-Consent"] = consent;
        return ctx;
    }

    [Theory]
    [InlineData("/api/v1/notifications/bindings")]
    [InlineData("/api/v1/notifications/email")]
    [InlineData("/api/v1/notifications/push")]
    [InlineData("/api/v1/notifications/whatsapp")]
    [InlineData("/api/v1/notifications/subscribe")]
    public async Task Child_actor_is_blocked_from_external_channel_bindings_without_consent(string path)
    {
        var ctx = MakeRequest("POST", path, actorType: "student");

        var (status, nextCalled) = await RunAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Child_actor_with_granted_parental_consent_is_allowed()
    {
        var ctx = MakeRequest(
            "POST",
            "/api/v1/notifications/bindings",
            actorType: "student",
            consent: "granted");

        var (status, nextCalled) = await RunAsync(ctx);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task Child_actor_with_blank_consent_header_is_blocked()
    {
        var ctx = MakeRequest(
            "POST",
            "/api/v1/notifications/push",
            actorType: "student",
            consent: ""); // explicit blank

        var (status, nextCalled) = await RunAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("school_admin")]
    [InlineData("operator")]
    public async Task Non_child_actors_are_unaffected(string actorType)
    {
        var ctx = MakeRequest(
            "POST",
            "/api/v1/notifications/bindings",
            actorType: actorType);

        var (_, nextCalled) = await RunAsync(ctx);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Child_actor_can_read_notifications_without_consent()
    {
        var ctx = MakeRequest(
            "GET",
            "/api/v1/notifications/bindings",
            actorType: "student");

        var (_, nextCalled) = await RunAsync(ctx);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Non_external_channel_path_is_unaffected_even_for_children()
    {
        // The policy is scoped strictly to external-channel binding paths.
        // A child writing to /api/v1/student/... should not trip the filter.
        var ctx = MakeRequest(
            "POST",
            "/api/v1/student/homework/attempt",
            actorType: "student");

        var (_, nextCalled) = await RunAsync(ctx);

        Assert.True(nextCalled);
    }
}
