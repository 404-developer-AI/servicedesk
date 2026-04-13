using System.Security.Claims;
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

        return app;
    }

    public sealed record ColumnPreference(string Columns, string Source);
    public sealed record UpdateColumnPreferenceRequest(string Columns);
    public sealed record WorkspaceEntry(string Key, string Value);
    public sealed record SaveWorkspaceRequest(WorkspaceEntry[] Entries);
}
