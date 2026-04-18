using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Api.Payments;
using Muallimi.Api.Payments.Idempotency;
using Muallimi.Api.Payments.LocalPaymentStub;
using Muallimi.Api.Payments.PaymentProviderAdapter;
using Muallimi.Api.Payments.RetryPolicy;
using Muallimi.Api.Payments.WebhookProcessing;
using Muallimi.Api.Security.DataEncryption;
using Muallimi.Domain.SaasOperations;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Payments;

/// <summary>
/// Phase 6 US7 — Coverage for T109 (payment method management), T110 (webhook
/// signature validation registry), T111 (24h idempotency dedup), T112 (retry
/// ladder 30s/5m/30m), and T113 (deterministic provider_reference on the stub).
/// </summary>
public class PaymentProviderExtensionsTests
{
    // ── T109: PaymentMethodManagementService ──

    [Fact]
    public async Task Add_then_list_returns_stored_method_with_masked_identifier()
    {
        var db = Phase6TestDbContextFactory.Create();
        var stub = new LocalPaymentStub();
        var audit = new AuditTrailWriter(db);
        var svc = new PaymentMethodManagementService(stub, audit);

        var tenantId = Guid.NewGuid();
        var added = await svc.AddAsync(tenantId, "card", "tok_4242", "corr-1");

        Assert.StartsWith("pm_", added.Ref);
        Assert.EndsWith("4242", added.MaskedIdentifier);

        var listed = await svc.ListAsync(tenantId);
        Assert.Single(listed);
        Assert.Equal(added.Ref, listed[0].Ref);
    }

    [Fact]
    public async Task Remove_detaches_method_idempotently()
    {
        var db = Phase6TestDbContextFactory.Create();
        var svc = new PaymentMethodManagementService(new LocalPaymentStub(), new AuditTrailWriter(db));
        var tenantId = Guid.NewGuid();
        var added = await svc.AddAsync(tenantId, "card", "tok_demo", "corr");

        await svc.RemoveAsync(tenantId, added.Ref, "corr");
        await svc.RemoveAsync(tenantId, added.Ref, "corr"); // second remove must not throw

        Assert.Empty(await svc.ListAsync(tenantId));
    }

    [Fact]
    public async Task Add_requires_non_empty_provider_token()
    {
        var db = Phase6TestDbContextFactory.Create();
        var svc = new PaymentMethodManagementService(new LocalPaymentStub(), new AuditTrailWriter(db));
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.AddAsync(Guid.NewGuid(), "card", "  ", "corr"));
    }

    // ── T110: Signature validator registry ──

    [Fact]
    public void Hmac_validator_accepts_matching_signature_and_rejects_mismatch()
    {
        var v = new LocalStubHmacSignatureValidator();
        var secret = "shared-secret";
        var body = "{\"event\":\"payment_succeeded\"}";
        var valid = System.Convert.ToHexString(
            new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret))
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var ok = new Dictionary<string, string> { ["X-Provider-Signature"] = valid };
        var bad = new Dictionary<string, string> { ["X-Provider-Signature"] = new string('0', 64) };

        Assert.True(v.Validate(body, ok, secret));
        Assert.False(v.Validate(body, bad, secret));
    }

    [Fact]
    public void Timestamped_hmac_rejects_outside_tolerance_window()
    {
        var v = new TimestampedHmacSignatureValidator("stripe_like", "Stripe-Signature", toleranceSeconds: 60);
        var secret = "whsec_test";
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
        var body = "{}";
        var sig = System.Convert.ToHexString(
            new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret))
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(stale + "." + body))).ToLowerInvariant();
        var headers = new Dictionary<string, string> { ["Stripe-Signature"] = $"t={stale},v1={sig}" };

        Assert.False(v.Validate(body, headers, secret));
    }

    [Fact]
    public void Registry_resolves_by_provider_name_case_insensitively()
    {
        var registry = new WebhookSignatureValidatorRegistry(new IWebhookSignatureValidator[]
        {
            new LocalStubHmacSignatureValidator(),
        });

        Assert.NotNull(registry.ResolveFor("local_stub"));
        Assert.NotNull(registry.ResolveFor("LOCAL_STUB"));
        Assert.Null(registry.ResolveFor("unknown_provider"));
    }

    // ── T111: 24h idempotency window ──

    [Fact]
    public async Task Idempotency_service_flags_recent_completion_as_duplicate()
    {
        var db = Phase6TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            TransactionId = Guid.NewGuid(),
            InvoiceId = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderName = "local_stub",
            ProviderReference = "ls_abc",
            Amount = 50m,
            Currency = "egp",
            TransactionType = "charge",
            Status = "success",
            IdempotencyKey = Guid.NewGuid().ToString(),
            CorrelationId = "c",
            AttemptedAt = DateTime.UtcNow.AddHours(-2),
            CompletedAt = DateTime.UtcNow.AddHours(-2),
        });
        await db.SaveChangesAsync();
        var svc = new PaymentIdempotencyService(db);

        Assert.True(await svc.IsDuplicateWebhookAsync("ls_abc", "charge"));
        Assert.False(await svc.IsDuplicateWebhookAsync("ls_abc", "refund"));
        Assert.False(await svc.IsDuplicateWebhookAsync("ls_other", "charge"));
    }

    [Fact]
    public async Task Idempotency_service_ignores_completions_older_than_24h()
    {
        var db = Phase6TestDbContextFactory.Create();
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            TransactionId = Guid.NewGuid(),
            InvoiceId = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ProviderName = "local_stub",
            ProviderReference = "ls_old",
            Amount = 50m,
            Currency = "egp",
            TransactionType = "charge",
            Status = "success",
            IdempotencyKey = Guid.NewGuid().ToString(),
            CorrelationId = "c",
            AttemptedAt = DateTime.UtcNow.AddHours(-48),
            CompletedAt = DateTime.UtcNow.AddHours(-48),
        });
        await db.SaveChangesAsync();
        var svc = new PaymentIdempotencyService(db);

        Assert.False(await svc.IsDuplicateWebhookAsync("ls_old", "charge"));
    }

    // ── T112: Retry ladder ──

    [Fact]
    public void Retry_scheduler_uses_30s_5m_30m_ladder()
    {
        var opts = Options.Create(new PaymentRetryOptions());
        var sched = new PaymentRetryScheduler(opts);
        Assert.Equal(3, sched.MaxAttempts);

        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        sched.Schedule(id, 1, now);
        var first = sched.Snapshot().Single();
        Assert.Equal(now + TimeSpan.FromSeconds(30), first.NotBefore);

        sched.Schedule(id, 2, now);
        Assert.Equal(now + TimeSpan.FromMinutes(5), sched.Snapshot().Single().NotBefore);

        sched.Schedule(id, 3, now);
        Assert.Equal(now + TimeSpan.FromMinutes(30), sched.Snapshot().Single().NotBefore);
    }

    [Fact]
    public void Retry_scheduler_rejects_attempt_beyond_ladder()
    {
        var opts = Options.Create(new PaymentRetryOptions());
        var sched = new PaymentRetryScheduler(opts);
        Assert.False(sched.Schedule(Guid.NewGuid(), 0));
        Assert.False(sched.Schedule(Guid.NewGuid(), 4));
    }

    [Fact]
    public void Retry_scheduler_only_claims_items_whose_NotBefore_has_passed()
    {
        var sched = new PaymentRetryScheduler(Options.Create(new PaymentRetryOptions()));
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        sched.Schedule(id, 1, now);

        Assert.False(sched.TryClaim(id, now, out _));           // not yet
        Assert.True(sched.TryClaim(id, now.AddSeconds(31), out var attempt));
        Assert.Equal(1, attempt);
        Assert.False(sched.TryClaim(id, now.AddMinutes(1), out _)); // claim removes entry
    }

    // ── T113: Deterministic provider_reference on local stub ──

    [Fact]
    public async Task LocalPaymentStub_returns_deterministic_provider_reference_for_same_idempotency_key()
    {
        var stub = new LocalPaymentStub();
        var req = new ChargeRequest(Guid.NewGuid(), Guid.NewGuid(), 50m, "egp", "pm", "idem-key-xyz", "corr");
        var a = await stub.ChargeAsync(req);
        var b = await stub.ChargeAsync(req);
        Assert.Equal(a.ProviderReference, b.ProviderReference);
        Assert.StartsWith("ls_", a.ProviderReference);
    }

    [Fact]
    public async Task LocalPaymentStub_refund_reference_is_distinct_from_charge()
    {
        var stub = new LocalPaymentStub();
        var key = "idem-key-abc";
        var charge = await stub.ChargeAsync(new ChargeRequest(Guid.NewGuid(), Guid.NewGuid(), 50m, "egp", "pm", key, "corr"));
        var refund = await stub.RefundAsync(new RefundRequest(charge.ProviderReference!, 50m, "egp", key, "corr"));
        Assert.NotEqual(charge.ProviderReference, refund.ProviderReference);
        Assert.StartsWith("ls_", refund.ProviderReference);
    }

    // ── End-to-end: network_timeout failure enters retry queue ──

    [Fact]
    public async Task Charge_with_network_timeout_schedules_first_retry_at_30s()
    {
        var db = Phase6TestDbContextFactory.Create();
        var stub = new LocalPaymentStub();
        var enc = new LocalAesGcmEncryptionAdapter(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test-key")));
        var outbox = new Phase6OperationalEventOutbox(db);
        var audit = new AuditTrailWriter(db);
        var sched = new PaymentRetryScheduler(Options.Create(new PaymentRetryOptions()));
        var svc = new PaymentTransactionService(db, stub, enc, outbox, audit, notifications: null, retryScheduler: sched);

        var before = DateTime.UtcNow;
        var txn = await svc.ChargeAsync(new ChargeCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 103.50m, "egp",
            "pm", Guid.NewGuid().ToString(), "corr"));
        Assert.Equal("failed", txn.Status);
        Assert.Equal("network_timeout", txn.FailureCode);

        var pending = sched.Snapshot().Single();
        Assert.Equal(txn.TransactionId, pending.TransactionId);
        Assert.InRange(pending.NotBefore, before + TimeSpan.FromSeconds(29), before + TimeSpan.FromSeconds(31));
    }

    [Fact]
    public async Task Charge_with_permanent_failure_does_not_enter_retry_queue()
    {
        var db = Phase6TestDbContextFactory.Create();
        var stub = new LocalPaymentStub();
        var enc = new LocalAesGcmEncryptionAdapter(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test-key")));
        var outbox = new Phase6OperationalEventOutbox(db);
        var audit = new AuditTrailWriter(db);
        var sched = new PaymentRetryScheduler(Options.Create(new PaymentRetryOptions()));
        var svc = new PaymentTransactionService(db, stub, enc, outbox, audit, notifications: null, retryScheduler: sched);

        await svc.ChargeAsync(new ChargeCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100.50m, "egp",
            "pm", Guid.NewGuid().ToString(), "corr"));

        Assert.Empty(sched.Snapshot());
    }
}
