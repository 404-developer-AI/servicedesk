using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Views;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Views;
using Servicedesk.Infrastructure.Preferences;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Preferences;

public static class UserPreferencesEndpoints
{
    public static IEndpointRouteBuilder MapUserPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences")
            .WithTags("Preferences")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // GET /api/preferences/columns — cascading resolution:
        // 1. user's per-view override, 2. view's own columns, 3. user's general default,
        // 4. admin global default.
        // v0.1.18 — a view with allow_user_columns = FALSE skips both user
        // levels (1 and 3): everyone gets the view's columns, or the admin
        // default when the view has none. The user rows are kept, so
        // unlocking the view brings each agent's own layout back.
        group.MapGet("/columns", async (
            Guid? viewId,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, [FromServices] IViewRepository views,
            [FromServices] ISettingsService settings, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);

            string? perView = null;
            View? view = null;
            if (viewId is Guid id)
            {
                perView = await prefs.GetAsync(userId, $"columns:view:{id}", ct);
                view = await views.GetAsync(id, ct);
            }

            var userDefault = await prefs.GetAsync(userId, "columns", ct);
            var adminDefault = await settings.GetAsync<string>(SettingKeys.Tickets.DefaultColumnLayout, ct);

            return Results.Ok(ColumnPreferenceResolver.Resolve(
                perView, view?.Columns, view?.AllowUserColumns ?? true, userDefault, adminDefault));
        }).WithName("GetColumnPreference");

        // PUT /api/preferences/columns — save a column layout.
        // If viewId is provided, saves as a per-view user override; otherwise saves as the general default.
        // v0.1.18 — refused (409) for a view whose column layout is locked.
        group.MapPut("/columns", async (
            Guid? viewId,
            [FromBody] UpdateColumnPreferenceRequest req,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, [FromServices] IViewRepository views, CancellationToken ct) =>
        {
            if (!ColumnPreferenceResolver.IsValidLayout(req.Columns))
                return Results.BadRequest(new { error = "Columns must be a comma-separated list of column ids." });

            var userId = ActorContext.GetUserId(http);
            var key = viewId.HasValue ? $"columns:view:{viewId}" : "columns";

            if (viewId is Guid id && await views.GetAsync(id, ct) is { AllowUserColumns: false })
                return Results.Conflict(new { error = "Columns are set by this view." });

            await prefs.SetAsync(userId, key, req.Columns, ct);
            return Results.NoContent();
        }).WithName("UpdateColumnPreference");

        // DELETE /api/preferences/columns — reset to next level in the cascade.
        // If viewId is provided, removes only the per-view override; otherwise removes the general default.
        group.MapDelete("/columns", async (
            Guid? viewId,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var key = viewId.HasValue ? $"columns:view:{viewId}" : "columns";
            await prefs.DeleteAsync(userId, key, ct);
            return Results.NoContent();
        }).WithName("ResetColumnPreference");

        // ── Workspace snapshot (generic key-value for workspace:* keys) ──

        // GET /api/preferences/workspace — all workspace keys for the current user.
        group.MapGet("/workspace", async (
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            return Results.Ok(await prefs.GetByPrefixAsync(userId, "workspace:", ct));
        }).WithName("GetWorkspacePreferences");

        // PUT /api/preferences/workspace — batch upsert workspace keys.
        group.MapPut("/workspace", async (
            [FromBody] SaveWorkspaceRequest req,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            if (req.Entries is not { Length: > 0 })
                return Results.BadRequest("Entries must not be empty.");

            if (req.Entries.Any(e => !e.Key.StartsWith("workspace:")))
                return Results.BadRequest("All keys must start with 'workspace:'.");

            var userId = ActorContext.GetUserId(http);

            // v0.1.25 — one statement for the whole snapshot. Last value wins
            // for a key sent twice (ON CONFLICT cannot touch the same row
            // twice in one statement).
            var latest = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in req.Entries) latest[entry.Key] = entry.Value;

            await prefs.SetManyAsync(userId, latest, ct);
            return Results.NoContent();
        }).WithName("SaveWorkspacePreferences");

        // DELETE /api/preferences/workspace/{key} — remove one workspace key.
        group.MapDelete("/workspace/{key}", async (
            string key,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            if (!key.StartsWith("workspace:"))
                return Results.BadRequest("Key must start with 'workspace:'.");

            await prefs.DeleteAsync(ActorContext.GetUserId(http), key, ct);
            return Results.NoContent();
        }).WithName("DeleteWorkspacePreference");

        // ── UI theme preference (v0.0.44) ──
        //
        // Cascade on GET: user override (user_preferences.ui:theme) →
        // admin default (Ui.DefaultTheme) → UiThemes.Factory. Values:
        // 'steaan' | 'light' | 'dark' (light/dark = Nebula, v0.0.108). The source
        // field lets the SPA show the right tooltip ("inherited from
        // organisation default" vs "your choice").
        //
        // Auth scope: same RequireAgent gate as the rest of /api/preferences.
        // The v0.1.x customer portal will gain a separate endpoint with
        // RequireCustomer when it lands.

        group.MapGet("/ui-theme", async (
            HttpContext http, [FromServices] IUserPreferenceStore prefs, [FromServices] ISettingsService settings, CancellationToken ct) =>
        {
            var userPref = await prefs.GetAsync(ActorContext.GetUserId(http), ThemeKey, ct);
            if (UiThemes.Normalize(userPref) is { } user)
                return Results.Ok(new ThemePreference(user, "user"));

            var adminDefault = UiThemes.NormalizeOrFactory(await settings.GetAsync<string>(SettingKeys.Ui.DefaultTheme, ct));
            return Results.Ok(new ThemePreference(adminDefault, "default"));
        }).WithName("GetUiThemePreference");

        group.MapPut("/ui-theme", async (
            [FromBody] UpdateUiThemeRequest req,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var normalized = UiThemes.Normalize(req.Theme);
            if (normalized is null)
                return Results.BadRequest(new { error = "Theme must be 'steaan', 'light' or 'dark'." });

            await prefs.SetAsync(ActorContext.GetUserId(http), ThemeKey, normalized, ct);
            return Results.NoContent();
        }).WithName("UpdateUiThemePreference");

        group.MapDelete("/ui-theme", async (
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            await prefs.DeleteAsync(ActorContext.GetUserId(http), ThemeKey, ct);
            return Results.NoContent();
        }).WithName("ResetUiThemePreference");

        // ── Pinned nav features (v0.0.60) ──
        //
        // Per-user list of feature-page paths the agent pinned out of the
        // sidebar "Features" flyout so they render inline under Dashboard.
        // Stored as a JSON array under the 'nav:pinned-features' key; survives
        // logout/login because it lives in user_preferences. Bounded + format-
        // validated on write; the SPA intersects against the live nav set so a
        // stale path simply has no effect.

        group.MapGet("/pinned-features", async (
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var raw = await prefs.GetAsync(ActorContext.GetUserId(http), PinnedFeaturesKey, ct);
            return Results.Ok(new PinnedFeaturesPreference(ParsePinnedPaths(raw)));
        }).WithName("GetPinnedFeatures");

        group.MapPut("/pinned-features", async (
            [FromBody] UpdatePinnedFeaturesRequest req,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var clean = NormalizePinnedPaths(req.Paths);
            if (clean is null)
                return Results.BadRequest(new { error = "Invalid pinned-features payload." });

            await prefs.SetAsync(ActorContext.GetUserId(http), PinnedFeaturesKey, JsonSerializer.Serialize(clean), ct);
            return Results.Ok(new PinnedFeaturesPreference(clean));
        }).WithName("UpdatePinnedFeatures");

        // ── Pinned views (v0.0.65) ──
        //
        // Per-user list of saved-view IDs the agent pinned out of the sidebar
        // "Views" flyout so they render inline above the flyout. Stored as a
        // JSON array of GUID strings under 'nav:pinned-views'; mirrors
        // pinned-features. The SPA intersects against the live view set, so a
        // pin for a since-deleted (or no-longer-accessible) view is ignored and
        // never re-saved.

        group.MapGet("/pinned-views", async (
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var raw = await prefs.GetAsync(ActorContext.GetUserId(http), PinnedViewsKey, ct);
            return Results.Ok(new PinnedViewsPreference(ParsePinnedViewIds(raw)));
        }).WithName("GetPinnedViews");

        group.MapPut("/pinned-views", async (
            [FromBody] UpdatePinnedViewsRequest req,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var clean = NormalizePinnedViewIds(req.Ids);
            if (clean is null)
                return Results.BadRequest(new { error = "Invalid pinned-views payload." });

            await prefs.SetAsync(ActorContext.GetUserId(http), PinnedViewsKey, JsonSerializer.Serialize(clean), ct);
            return Results.Ok(new PinnedViewsPreference(clean));
        }).WithName("UpdatePinnedViews");

        // ── Post-close redirect (v0.0.78) ──
        //
        // Per-user choice of where the sidebar's "remove from Recent" (×)
        // action sends the agent when they dismiss the ticket they currently
        // have open. Stored as a single token under 'nav:post-close-redirect':
        //   "dashboard"      → "/"            (factory default)
        //   "timesheet"      → "/timesheet"
        //   "view:<guid>"    → "/tickets?viewId=<guid>"
        // The SPA only offers destinations the agent can actually reach and
        // re-intersects against the live view set + timesheet flag at click
        // time, so a stale "view:" token (deleted / access revoked) simply
        // falls back to the dashboard — no broken navigation.

        group.MapGet("/post-close-redirect", async (
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var raw = await prefs.GetAsync(ActorContext.GetUserId(http), PostCloseRedirectKey, ct);
            return Results.Ok(new PostCloseRedirectPreference(NormalizePostCloseRedirect(raw) ?? "dashboard"));
        }).WithName("GetPostCloseRedirect");

        group.MapPut("/post-close-redirect", async (
            [FromBody] UpdatePostCloseRedirectRequest req,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var normalized = NormalizePostCloseRedirect(req.Target);
            if (normalized is null)
                return Results.BadRequest(new { error = "Target must be 'dashboard', 'timesheet', or 'view:<guid>'." });

            await prefs.SetAsync(ActorContext.GetUserId(http), PostCloseRedirectKey, normalized, ct);
            return Results.Ok(new PostCloseRedirectPreference(normalized));
        }).WithName("UpdatePostCloseRedirect");

        // v0.1.54 — which notifications also arrive by mail (Profile → Tag
        // notifications). Stored per user in user_preferences; a missing row
        // means the switch's default (tags on, replies on, likes off). Only
        // the caller's own rows; the app-wide mail switch still wins.
        group.MapGet("/notification-mail", async (
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            var rows = await prefs.GetManyAsync(ActorContext.GetUserId(http), MailPrefs.Select(p => p.Key).ToArray(), ct);
            bool Read(NotificationMailPreference p)
                => rows.TryGetValue(p.Key, out var v) ? v == "true" || (p.DefaultOn && v != "false") : p.DefaultOn;
            return Results.Ok(new NotificationMailPreferences(
                Read(NotificationPreferenceKeys.MentionEmailPreference),
                Read(NotificationPreferenceKeys.ReplyEmailPreference),
                Read(NotificationPreferenceKeys.LikeEmailPreference)));
        }).WithName("GetNotificationMailPreferences");

        group.MapPut("/notification-mail", async (
            [FromBody] UpdateNotificationMailPreferences req,
            HttpContext http, [FromServices] IUserPreferenceStore prefs, CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new { error = "Body is required." });
            var changes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (req.Tags is { } t) changes[NotificationPreferenceKeys.MentionEmail] = t ? "true" : "false";
            if (req.Replies is { } r) changes[NotificationPreferenceKeys.ReplyEmail] = r ? "true" : "false";
            if (req.Likes is { } l) changes[NotificationPreferenceKeys.LikeEmail] = l ? "true" : "false";
            await prefs.SetManyAsync(ActorContext.GetUserId(http), changes, ct);
            return Results.NoContent();
        }).WithName("UpdateNotificationMailPreferences");

        return app;
    }

    private const string ThemeKey = UiThemes.PreferenceKey;
    private const string PinnedFeaturesKey = "nav:pinned-features";
    private const string PinnedViewsKey = "nav:pinned-views";
    private const string PostCloseRedirectKey = "nav:post-close-redirect";

    private static readonly NotificationMailPreference[] MailPrefs =
    {
        NotificationPreferenceKeys.MentionEmailPreference,
        NotificationPreferenceKeys.ReplyEmailPreference,
        NotificationPreferenceKeys.LikeEmailPreference,
    };

    public sealed record NotificationMailPreferences(bool Tags, bool Replies, bool Likes);
    public sealed record UpdateNotificationMailPreferences(bool? Tags, bool? Replies, bool? Likes);

    /// Validates the post-close redirect token. Accepts the literals
    /// "dashboard" / "timesheet", or "view:&lt;guid&gt;" with a parseable GUID
    /// (canonicalised). Returns null for anything else so a bad write is
    /// rejected and a hand-edited DB row coerces to the default on read.
    private static string? NormalizePostCloseRedirect(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (string.Equals(v, "dashboard", StringComparison.OrdinalIgnoreCase)) return "dashboard";
        if (string.Equals(v, "timesheet", StringComparison.OrdinalIgnoreCase)) return "timesheet";
        if (v.StartsWith("view:", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(v["view:".Length..], out var g))
            return $"view:{g}";
        return null;
    }

    /// Reads the stored JSON array, coercing any malformed row to an empty
    /// list rather than throwing — a hand-edited DB value must not 500 the nav.
    private static string[] ParsePinnedPaths(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(raw);
            return NormalizePinnedPaths(arr) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// Bounds + format guard. Returns null when the payload is structurally
    /// invalid (too many entries, an over-long value, or a non-path entry) so
    /// the write is rejected; otherwise the trimmed, de-duplicated set.
    private static string[]? NormalizePinnedPaths(IReadOnlyList<string>? paths)
    {
        if (paths is null) return Array.Empty<string>();
        if (paths.Count > 20) return null;
        var clean = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var t = p.Trim();
            if (t.Length > 64 || t[0] != '/') return null;
            if (!clean.Contains(t)) clean.Add(t);
        }
        return clean.ToArray();
    }

    /// Reads the stored JSON array of view-id strings, coercing any malformed
    /// row to an empty list rather than throwing.
    private static string[] ParsePinnedViewIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(raw);
            return NormalizePinnedViewIds(arr) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// Bounds + format guard for pinned view ids. Returns null when the payload
    /// is structurally invalid (too many entries or a non-GUID entry) so the
    /// write is rejected; otherwise the de-duplicated set of canonical GUID
    /// strings.
    private static string[]? NormalizePinnedViewIds(IReadOnlyList<string>? ids)
    {
        if (ids is null) return Array.Empty<string>();
        if (ids.Count > 50) return null;
        var clean = new List<string>(ids.Count);
        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Guid.TryParse(raw.Trim(), out var g)) return null;
            var canonical = g.ToString();
            if (!clean.Contains(canonical)) clean.Add(canonical);
        }
        return clean.ToArray();
    }

    public sealed record ColumnPreference(string Columns, string Source, bool Locked = false);
    public sealed record UpdateColumnPreferenceRequest(string Columns);
    public sealed record WorkspaceEntry(string Key, string Value);
    public sealed record SaveWorkspaceRequest(WorkspaceEntry[] Entries);
    public sealed record ThemePreference(string Theme, string Source);
    public sealed record UpdateUiThemeRequest(string Theme);
    public sealed record PinnedFeaturesPreference(string[] Paths);
    public sealed record UpdatePinnedFeaturesRequest(string[] Paths);
    public sealed record PinnedViewsPreference(string[] Ids);
    public sealed record UpdatePinnedViewsRequest(string[] Ids);
    public sealed record PostCloseRedirectPreference(string Target);
    public sealed record UpdatePostCloseRedirectRequest(string Target);
}
