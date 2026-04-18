using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Muallimi.Api.Security.PIIMasking;

/// <summary>
/// T018 — Serilog enricher that masks PII in log property values. Applies to
/// user_id, email, phone, ip_address, payment_token, student_name per
/// observability-contract.md.
/// </summary>
public class PIIMaskingEnricher : ILogEventEnricher
{
    private static readonly HashSet<string> SensitiveProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Email", "email", "Phone", "phone", "IpAddress", "ip_address",
        "PaymentToken", "payment_token", "StudentName", "student_name",
        "ParentEmail", "parent_email", "ParentPhone", "parent_phone"
    };

    private static readonly Regex EmailRegex = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(@"\+?\d[\d\- ]{7,}", RegexOptions.Compiled);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var key in logEvent.Properties.Keys.ToList())
        {
            if (SensitiveProps.Contains(key))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, "***"));
            }
            else if (logEvent.Properties[key] is ScalarValue { Value: string s })
            {
                var masked = MaskFreeText(s);
                if (!ReferenceEquals(masked, s))
                {
                    logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, masked));
                }
            }
        }
    }

    internal static string MaskFreeText(string input)
    {
        var masked = EmailRegex.Replace(input, "***@***");
        masked = PhoneRegex.Replace(masked, "***");
        return masked;
    }
}
