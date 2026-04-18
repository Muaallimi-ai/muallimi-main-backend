using System;
using System.Linq;
using Muallimi.Api.Security.PIIMasking;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Polish;

/// <summary>
/// T129 (Polish) — Verify PIIMaskingEnricher masks the full set of sensitive
/// properties declared in observability-contract.md: user_id, email, phone,
/// ip_address, payment_token, student_name, parent_email, parent_phone.
///
/// Complements the main-backend enricher — the ai-service and
/// document-ingestion repos each ship their own <c>StructuredLoggingEnricher</c>
/// covering the same sensitive property set. Any regression that adds a new
/// PII property without masking will surface here and the production logs
/// will leak.
/// </summary>
public class PiiMaskingEnricherTests
{
    private static LogEvent MakeEvent(params (string key, string value)[] properties)
    {
        var props = properties.Select(p =>
            new LogEventProperty(p.key, new ScalarValue(p.value))).ToList();
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplate("test", Enumerable.Empty<MessageTemplateToken>()),
            properties: props);
    }

    private sealed class CapturingFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new LogEventProperty(name, new ScalarValue(value));
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("email")]
    [InlineData("Phone")]
    [InlineData("phone")]
    [InlineData("IpAddress")]
    [InlineData("ip_address")]
    [InlineData("PaymentToken")]
    [InlineData("payment_token")]
    [InlineData("StudentName")]
    [InlineData("student_name")]
    [InlineData("ParentEmail")]
    [InlineData("parent_email")]
    [InlineData("ParentPhone")]
    [InlineData("parent_phone")]
    public void Sensitive_property_is_masked_to_three_stars(string key)
    {
        var evt = MakeEvent((key, "real-sensitive-value"));
        var enricher = new PIIMaskingEnricher();

        enricher.Enrich(evt, new CapturingFactory());

        var rendered = (evt.Properties[key] as ScalarValue)!.Value!.ToString();
        Assert.Equal("***", rendered);
    }

    [Fact]
    public void Non_sensitive_property_is_not_masked_when_value_is_non_pii()
    {
        // A short, non-PII scalar like "tenant_A" must pass through
        // unchanged — the masker is a PII filter, not an opaque redactor.
        var evt = MakeEvent(("tenant_name", "tenant_A"));
        var enricher = new PIIMaskingEnricher();

        enricher.Enrich(evt, new CapturingFactory());

        var rendered = (evt.Properties["tenant_name"] as ScalarValue)!.Value!.ToString();
        Assert.Equal("tenant_A", rendered);
    }

    [Fact]
    public void Free_text_email_inside_other_property_is_masked()
    {
        // Parents sometimes arrive in log messages as free-text — e.g. a
        // caught exception's Message field. Mask those too so Seq never
        // renders an address in plaintext.
        var evt = MakeEvent(("Message", "failed to notify parent at parent@example.com"));
        var enricher = new PIIMaskingEnricher();

        enricher.Enrich(evt, new CapturingFactory());

        var rendered = (evt.Properties["Message"] as ScalarValue)!.Value!.ToString()!;
        Assert.DoesNotContain("parent@example.com", rendered);
        Assert.Contains("***@***", rendered);
    }

    [Fact]
    public void Free_text_phone_inside_other_property_is_masked()
    {
        var evt = MakeEvent(("Message", "sms retry to +201012345678 failed"));
        var enricher = new PIIMaskingEnricher();

        enricher.Enrich(evt, new CapturingFactory());

        var rendered = (evt.Properties["Message"] as ScalarValue)!.Value!.ToString()!;
        Assert.DoesNotContain("+201012345678", rendered);
        Assert.Contains("***", rendered);
    }

    [Fact]
    public void Free_text_masks_both_email_and_phone_patterns_inside_a_single_property()
    {
        var evt = MakeEvent(("Description", "Contact: a.b_c@example.co.uk or +1 555-444-3333"));
        var enricher = new PIIMaskingEnricher();

        enricher.Enrich(evt, new CapturingFactory());

        var rendered = (evt.Properties["Description"] as ScalarValue)!.Value!.ToString()!;
        Assert.DoesNotContain("a.b_c@example.co.uk", rendered);
        Assert.DoesNotContain("+1 555-444-3333", rendered);
    }
}
