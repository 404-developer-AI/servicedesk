namespace Servicedesk.Infrastructure.Audit;

public interface IAuditLogger
{
    Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default);
}
