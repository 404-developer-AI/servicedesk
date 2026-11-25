using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Tickets;

/// v0.1.67 — the one "project tickets are internal" mail rule. A project
/// ticket never sends customer mail while the projects feature is on:
/// the manual mail endpoint 409s, a trigger `send_mail` to the customer is
/// a no-op, and a campaign skips the ticket. Switching the feature off
/// returns the ticket to the normal rules. Every caller goes through here
/// so the three paths can never drift.
public static class ProjectMailGuard
{
    public static async Task<bool> IsBlockedAsync(ISettingsService settings, bool isProject, CancellationToken ct)
    {
        if (!isProject) return false;
        return await settings.GetOrAsync<bool>(SettingKeys.Projects.Enabled, true, ct);
    }
}
