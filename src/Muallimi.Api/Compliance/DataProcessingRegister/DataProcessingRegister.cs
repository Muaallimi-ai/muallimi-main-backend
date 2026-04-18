namespace Muallimi.Api.Compliance.DataProcessingRegister;

/// <summary>
/// T095 — Static data processing register per security-data-protection-contract.md.
/// Documents every personal data category we collect, its purpose, legal basis,
/// retention period, and third-party sharing. Exposed via GET
/// /api/v1/compliance/data-register and consumed by the operator compliance UI.
/// </summary>
public static class DataProcessingRegister
{
    public static object GetRegister() => new
    {
        schema_version = "1.0.0",
        generated_at = DateTime.UtcNow,
        categories = new object[]
        {
            new
            {
                category = "Student Learning Activity",
                data_fields = new object[]
                {
                    new
                    {
                        field = "student_profile.display_name",
                        purpose = "Personalise greetings and UI",
                        legal_basis = "parental_consent",
                        retention_days = 365,
                        shared_with = Array.Empty<object>(),
                    },
                    new
                    {
                        field = "session_event",
                        purpose = "Adaptive learning, progress reporting",
                        legal_basis = "parental_consent",
                        retention_days = 365,
                        shared_with = Array.Empty<object>(),
                    },
                    new
                    {
                        field = "ai_operations_metric.correlation_id",
                        purpose = "AI service auditability and cost attribution",
                        legal_basis = "legitimate_interest",
                        retention_days = 180,
                        shared_with = new object[]
                        {
                            new
                            {
                                third_party = "LLM provider adapter",
                                purpose = "Generate tutor responses",
                                fields_shared = new[] { "prompt_body", "prompt_key" },
                            },
                        },
                    },
                },
            },
            new
            {
                category = "Parent Contact & Engagement",
                data_fields = new object[]
                {
                    new
                    {
                        field = "parent_profile.notification_channels",
                        purpose = "Deliver parent notifications",
                        legal_basis = "contract",
                        retention_days = 1095,
                        shared_with = new object[]
                        {
                            new
                            {
                                third_party = "Email / Push / WhatsApp provider",
                                purpose = "Channel delivery",
                                fields_shared = new[] { "recipient_email", "recipient_phone" },
                            },
                        },
                    },
                    new
                    {
                        field = "weekly_report",
                        purpose = "Parent-facing weekly summary",
                        legal_basis = "contract",
                        retention_days = 365,
                        shared_with = Array.Empty<object>(),
                    },
                },
            },
            new
            {
                category = "Billing & Subscription",
                data_fields = new object[]
                {
                    new
                    {
                        field = "subscription.payment_method_ref",
                        purpose = "Recurring charge authorisation",
                        legal_basis = "contract",
                        retention_days = 2555,
                        shared_with = new object[]
                        {
                            new
                            {
                                third_party = "Payment provider adapter",
                                purpose = "Charge and refund processing",
                                fields_shared = new[] { "payment_method_ref" },
                            },
                        },
                    },
                    new
                    {
                        field = "invoice",
                        purpose = "Statutory accounting record",
                        legal_basis = "legal_obligation",
                        retention_days = 2555,
                        shared_with = Array.Empty<object>(),
                    },
                },
            },
            new
            {
                category = "School B2B",
                data_fields = new object[]
                {
                    new
                    {
                        field = "school_tenant",
                        purpose = "School admin onboarding and license management",
                        legal_basis = "contract",
                        retention_days = 2555,
                        shared_with = Array.Empty<object>(),
                    },
                    new
                    {
                        field = "roster_import",
                        purpose = "Provision student and teacher accounts",
                        legal_basis = "contract",
                        retention_days = 1095,
                        shared_with = Array.Empty<object>(),
                    },
                },
            },
            new
            {
                category = "Operational Audit",
                data_fields = new object[]
                {
                    new
                    {
                        field = "audit_entry",
                        purpose = "Security, compliance, and incident response",
                        legal_basis = "legal_obligation",
                        retention_days = 2555,
                        shared_with = Array.Empty<object>(),
                    },
                    new
                    {
                        field = "incident_record",
                        purpose = "Operational incident post-mortems",
                        legal_basis = "legitimate_interest",
                        retention_days = 1095,
                        shared_with = Array.Empty<object>(),
                    },
                },
            },
        },
    };
}
