namespace Muallimi.Api.OperatorManagement.LaunchReadinessGate;

/// <summary>
/// T123 (US9) — Static launch-readiness criterion descriptors per
/// operator-management-contract.md §"Launch-Readiness Criteria".
/// Evidence source paths resolve relative to the planning-docs repo
/// (../Muaallimi-Platform-Planning-Docs-main) or the backend repo.
/// </summary>
public sealed record LaunchReadinessCriterion
{
    public required string Key { get; init; }
    public required string NameAr { get; init; }
    public required string NameEn { get; init; }
    public required string Category { get; init; }
    public required string EvidenceSource { get; init; }
}

public static class LaunchReadinessCriteria
{
    public static readonly IReadOnlyList<LaunchReadinessCriterion> All = new List<LaunchReadinessCriterion>
    {
        new()
        {
            Key = "phase_0_5_readiness",
            NameAr = "جاهزية المراحل 0 إلى 5",
            NameEn = "Phase 0–5 readiness evidence",
            Category = "readiness",
            EvidenceSource = "specs/*/checklists/requirements.md",
        },
        new()
        {
            Key = "security_audit",
            NameAr = "نتائج المراجعة الأمنية",
            NameEn = "Security audit results",
            Category = "security",
            EvidenceSource = "tests/integration/SaasOperations/Security/SecurityAndDataProtectionTests.cs",
        },
        new()
        {
            Key = "auth_bypass_tests",
            NameAr = "اختبارات تجاوز المصادقة",
            NameEn = "Authentication bypass tests",
            Category = "security",
            EvidenceSource = "tests/security/",
        },
        new()
        {
            Key = "pii_encryption",
            NameAr = "التحقق من تشفير البيانات الحساسة",
            NameEn = "PII encryption verification",
            Category = "security",
            EvidenceSource = "src/Muallimi.Infrastructure/Persistence/ColumnEncryption.cs",
        },
        new()
        {
            Key = "performance_benchmarks",
            NameAr = "مؤشرات الأداء",
            NameEn = "Performance benchmarks",
            Category = "performance",
            EvidenceSource = "tests/integration/SaasOperations/_evidence/perf.json",
        },
        new()
        {
            Key = "arabic_quality",
            NameAr = "جودة اللغة العربية",
            NameEn = "Arabic quality validation",
            Category = "arabic_quality",
            EvidenceSource = "Muaallimi-Platform/tests/e2e/**/arabic-quality/",
        },
        new()
        {
            Key = "accessibility",
            NameAr = "الامتثال لمعايير الوصول",
            NameEn = "Accessibility compliance",
            Category = "accessibility",
            EvidenceSource = "Muaallimi-Platform/tests/e2e/**/a11y/",
        },
        new()
        {
            Key = "billing_e2e",
            NameAr = "اختبار الفوترة الشامل",
            NameEn = "Billing end-to-end test",
            Category = "billing",
            EvidenceSource = "tests/integration/SaasOperations/Billing/",
        },
        new()
        {
            Key = "notification_delivery",
            NameAr = "اختبار إرسال الإشعارات",
            NameEn = "Notification delivery test",
            Category = "observability",
            EvidenceSource = "tests/integration/SaasOperations/Notifications/",
        },
        new()
        {
            Key = "observability_dashboard",
            NameAr = "لوحات الرصد والمراقبة",
            NameEn = "Observability dashboard check",
            Category = "observability",
            EvidenceSource = "src/Muallimi.Api/AiOperations/",
        },
        new()
        {
            Key = "runbook_documentation",
            NameAr = "توثيق إجراءات التشغيل",
            NameEn = "Runbook documentation",
            Category = "compliance",
            EvidenceSource = "docs/runbooks/",
        },
        new()
        {
            Key = "data_protection",
            NameAr = "ضوابط حماية البيانات",
            NameEn = "Data protection controls",
            Category = "compliance",
            EvidenceSource = "src/Muallimi.Api/Compliance/",
        },
    };
}
