using System.Globalization;
using System.Text;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Trmm;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for Assets → Remote Desktop (v0.1.10). Agent + Admin, under
/// <c>/api/assets/remote-desktop</c>. The list groups the mirrored RDS
/// check results per TRMM client, hides servers that reported "not an
/// RDS host", and parks failed / missing / pending checks in a separate
/// attention list. Per-client notes are a small thread with author
/// attribution; edit/delete is author-or-admin, and an admin acting on
/// someone else's note lands in the security audit trail.
public static class RemoteDesktopEndpoints
{
    /// Upper bound on a note body. Generous for a free-text remark, small
    /// enough that a pasted log cannot balloon the table.
    private const int MaxNoteLength = 4000;

    private static readonly HashSet<string> AttentionStatuses = new(StringComparer.Ordinal)
    {
        RdsStatus.Failed, RdsStatus.NoCheck, RdsStatus.Pending, RdsStatus.Error,
    };

    public static IEndpointRouteBuilder MapRemoteDesktopEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets/remote-desktop")
            .WithTags("Assets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", GetOverview).WithName("GetRemoteDesktopOverview").WithOpenApi();
        group.MapGet("/export.csv", ExportCsv).WithName("ExportRemoteDesktopCsv").WithOpenApi();
        group.MapGet("/clients/{trmmClientId:long}/notes", ListNotes).WithName("ListRemoteDesktopClientNotes").WithOpenApi();
        group.MapPost("/clients/{trmmClientId:long}/notes", AddNote).WithName("AddRemoteDesktopClientNote").WithOpenApi();
        group.MapPut("/notes/{noteId:guid}", UpdateNote).WithName("UpdateRemoteDesktopClientNote").WithOpenApi();
        group.MapDelete("/notes/{noteId:guid}", DeleteNote).WithName("DeleteRemoteDesktopClientNote").WithOpenApi();

        return app;
    }

    // ---- overview --------------------------------------------------------

    private static async Task<IResult> GetOverview(
        IRemoteDesktopRepository repo,
        ISettingsService settings,
        string? search,
        CancellationToken ct)
    {
        var trmmEnabled = await settings.GetAsync<bool>(SettingKeys.Trmm.Enabled, ct);
        var rdsEnabled = await settings.GetAsync<bool>(SettingKeys.Trmm.RdsCheckEnabled, ct);
        var interval = await settings.GetAsync<int>(SettingKeys.Trmm.RdsSyncIntervalMinutes, ct);
        var state = await repo.GetRdsSyncStateAsync(ct);
        var rows = await repo.ListAsync(search, ct);
        var noteSummaries = (await repo.ListNoteSummariesAsync(ct))
            .ToDictionary(n => n.TrmmClientId);

        var rdsRows = rows.Where(r => r.Status == RdsStatus.Rds).ToList();
        var attention = rows.Where(r => r.Status is not null && AttentionStatuses.Contains(r.Status)).ToList();
        var hiddenNotRds = rows.Count(r => r.Status == RdsStatus.NotRds);
        var unchecked_ = rows.Count(r => r.Status is null);

        var clients = rdsRows
            .GroupBy(r => r.TrmmClientId)
            .Select(g =>
            {
                var first = g.First();
                noteSummaries.TryGetValue(g.Key, out var notes);
                return new
                {
                    trmmClientId = g.Key,
                    name = first.ClientName,
                    displayName = StripCode(first.ClientName),
                    code = first.ClientCode,
                    companyId = first.CompanyId,
                    companyName = first.CompanyName,
                    noteCount = notes?.NoteCount ?? 0,
                    lastNoteUtc = notes?.LastNoteUtc,
                    servers = g.Select(ToServerDto).ToList(),
                };
            })
            .OrderBy(c => c.code ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.displayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(new
        {
            enabled = trmmEnabled && rdsEnabled,
            trmmEnabled,
            rdsCheckEnabled = rdsEnabled,
            syncIntervalMinutes = interval,
            lastRdsSyncUtc = state.LastRdsSyncUtc,
            lastRdsStatus = state.LastRdsStatus,
            lastRdsError = state.LastRdsError,
            summary = new
            {
                clients = clients.Count,
                rdsServers = rdsRows.Count,
                attention = attention.Count,
                hiddenNotRds,
                @unchecked = unchecked_,
                scopedAgents = rows.Count,
            },
            clients,
            attention = attention
                .OrderBy(r => r.ClientCode ?? "￿", StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ClientName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Hostname, StringComparer.OrdinalIgnoreCase)
                .Select(ToServerDto)
                .ToList(),
        });
    }

    private static object ToServerDto(RemoteDesktopRow r) => new
    {
        id = r.Id,
        trmmAgentId = r.TrmmAgentId,
        hostname = r.Hostname,
        agentType = r.AgentType,
        osName = r.OsName,
        osFamily = r.OsFamily,
        osBuild = r.OsBuild,
        online = r.Online,
        lastSeenUtc = r.LastSeenUtc,
        siteName = r.SiteName,
        trmmClientId = r.TrmmClientId,
        clientName = r.ClientName,
        clientDisplayName = StripCode(r.ClientName),
        clientCode = r.ClientCode,
        companyId = r.CompanyId,
        companyName = r.CompanyName,
        status = r.Status,
        checkStatus = r.CheckStatus,
        retcode = r.Retcode,
        stdout = r.Stdout,
        stderr = r.Stderr,
        // Unzoned: the server's own clock as printed by the script.
        lastLoginLocal = r.LastLoginLocal?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        lastLoginKind = r.LastLoginKind,
        lastLoginUser = r.LastLoginUser,
        lastRunUtc = r.LastRunUtc,
        fetchedUtc = r.FetchedUtc,
        fetchError = r.FetchError,
    };

    // ---- CSV export ------------------------------------------------------

    private static async Task<IResult> ExportCsv(
        IRemoteDesktopRepository repo,
        string? search,
        CancellationToken ct)
    {
        var rows = await repo.ListAsync(search, ct);
        var exported = rows
            .Where(r => r.Status == RdsStatus.Rds || (r.Status is not null && AttentionStatuses.Contains(r.Status)))
            .OrderBy(r => r.Status == RdsStatus.Rds ? 0 : 1)
            .ThenBy(r => r.ClientCode ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ClientName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Hostname, StringComparer.OrdinalIgnoreCase);

        var csv = BuildCsv(exported);
        var fileName = $"remote-desktop_{DateTime.UtcNow:yyyy-MM-dd}.csv";
        return Results.File(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(),
            "text/csv; charset=utf-8",
            fileName);
    }

    internal static string BuildCsv(IEnumerable<RemoteDesktopRow> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        // Excel honours a leading "sep=" directive regardless of the
        // user's list-separator locale (Belgian Excel defaults to ';'),
        // so the file opens in columns on a double-click. Other readers
        // treat it as a one-cell first row.
        sb.AppendLine("sep=,");
        sb.AppendLine("client_code,client_name,company,hostname,os,site,status,last_login,last_login_type,last_login_user,last_check_run_utc,exit_code,output,error");
        foreach (var r in rows)
        {
            sb.Append(CsvQuote(r.ClientCode ?? "")).Append(',')
              .Append(CsvQuote(StripCode(r.ClientName))).Append(',')
              .Append(CsvQuote(r.CompanyName ?? "")).Append(',')
              .Append(CsvQuote(r.Hostname)).Append(',')
              .Append(CsvQuote(r.OsName ?? r.OsFamily ?? "")).Append(',')
              .Append(CsvQuote(r.SiteName)).Append(',')
              .Append(CsvQuote(StatusLabel(r.Status))).Append(',')
              .Append(CsvQuote(r.LastLoginLocal?.ToString("yyyy-MM-dd HH:mm:ss", inv) ?? "")).Append(',')
              .Append(CsvQuote(r.LastLoginKind ?? "")).Append(',')
              .Append(CsvQuote(r.LastLoginUser ?? "")).Append(',')
              .Append(CsvQuote(r.LastRunUtc?.ToString("yyyy-MM-dd HH:mm:ss", inv) ?? "")).Append(',')
              .Append(CsvQuote(r.Retcode?.ToString(inv) ?? "")).Append(',')
              .Append(CsvQuote(r.Stdout ?? "")).Append(',')
              .Append(CsvQuote(r.FetchError ?? r.Stderr ?? ""))
              .AppendLine();
        }
        return sb.ToString();
    }

    internal static string StatusLabel(string? status) => status switch
    {
        RdsStatus.Rds => "Remote Desktop",
        RdsStatus.NotRds => "No Remote Desktop",
        RdsStatus.Failed => "Check failed",
        RdsStatus.NoCheck => "No check",
        RdsStatus.Pending => "No result yet",
        RdsStatus.Error => "Unreachable",
        _ => "Not checked",
    };

    private static string CsvQuote(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// TRMM display names carry the "[CODE] " prefix; the tab shows the
    /// code as its own badge so the name is rendered without it.
    internal static string StripCode(string name)
    {
        var t = name.TrimStart();
        if (t.Length > 0 && t[0] == '[')
        {
            var close = t.IndexOf(']');
            if (close > 0)
            {
                var rest = t[(close + 1)..].Trim();
                if (rest.Length > 0) return rest;
            }
        }
        return name.Trim();
    }

    // ---- notes -----------------------------------------------------------

    private static async Task<IResult> ListNotes(
        long trmmClientId, IRemoteDesktopRepository repo, CancellationToken ct)
    {
        if (!await repo.ClientExistsAsync(trmmClientId, ct))
            return Results.NotFound(new { error = "client_not_found" });
        var notes = await repo.ListNotesAsync(trmmClientId, ct);
        return Results.Ok(new { items = notes.Select(ToNoteDto) });
    }

    public sealed record NoteBodyRequest(string? Body);

    private static async Task<IResult> AddNote(
        long trmmClientId,
        NoteBodyRequest req,
        HttpContext http,
        IRemoteDesktopRepository repo,
        ITrmmSyncNotifier notifier,
        CancellationToken ct)
    {
        var body = NormalizeBody(req?.Body);
        if (body is null)
            return Results.BadRequest(new { error = "invalid_body", message = $"Note must be 1–{MaxNoteLength} characters." });
        if (!await repo.ClientExistsAsync(trmmClientId, ct))
            return Results.NotFound(new { error = "client_not_found" });

        var userId = ActorContext.GetUserId(http);
        var note = await repo.AddNoteAsync(trmmClientId, userId, body, ct);
        await NotifyNotesAsync(notifier, trmmClientId, ct);
        return Results.Ok(ToNoteDto(note));
    }

    private static async Task<IResult> UpdateNote(
        Guid noteId,
        NoteBodyRequest req,
        HttpContext http,
        IRemoteDesktopRepository repo,
        ITrmmSyncNotifier notifier,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var body = NormalizeBody(req?.Body);
        if (body is null)
            return Results.BadRequest(new { error = "invalid_body", message = $"Note must be 1–{MaxNoteLength} characters." });

        var existing = await repo.GetNoteAsync(noteId, ct);
        if (existing is null) return Results.NotFound(new { error = "note_not_found" });

        var (actor, role) = ActorContext.Resolve(http);
        var userId = ActorContext.GetUserId(http);
        var isOwn = existing.AuthorId == userId;
        var isAdmin = string.Equals(role, "Admin", StringComparison.Ordinal);
        if (!isOwn && !isAdmin) return Results.Forbid();

        var updated = await repo.UpdateNoteAsync(noteId, body, ct);
        if (updated is null) return Results.NotFound(new { error = "note_not_found" });

        if (!isOwn)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: TrmmEventTypes.SecurityClientNoteOverridden,
                Actor: actor,
                ActorRole: role,
                Target: noteId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { action = "edit", trmmClientId = existing.TrmmClientId, originalAuthor = existing.AuthorEmail }), ct);
        }

        await NotifyNotesAsync(notifier, existing.TrmmClientId, ct);
        return Results.Ok(ToNoteDto(updated));
    }

    private static async Task<IResult> DeleteNote(
        Guid noteId,
        HttpContext http,
        IRemoteDesktopRepository repo,
        ITrmmSyncNotifier notifier,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var existing = await repo.GetNoteAsync(noteId, ct);
        if (existing is null) return Results.NotFound(new { error = "note_not_found" });

        var (actor, role) = ActorContext.Resolve(http);
        var userId = ActorContext.GetUserId(http);
        var isOwn = existing.AuthorId == userId;
        var isAdmin = string.Equals(role, "Admin", StringComparison.Ordinal);
        if (!isOwn && !isAdmin) return Results.Forbid();

        if (!await repo.DeleteNoteAsync(noteId, ct))
            return Results.NotFound(new { error = "note_not_found" });

        if (!isOwn)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: TrmmEventTypes.SecurityClientNoteOverridden,
                Actor: actor,
                ActorRole: role,
                Target: noteId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { action = "delete", trmmClientId = existing.TrmmClientId, originalAuthor = existing.AuthorEmail }), ct);
        }

        await NotifyNotesAsync(notifier, existing.TrmmClientId, ct);
        return Results.NoContent();
    }

    private static object ToNoteDto(ClientNote n) => new
    {
        id = n.Id,
        trmmClientId = n.TrmmClientId,
        body = n.Body,
        authorId = n.AuthorId,
        authorEmail = n.AuthorEmail,
        createdUtc = n.CreatedUtc,
        updatedUtc = n.UpdatedUtc,
        edited = n.UpdatedUtc > n.CreatedUtc.AddSeconds(1),
    };

    private static string? NormalizeBody(string? raw)
    {
        var body = (raw ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (body.Length == 0 || body.Length > MaxNoteLength) return null;
        return body;
    }

    private static async Task NotifyNotesAsync(ITrmmSyncNotifier notifier, long trmmClientId, CancellationToken ct)
    {
        try
        {
            await notifier.NotifyRemoteDesktopChangedAsync(new { kind = "client-notes", trmmClientId }, ct);
        }
        catch
        {
            // Best-effort push; the write is already committed.
        }
    }
}
