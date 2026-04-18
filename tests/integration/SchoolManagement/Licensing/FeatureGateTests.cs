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
/// T186 (US10) — integration test for feature-gate enforcement.
///
/// FeatureGateEvaluator reads the license JSON and exposes per-feature
/// enablement. The middleware refuses mutating requests that map to a
/// disabled feature with 403 / feature_gated. Missing keys fail-open
/// (unknown features stay enabled until operator explicitly disables).
/// </summary>
public class FeatureGateTests
{
    [Fact]
    public async Task FeatureGateEvaluator_Returns_True_For_Enabled_Feature()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaFeatureGates: "{\"exams\":true,\"announcements\":false}");

        var repo = new SchoolLicenseRepository(db);
        var evaluator = new FeatureGateEvaluator(repo);

        Assert.True(await evaluator.IsFeatureEnabledAsync(LicensingHarness.SchoolAlpha, "exams"));
    }

    [Fact]
    public async Task FeatureGateEvaluator_Returns_False_For_Disabled_Feature()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaFeatureGates: "{\"exams\":true,\"announcements\":false}");

        var repo = new SchoolLicenseRepository(db);
        var evaluator = new FeatureGateEvaluator(repo);

        Assert.False(await evaluator.IsFeatureEnabledAsync(LicensingHarness.SchoolAlpha, "announcements"));
    }

    [Fact]
    public async Task FeatureGateEvaluator_Returns_True_For_Missing_Feature_Key()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaFeatureGates: "{\"exams\":true}");

        var repo = new SchoolLicenseRepository(db);
        var evaluator = new FeatureGateEvaluator(repo);

        // "reports" not in JSON → fail-open.
        Assert.True(await evaluator.IsFeatureEnabledAsync(LicensingHarness.SchoolAlpha, "reports"));
    }

    [Fact]
    public async Task Middleware_Refuses_With_403_Feature_Gated_For_Disabled_Feature()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaFeatureGates: "{\"exams\":true,\"announcements\":false}");

        var resolver = new StubResolver(LicensingHarness.SchoolAlpha);
        var context = BuildContext("POST", "/api/school-admin/announcements");

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new LicensingEnforcementMiddleware(next);

        await middleware.InvokeAsync(context, resolver, db);

        Assert.False(nextCalled);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("feature_gated", body);
        Assert.Contains("announcements", body);
    }

    [Fact]
    public async Task Middleware_Allows_Enabled_Feature()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaFeatureGates: "{\"exams\":true,\"announcements\":false}");

        var resolver = new StubResolver(LicensingHarness.SchoolAlpha);
        var context = BuildContext("POST", "/api/school-admin/exams");

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new LicensingEnforcementMiddleware(next);

        await middleware.InvokeAsync(context, resolver, db);

        Assert.True(nextCalled);
    }

    [Fact]
    public void Path_Prefix_Map_Resolves_Known_Features()
    {
        Assert.Equal("exams", LicensingEnforcementMiddleware.ResolveGatedFeature("/api/school-admin/exams"));
        Assert.Equal("announcements", LicensingEnforcementMiddleware.ResolveGatedFeature("/api/school-admin/announcements/new"));
        Assert.Equal("reports", LicensingEnforcementMiddleware.ResolveGatedFeature("/api/school-admin/reports/123"));
        Assert.Equal("leaderboards", LicensingEnforcementMiddleware.ResolveGatedFeature("/api/school-admin/leaderboards"));
        Assert.Null(LicensingEnforcementMiddleware.ResolveGatedFeature("/api/school-admin/classes"));
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
