using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.Compliance.DataDeletion;
using Muallimi.Api.Compliance.DataExport;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Api.Security.ChildSafetyControls;
using Muallimi.Api.Security.DataEncryption;
using Muallimi.Api.Security.TransportSecurity;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Security;

/// <summary>
/// T088–T099 (US5) — Security hardening + data protection integration coverage.
/// Exercises the middleware stack, deletion pipeline, export archive, and
/// register contract shape end-to-end against the in-memory Phase 6 fixtures.
/// </summary>
public class SecurityAndDataProtectionTests
{
    [Fact]
    public async Task TransportSecurity_sets_baseline_response_headers()
    {
        var middleware = new TransportSecurityMiddleware(_ => Task.CompletedTask);
        var ctx = new DefaultHttpContext();

        await middleware.InvokeAsync(ctx);

        Assert.Equal("nosniff", ctx.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", ctx.Response.Headers["X-Frame-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", ctx.Response.Headers["Referrer-Policy"]);
    }

    [Fact]
    public async Task ChildSafety_blocks_child_external_channel_without_consent()
    {
        var invoked = false;
        var middleware = new ChildSafetyControlsMiddleware(_ => { invoked = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/api/v1/notifications/bindings";
        ctx.Request.Headers["X-Actor-Type"] = "student";
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ChildSafety_allows_non_child_actors()
    {
        var invoked = false;
        var middleware = new ChildSafetyControlsMiddleware(_ => { invoked = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/api/v1/notifications/bindings";
        ctx.Request.Headers["X-Actor-Type"] = "parent";

        await middleware.InvokeAsync(ctx);

        Assert.True(invoked);
    }

    [Fact]
    public void ColumnEncryption_round_trips_plaintext_through_adapter()
    {
        var original = ColumnEncryption.Encrypt;
        var originalDecrypt = ColumnEncryption.Decrypt;
        try
        {
            var adapter = LocalAesGcmEncryptionAdapter.FromConfiguration(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            ColumnEncryption.Encrypt = s => adapter.Encrypt(s);
            ColumnEncryption.Decrypt = s => adapter.Decrypt(s);

            var ciphertext = ColumnEncryption.EncryptValue("pm_test_abc");
            Assert.StartsWith(ColumnEncryption.CipherPrefix, ciphertext);
            var plaintext = ColumnEncryption.DecryptValue(ciphertext);
            Assert.Equal("pm_test_abc", plaintext);
        }
        finally
        {
            ColumnEncryption.Encrypt = original;
            ColumnEncryption.Decrypt = originalDecrypt;
        }
    }

    [Fact]
    public void ColumnEncryption_passthrough_for_legacy_plaintext()
    {
        var legacy = "pm_legacy";
        Assert.Equal(legacy, ColumnEncryption.DecryptValue(legacy));
    }

    [Fact]
    public async Task DataExportService_generates_zip_archive_and_writes_audit()
    {
        var db = Phase6TestDbContextFactory.Create();
        var audit = new AuditTrailWriter(db);
        var service = new DataExportService(db, audit, NullLogger<DataExportService>.Instance);

        var tenantId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var archive = await service.GenerateAsync(tenantId, "tenant", targetId, Guid.NewGuid(), "corr-export-1");

        Assert.True(archive.ZipBytes.Length > 0);
        Assert.Equal("application/zip", archive.ContentType);
        Assert.Contains("manifest.json", archive.Entries);
        Assert.Contains("subscriptions.json", archive.Entries);
        Assert.Single(db.AuditEntries, a => a.ActionType == "export_request");
    }

    [Fact]
    public async Task DataDeletionService_completes_pipeline_and_emits_event_and_audit()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MuallimiDbContext>(sp => Phase6TestDbContextFactory.Create("deletion-run"));
        services.AddSingleton<AuditTrailWriter>();
        services.AddSingleton<Phase6OperationalEventOutbox>();
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var options = Options.Create(new DataDeletionServiceOptions { EnableBackgroundLoop = false });
        var service = new DataDeletionService(sp, NullLogger<DataDeletionService>.Instance, options);

        var tenantId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var request = await service.CreateAsync(tenantId, "student", targetId, Guid.NewGuid(), "corr-del-1");
        Assert.Equal("pending", request.Status);

        var processed = await service.ProcessAsync(request.DeletionRequestId);
        Assert.NotNull(processed);
        Assert.Equal("completed", processed!.Status);
        Assert.NotNull(processed.CompletedAt);
        Assert.NotNull(processed.TablesProcessed);

        var db = sp.GetRequiredService<MuallimiDbContext>();
        Assert.Contains(db.AuditEntries, a => a.ActionType == "data_delete");
        Assert.Contains(db.Phase6OperationalEvents, e => e.EventKind == "data_deletion_completed");
    }

    [Fact]
    public void DataProcessingRegister_shape_matches_contract()
    {
        var register = Muallimi.Api.Compliance.DataProcessingRegister.DataProcessingRegister.GetRegister();
        var json = System.Text.Json.JsonSerializer.Serialize(register);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("categories", out var categories));
        Assert.True(categories.GetArrayLength() >= 3);
        foreach (var cat in categories.EnumerateArray())
        {
            Assert.True(cat.TryGetProperty("category", out _));
            Assert.True(cat.TryGetProperty("data_fields", out var fields));
            foreach (var field in fields.EnumerateArray())
            {
                Assert.True(field.TryGetProperty("field", out _));
                Assert.True(field.TryGetProperty("legal_basis", out _));
                Assert.True(field.TryGetProperty("retention_days", out _));
                Assert.True(field.TryGetProperty("shared_with", out _));
            }
        }
    }
}
