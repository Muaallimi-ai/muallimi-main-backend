using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Application.Identity.Credentials;

namespace Muallimi.Api.Identity.Credentials;

/// <summary>
/// DB-backed credential audit writer. Delegates to the Phase 6
/// <see cref="AuditTrailWriter"/> so credential events land in the
/// <c>audit_entries</c> table with PII masking applied — never just in
/// stdout logs which rotate away.
///
/// Action type uses the stable snake-case wire string from
/// <see cref="CredentialAuditEventKindExtensions.ToActionType"/> so the
/// rows are queryable by kind without enum mapping at read time.
/// </summary>
public sealed class CredentialAuditWriter : ICredentialAuditWriter
{
    private readonly AuditTrailWriter _phase6;

    public CredentialAuditWriter(AuditTrailWriter phase6)
    {
        _phase6 = phase6;
    }

    public Task WriteAsync(CredentialAuditEvent evt, CancellationToken ct = default)
    {
        var entry = new AuditTrailEntry
        {
            TenantId = evt.TenantId,
            ActorId = evt.ActorId,
            ActorType = evt.ActorType,
            TargetId = evt.TargetUserId,
            TargetType = "User",
            ActionType = evt.Kind.ToActionType(),
            Payload = evt.Payload,
            IpAddress = evt.IpAddress,
            UserAgent = evt.UserAgent,
            CorrelationId = evt.CorrelationId,
        };
        return _phase6.WriteAsync(entry, ct);
    }
}
