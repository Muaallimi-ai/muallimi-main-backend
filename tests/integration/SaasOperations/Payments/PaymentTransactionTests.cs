using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Api.Payments;
using Muallimi.Api.Payments.LocalPaymentStub;
using Muallimi.Api.Security.DataEncryption;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Payments;

public class PaymentTransactionTests
{
    private static IPaymentTransactionService Build()
    {
        var db = Phase6TestDbContextFactory.Create();
        var provider = new LocalPaymentStub();
        var enc = new LocalAesGcmEncryptionAdapter(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test-key")));
        var outbox = new Phase6OperationalEventOutbox(db);
        var audit = new AuditTrailWriter(db);
        return new PaymentTransactionService(db, provider, enc, outbox, audit);
    }

    [Theory]
    [InlineData(50.00, "success")]
    [InlineData(100.50, "failed")]
    [InlineData(101.50, "failed")]
    [InlineData(102.50, "pending")]
    [InlineData(103.50, "failed")]
    [InlineData(150.00, "success")]
    public async Task Charge_honours_local_stub_scenario_table(decimal amount, string expected)
    {
        var svc = Build();
        var txn = await svc.ChargeAsync(new ChargeCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount, "egp",
            "pm_test", Guid.NewGuid().ToString(), "corr-x"));

        Assert.Equal(expected, txn.Status);
    }

    [Fact]
    public async Task Charge_is_idempotent_on_repeat_key()
    {
        var svc = Build();
        var key = Guid.NewGuid().ToString();
        var a = await svc.ChargeAsync(new ChargeCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50m, "egp", "pm", key, "corr"));
        var b = await svc.ChargeAsync(new ChargeCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50m, "egp", "pm", key, "corr"));

        Assert.Equal(a.TransactionId, b.TransactionId);
    }
}
