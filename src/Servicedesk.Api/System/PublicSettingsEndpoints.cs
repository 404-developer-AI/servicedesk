using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.System;

/// Anonymous derived-settings endpoints the SPA reads before sign-in
/// (moved out of Program.cs; behaviour unchanged).
public static class PublicSettingsEndpoints
{
    public static IEndpointRouteBuilder MapPublicSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // Default theme — public derived-settings endpoint. The login page reads
        // this before authentication so anonymous users land on the admin-configured
        // default theme on their first ever visit (when no localStorage exists).
        // Values: 'steaan' | 'light' | 'dark' (v0.0.108). Unknown / corrupt values
        // silently fall back to the factory default so a hand-edited DB row can't
        // break paint. Settings-store failures are swallowed and reported as the
        // factory default — the login page must always load.
        app.MapGet("/api/system/default-theme", async (ISettingsService settings, CancellationToken ct) =>
        {
            // Settings store unreachable — null falls through to the factory default.
            var raw = await settings.GetOrAsync<string?>(SettingKeys.Ui.DefaultTheme, null, ct);
            return Results.Ok(new { theme = UiThemes.NormalizeOrFactory(raw) });
        })
        .WithName("GetSystemDefaultTheme");

        // Ticket reference prefix — public derived-settings endpoint (v0.0.57). The
        // app reads this once at bootstrap so the copy-to-clipboard button produces
        // the admin-configured "Ticket#1234" form. Anonymous + cheap (single cached
        // settings read); a store failure falls back to the factory default so the
        // copy button always has a sane prefix.
        app.MapGet("/api/system/ticket-reference-prefix", async (ISettingsService settings, CancellationToken ct) =>
        {
            // Settings store unreachable — null falls through with the factory default.
            var raw = await settings.GetOrAsync<string?>(SettingKeys.Tickets.ReferencePrefix, null, ct);
            var prefix = string.IsNullOrWhiteSpace(raw) ? TicketReference.DefaultPrefix : raw;
            return Results.Ok(new { prefix });
        })
        .WithName("GetTicketReferencePrefix");

        return app;
    }
}
