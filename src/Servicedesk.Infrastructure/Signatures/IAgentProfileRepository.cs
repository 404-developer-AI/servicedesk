using Servicedesk.Domain.Signatures;

namespace Servicedesk.Infrastructure.Signatures;

/// Reads/writes the signature profile columns on the <c>users</c> row. Each
/// value is a local override of the Entra ID value (null = use Entra / collapse).
public interface IAgentProfileRepository
{
    Task<AgentProfile?> GetAsync(Guid userId, CancellationToken ct);

    /// Sets the local override fields (admin/self-service profile editor).
    Task<bool> UpsertOverrideAsync(
        Guid userId, string? displayName, string? jobTitle,
        string? workPhone, string? mobilePhone, CancellationToken ct);

    /// Stores the content-addressed photo blob hash + mime (uploaded or Entra).
    /// Pass nulls to clear the photo.
    Task SetPhotoAsync(Guid userId, string? blobHash, string? mime, CancellationToken ct);

    /// Stamps the last successful Entra pull.
    Task StampEntraSyncedAsync(Guid userId, CancellationToken ct);

    /// All agent/admin accounts with their signature profile fields — drives
    /// the admin "team profiles" management UI. Customers are excluded
    /// (signatures never apply to them).
    Task<IReadOnlyList<AgentProfileListItem>> ListAllAsync(CancellationToken ct);
}

/// One row of the admin team-profiles list.
public sealed record AgentProfileListItem(
    Guid UserId,
    string Email,
    string RoleName,
    string? DisplayName,
    string? JobTitle,
    string? WorkPhone,
    string? MobilePhone,
    bool HasPhoto,
    DateTime? EntraSyncedUtc);
