using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Settings;

public static class SettingEndpoints
{
    public static IEndpointRouteBuilder MapSettingEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Admin CRUD ----
        var admin = app.MapGroup("/api/settings")
            .WithTags("Settings")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/", async (ISettingsService svc, [FromQuery] string? category, CancellationToken ct) =>
        {
            var entries = await svc.ListAsync(category, ct);
            return Results.Ok(entries);
        }).WithName("ListSettings").WithOpenApi();

        admin.MapPut("/{key}", async (
            string key,
            [FromBody] UpdateSettingRequest req,
            HttpContext http,
            ISettingsService svc,
            CancellationToken ct) =>
        {
            var actor = http.User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown";
            var actorRole = http.User.FindFirst(ClaimTypes.Role)?.Value ?? "unknown";
            try
            {
                await svc.SetAsync<string>(key, req.Value, actor, actorRole, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Unknown setting key '{key}'." });
            }
        }).WithName("UpdateSetting").WithOpenApi();

        // ---- Navigation settings (any authenticated agent/admin) ----
        app.MapGet("/api/settings/navigation", async (ISettingsService svc, CancellationToken ct) =>
        {
            var showOpenTickets = await svc.GetAsync<bool>(SettingKeys.Navigation.ShowOpenTickets, ct);
            return Results.Ok(new NavigationSettings(showOpenTickets));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetNavigationSettings")
        .WithOpenApi();

        // ---- Notification settings (v0.0.12 stap 4, agent-readable) ----
        // The notification raamwerk needs the toast duration on the client.
        // Exposing the two public-safe knobs here keeps the settings read
        // off the admin-only `/api/settings` endpoint.
        app.MapGet("/api/settings/notifications", async (ISettingsService svc, CancellationToken ct) =>
        {
            var popupDuration = await svc.GetAsync<int>(SettingKeys.Notifications.PopupDurationSeconds, ct);
            if (popupDuration <= 0) popupDuration = 10;
            return Results.Ok(new NotificationsSettings(popupDuration));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetNotificationsSettings")
        .WithOpenApi();

        // ---- Mail compose settings (v0.0.85, agent-readable) ----
        // The composer's "forgotten attachment" warning needs the enable flag
        // and keyword list on the client. Exposed here (agent-safe projection)
        // so the read stays off the admin-only `/api/settings` endpoint.
        app.MapGet("/api/settings/mail-compose", async (ISettingsService svc, CancellationToken ct) =>
        {
            var enabled = await svc.GetAsync<bool>(SettingKeys.Mail.ForgottenAttachmentEnabled, ct);
            var keywords = await svc.GetAsync<string>(SettingKeys.Mail.ForgottenAttachmentKeywords, ct) ?? string.Empty;
            return Results.Ok(new MailComposeSettings(enabled, keywords));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetMailComposeSettings")
        .WithOpenApi();

        // ---- Ticket grouping settings (v0.0.85, agent-readable) ----
        // The grouped ticket list applies a per-state sort inside status groups.
        // Exposed here (agent-safe projection) so the list can read it without the
        // admin-only `/api/settings` endpoint. Falls open to the view's global
        // sort if the read fails or grouping is disabled.
        app.MapGet("/api/settings/ticket-grouping", async (ISettingsService svc, CancellationToken ct) =>
        {
            var enabled = await svc.GetAsync<bool>(SettingKeys.Tickets.GroupSortEnabled, ct);
            var pendingField = await svc.GetAsync<string>(SettingKeys.Tickets.GroupSortPendingField, ct) ?? "pendingTillUtc";
            var pendingDirection = await svc.GetAsync<string>(SettingKeys.Tickets.GroupSortPendingDirection, ct) ?? "asc";
            var openNewField = await svc.GetAsync<string>(SettingKeys.Tickets.GroupSortOpenNewField, ct) ?? "updatedUtc";
            var openNewDirection = await svc.GetAsync<string>(SettingKeys.Tickets.GroupSortOpenNewDirection, ct) ?? "asc";
            return Results.Ok(new TicketGroupingSettings(
                enabled, pendingField, pendingDirection, openNewField, openNewDirection));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetTicketGroupingSettings")
        .WithOpenApi();

        // ---- Bulk actions settings (v0.0.102, agent-readable) ----
        // The ticket list needs the enable flag (show row checkboxes at all)
        // and the selection cap (disable the bulk-edit button above it).
        // Neither is sensitive; the server re-enforces both on the bulk
        // endpoint regardless of what the client read.
        app.MapGet("/api/settings/bulk-actions", async (ISettingsService svc, CancellationToken ct) =>
        {
            bool enabled;
            int max;
            try { enabled = await svc.GetAsync<bool>(SettingKeys.Tickets.BulkActionsEnabled, ct); }
            catch { enabled = true; }
            try { max = await svc.GetAsync<int>(SettingKeys.Tickets.BulkActionsMaxSelection, ct); }
            catch { max = 100; }
            if (max <= 0) max = 100;
            max = Math.Min(max, Servicedesk.Infrastructure.Tickets.TicketBulkActionService.HardMaxSelection);
            return Results.Ok(new BulkActionsSettings(enabled, max));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetBulkActionsSettings")
        .WithOpenApi();

        // ---- Checklist settings (v0.0.103, agent-readable) ----
        // The ticket page needs the enable flag (render the bar/header/panel
        // at all), the blocking categories (client-side pre-check before the
        // PATCH so the agent gets the "finish the checklist" dialog without a
        // round-trip) and the caps (disable "add" buttons at the limit). The
        // server re-enforces every one of them.
        app.MapGet("/api/settings/checklists", async (
            Servicedesk.Infrastructure.Checklists.IChecklistSettingsReader reader, CancellationToken ct) =>
        {
            var s = await reader.GetAsync(ct);
            return Results.Ok(new ChecklistSettingsDto(
                s.Enabled, s.BlockingStateCategories, s.LogItemChangesToTimeline, s.MaxPerTicket, s.MaxItemsPerChecklist));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetChecklistSettings")
        .WithOpenApi();

        // ---- Copilot launcher settings (v0.0.89, agent-readable) ----
        // The nav button needs the enable flag, URL, label and open-mode on the
        // client to render itself. None of these are secret (it is a public
        // Microsoft URL), so the agent-safe projection lives here, off the
        // admin-only `/api/settings` endpoint. Falls back to the registered
        // defaults if a read returns empty.
        app.MapGet("/api/settings/copilot", async (ISettingsService svc, CancellationToken ct) =>
        {
            var enabled = await svc.GetAsync<bool>(SettingKeys.Copilot.Enabled, ct);
            var url = await svc.GetAsync<string>(SettingKeys.Copilot.Url, ct) ?? string.Empty;
            var label = await svc.GetAsync<string>(SettingKeys.Copilot.Label, ct);
            if (string.IsNullOrWhiteSpace(label)) label = "Copilot";
            var openInPopup = await svc.GetAsync<bool>(SettingKeys.Copilot.OpenInPopup, ct);
            return Results.Ok(new CopilotSettings(enabled, url, label, openInPopup));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetCopilotSettings")
        .WithOpenApi();

        return app;
    }

    public sealed record UpdateSettingRequest([property: Required] string Value);
    public sealed record NavigationSettings(bool ShowOpenTickets);
    public sealed record CopilotSettings(bool Enabled, string Url, string Label, bool OpenInPopup);
    public sealed record NotificationsSettings(int PopupDurationSeconds);
    public sealed record MailComposeSettings(bool ForgottenAttachmentEnabled, string ForgottenAttachmentKeywords);
    public sealed record BulkActionsSettings(bool Enabled, int MaxSelection);

    public sealed record ChecklistSettingsDto(
        bool Enabled,
        IReadOnlyList<string> BlockingStateCategories,
        bool LogItemChangesToTimeline,
        int MaxPerTicket,
        int MaxItemsPerChecklist);
    public sealed record TicketGroupingSettings(
        bool Enabled,
        string PendingField,
        string PendingDirection,
        string OpenNewField,
        string OpenNewDirection);
}
