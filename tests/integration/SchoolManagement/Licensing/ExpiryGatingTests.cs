using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Muallimi.Api.SchoolManagement.Licensing;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement.Licensing;

/// <summary>
/// T185 (US10) — integration test for license-expiry gating.
///
/// Expired license ⇒ mutating methods return 402 / license_expired; GET
/// methods pass through so read-only surfaces keep working. Missing license
/// follows the same read-only contract but returns license_missing.
/// </summary>
public class ExpiryGatingTests
{
    [Fact]
    public async Task Expired_License_Blocks_Post_With_402_License_Expired()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(); // Beta is seeded as already-expired.

        var resolver = new StubResolver(LicensingHarness.SchoolBeta);
        var context = BuildContext("POST", "/api/school-admin/classes");

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new LicensingEnforcementMiddleware(next);

        await middleware.InvokeAsync(context, resolver, db);

        Assert.False(nextCalled);
        Assert.Equal(402, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("license_expired", body);
    }

    [Fact]
    public async Task Expired_License_Still_Allows_Read_Only_Get()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync();

        var resolver = new StubResolver(LicensingHarness.SchoolBeta);
        var context = BuildContext("GET", "/api/school-admin/classes");

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new LicensingEnforcementMiddleware(next);

        await middleware.InvokeAsync(context, resolver, db);

        Assert.True(nextCalled);
        Assert.NotEqual(402, context.Response.StatusCode);
    }

    [Fact]
    public async Task Missing_License_Returns_402_License_Missing_For_Mutations()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        // No license rows seeded; resolver claims an unknown school.
        var unknownSchool = Guid.NewGuid();

        var resolver = new StubResolver(unknownSchool);
        var context = BuildContext("POST", "/api/school-admin/classes");

        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new LicensingEnforcementMiddleware(next);

        await middleware.InvokeAsync(context, resolver, db);

        Assert.Equal(402, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("license_missing", body);
    }

    [Fact]
    public async Task Active_License_Passes_Through_On_Mutation()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync();

        var resolver = new StubResolver(LicensingHarness.SchoolAlpha);
        var context = BuildContext("POST", "/api/school-admin/classes");

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new LicensingEnforcementMiddleware(next);

        await middleware.InvokeAsync(context, resolver, db);

        Assert.True(nextCalled);
    }

    private static DefaultHttpContext BuildContext(string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private sealed class StubResolver : ISchoolTenantResolver
    {
        private readonly Guid? _tenantId;
        public StubResolver(Guid? tenantId) => _tenantId = tenantId;
        public Task<Guid?> ResolveAsync(CancellationToken ct = default) => Task.FromResult(_tenantId);
    }
}
