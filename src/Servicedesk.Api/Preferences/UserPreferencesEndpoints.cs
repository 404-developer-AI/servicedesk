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

        return app;
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
}
