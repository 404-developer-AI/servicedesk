namespace Servicedesk.Domain.TaggingMailboxes;

/// A login-less mailbox that exists only as an @@-mention target. Mentioning
/// it in a ticket note / reply / outbound mail sends a notification e-mail to
/// <see cref="Email"/>; it has no user row, no role, no tickets and never
/// signs in. Managed admin-only under Settings → Users.
public sealed record TaggingMailbox(
    Guid Id,
    string Name,
    string Email,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
