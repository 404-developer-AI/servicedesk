using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using Npgsql;
using Servicedesk.Api.Auth;
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
        group.MapGet("/columns", async (
            Guid? viewId,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, [FromServices] ISettingsService settings, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // 1. User's per-view override
            if (viewId.HasValue)
            {
                var perView = await conn.ExecuteScalarAsync<string?>(
                    new CommandDefinition(
                        "SELECT pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = @key",
                        new { userId, key = $"columns:view:{viewId}" }, cancellationToken: ct));
                if (perView is not null)
                    return Results.Ok(new ColumnPreference(perView, "user-view"));
            }

            // 2. View's own columns
            if (viewId.HasValue)
            {
                var viewColumns = await conn.ExecuteScalarAsync<string?>(
                    new CommandDefinition(
                        "SELECT columns FROM views WHERE id = @viewId",
                        new { viewId }, cancellationToken: ct));
                if (viewColumns is not null)
                    return Results.Ok(new ColumnPreference(viewColumns, "view"));
            }

            // 3. User's general column preference
            var userDefault = await conn.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    "SELECT pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = 'columns'",
                    new { userId }, cancellationToken: ct));
            if (userDefault is not null)
                return Results.Ok(new ColumnPreference(userDefault, "user"));

            // 4. Admin global default
            var adminDefault = await settings.GetAsync<string>(SettingKeys.Tickets.DefaultColumnLayout, ct);
            return Results.Ok(new ColumnPreference(adminDefault, "default"));
        }).WithName("GetColumnPreference").WithOpenApi();

        // PUT /api/preferences/columns — save a column layout.
        // If viewId is provided, saves as a per-view user override; otherwise saves as the general default.
        group.MapPut("/columns", async (
            Guid? viewId,
            [FromBody] UpdateColumnPreferenceRequest req,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var key = viewId.HasValue ? $"columns:view:{viewId}" : "columns";

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_preferences (user_id, pref_key, pref_value)
                VALUES (@userId, @key, @columns)
                ON CONFLICT (user_id, pref_key) DO UPDATE
                    SET pref_value = @columns, updated_utc = now()
                """,
                new { userId, key, columns = req.Columns }, cancellationToken: ct));

            return Results.NoContent();
        }).WithName("UpdateColumnPreference").WithOpenApi();

        // DELETE /api/preferences/columns — reset to next level in the cascade.
        // If viewId is provided, removes only the per-view override; otherwise removes the general default.
        group.MapDelete("/columns", async (
            Guid? viewId,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var key = viewId.HasValue ? $"columns:view:{viewId}" : "columns";

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM user_preferences WHERE user_id = @userId AND pref_key = @key",
                new { userId, key }, cancellationToken: ct));

            return Results.NoContent();
        }).WithName("ResetColumnPreference").WithOpenApi();

        // ── Workspace snapshot (generic key-value for workspace:* keys) ──

        // GET /api/preferences/workspace — all workspace keys for the current user.
        group.MapGet("/workspace", async (
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            var rows = await conn.QueryAsync<(string pref_key, string pref_value)>(
                new CommandDefinition(
                    "SELECT pref_key, pref_value FROM user_preferences WHERE user_id = @userId AND pref_key LIKE 'workspace:%'",
                    new { userId }, cancellationToken: ct));

            var dict = rows.ToDictionary(r => r.pref_key, r => r.pref_value);
            return Results.Ok(dict);
        }).WithName("GetWorkspacePreferences").WithOpenApi();

        // PUT /api/preferences/workspace — batch upsert workspace keys.
        group.MapPut("/workspace", async (
            [FromBody] SaveWorkspaceRequest req,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            if (req.Entries is not { Length: > 0 })
                return Results.BadRequest("Entries must not be empty.");

            if (req.Entries.Any(e => !e.Key.StartsWith("workspace:")))
                return Results.BadRequest("All keys must start with 'workspace:'.");

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            foreach (var entry in req.Entries)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO user_preferences (user_id, pref_key, pref_value)
                    VALUES (@userId, @key, @value)
                    ON CONFLICT (user_id, pref_key) DO UPDATE
                        SET pref_value = @value, updated_utc = now()
                    """,
                    new { userId, key = entry.Key, value = entry.Value },
                    transaction: tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).WithName("SaveWorkspacePreferences").WithOpenApi();

        // DELETE /api/preferences/workspace/{key} — remove one workspace key.
        group.MapDelete("/workspace/{key}", async (
            string key,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            if (!key.StartsWith("workspace:"))
                return Results.BadRequest("Key must start with 'workspace:'.");

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM user_preferences WHERE user_id = @userId AND pref_key = @key",
                new { userId, key }, cancellationToken: ct));

            return Results.NoContent();
        }).WithName("DeleteWorkspacePreference").WithOpenApi();

        // ── UI theme preference (v0.0.44) ──
        //
        // Cascade on GET: user override (user_preferences.ui:theme) →
        // admin default (Ui.DefaultTheme) → factory 'light'. The source
        // field lets the SPA show the right tooltip ("inherited from
        // organisation default" vs "your choice").
        //
        // Auth scope: same RequireAgent gate as the rest of /api/preferences.
        // The v0.1.x customer portal will gain a separate endpoint with
        // RequireCustomer when it lands.

        group.MapGet("/ui-theme", async (
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, [FromServices] ISettingsService settings, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            var userPref = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = 'ui:theme'",
                new { userId }, cancellationToken: ct));
            if (NormalizeTheme(userPref) is { } user)
                return Results.Ok(new ThemePreference(user, "user"));

            var adminDefault = NormalizeTheme(await settings.GetAsync<string>(SettingKeys.Ui.DefaultTheme, ct));
            return Results.Ok(new ThemePreference(adminDefault ?? "light", "default"));
        }).WithName("GetUiThemePreference").WithOpenApi();

        group.MapPut("/ui-theme", async (
            [FromBody] UpdateUiThemeRequest req,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var normalized = NormalizeTheme(req.Theme);
            if (normalized is null)
                return Results.BadRequest(new { error = "Theme must be 'light' or 'dark'." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_preferences (user_id, pref_key, pref_value)
                VALUES (@userId, 'ui:theme', @value)
                ON CONFLICT (user_id, pref_key) DO UPDATE
                    SET pref_value = @value, updated_utc = now()
                """,
                new { userId, value = normalized }, cancellationToken: ct));

            return Results.NoContent();
        }).WithName("UpdateUiThemePreference").WithOpenApi();

        group.MapDelete("/ui-theme", async (
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM user_preferences WHERE user_id = @userId AND pref_key = 'ui:theme'",
                new { userId }, cancellationToken: ct));
            return Results.NoContent();
        }).WithName("ResetUiThemePreference").WithOpenApi();

        // ── Pinned nav features (v0.0.60) ──
        //
        // Per-user list of feature-page paths the agent pinned out of the
        // sidebar "Features" flyout so they render inline under Dashboard.
        // Stored as a JSON array under the 'nav:pinned-features' key; survives
        // logout/login because it lives in user_preferences. Bounded + format-
        // validated on write; the SPA intersects against the live nav set so a
        // stale path simply has no effect.

        group.MapGet("/pinned-features", async (
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var raw = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = 'nav:pinned-features'",
                new { userId }, cancellationToken: ct));
            return Results.Ok(new PinnedFeaturesPreference(ParsePinnedPaths(raw)));
        }).WithName("GetPinnedFeatures").WithOpenApi();

        group.MapPut("/pinned-features", async (
            [FromBody] UpdatePinnedFeaturesRequest req,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var clean = NormalizePinnedPaths(req.Paths);
            if (clean is null)
                return Results.BadRequest(new { error = "Invalid pinned-features payload." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var value = JsonSerializer.Serialize(clean);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_preferences (user_id, pref_key, pref_value)
                VALUES (@userId, 'nav:pinned-features', @value)
                ON CONFLICT (user_id, pref_key) DO UPDATE
                    SET pref_value = @value, updated_utc = now()
                """,
                new { userId, value }, cancellationToken: ct));

            return Results.Ok(new PinnedFeaturesPreference(clean));
        }).WithName("UpdatePinnedFeatures").WithOpenApi();

        // ── Pinned views (v0.0.65) ──
        //
        // Per-user list of saved-view IDs the agent pinned out of the sidebar
        // "Views" flyout so they render inline above the flyout. Stored as a
        // JSON array of GUID strings under 'nav:pinned-views'; mirrors
        // pinned-features. The SPA intersects against the live view set, so a
        // pin for a since-deleted (or no-longer-accessible) view is ignored and
        // never re-saved.

        group.MapGet("/pinned-views", async (
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var raw = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = 'nav:pinned-views'",
                new { userId }, cancellationToken: ct));
            return Results.Ok(new PinnedViewsPreference(ParsePinnedViewIds(raw)));
        }).WithName("GetPinnedViews").WithOpenApi();

        group.MapPut("/pinned-views", async (
            [FromBody] UpdatePinnedViewsRequest req,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var clean = NormalizePinnedViewIds(req.Ids);
            if (clean is null)
                return Results.BadRequest(new { error = "Invalid pinned-views payload." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var value = JsonSerializer.Serialize(clean);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_preferences (user_id, pref_key, pref_value)
                VALUES (@userId, 'nav:pinned-views', @value)
                ON CONFLICT (user_id, pref_key) DO UPDATE
                    SET pref_value = @value, updated_utc = now()
                """,
                new { userId, value }, cancellationToken: ct));

            return Results.Ok(new PinnedViewsPreference(clean));
        }).WithName("UpdatePinnedViews").WithOpenApi();

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
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var raw = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = 'nav:post-close-redirect'",
                new { userId }, cancellationToken: ct));
            return Results.Ok(new PostCloseRedirectPreference(NormalizePostCloseRedirect(raw) ?? "dashboard"));
        }).WithName("GetPostCloseRedirect").WithOpenApi();

        group.MapPut("/post-close-redirect", async (
            [FromBody] UpdatePostCloseRedirectRequest req,
            HttpContext http, [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var normalized = NormalizePostCloseRedirect(req.Target);
            if (normalized is null)
                return Results.BadRequest(new { error = "Target must be 'dashboard', 'timesheet', or 'view:<guid>'." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_preferences (user_id, pref_key, pref_value)
                VALUES (@userId, 'nav:post-close-redirect', @value)
                ON CONFLICT (user_id, pref_key) DO UPDATE
                    SET pref_value = @value, updated_utc = now()
                """,
                new { userId, value = normalized }, cancellationToken: ct));

            return Results.Ok(new PostCloseRedirectPreference(normalized));
        }).WithName("UpdatePostCloseRedirect").WithOpenApi();

        return app;
    }

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

    /// Returns the trimmed lowercase form when input is 'light' or 'dark',
    /// otherwise null. Used both at write time (reject bad input) and at
    /// read time (silently coerce a hand-edited DB row).
    private static string? NormalizeTheme(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        return v is "light" or "dark" ? v : null;
    }

    public sealed record ColumnPreference(string Columns, string Source);
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
