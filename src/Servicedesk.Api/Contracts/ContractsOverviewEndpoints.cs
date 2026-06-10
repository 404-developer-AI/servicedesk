using System.Text.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Contracts;

/// Agent-facing endpoints for the Contracts overview module (Contracts →
/// Contracts overview): the mirrored Adsolut contracts list, a per-contract
/// detail (with article lines), a manual "Sync contracts" trigger (incremental —
/// the worker only pulls what changed) and a per-row resync.
///
/// Gated by the per-user <c>contracts_enabled</c> flag: the route policy is
/// RequireAgent (role gate) and every handler additionally verifies the flag
/// in-handler (Forbid on miss), so the gate is the real security boundary — not
/// just UI visibility — consistent with the Contract Articles module. The
/// admin's display status filter (DISPLAY-ONLY — the mirror holds every status)
/// is applied to the overview and global search.
public static class ContractsOverviewEndpoints
{
    public static IEndpointRouteBuilder MapContractsOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/overview")
            .WithTags("Contracts")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("", ListContracts).WithName("ListAdsolutContracts").WithOpenApi();
        group.MapGet("/{id:guid}", GetContractDetail).WithName("GetAdsolutContractDetail").WithOpenApi();
        group.MapPost("/sync", TriggerSync).WithName("TriggerAdsolutContractsSync").WithOpenApi();
        group.MapPost("/{id:guid}/resync", ResyncContract).WithName("ResyncAdsolutContract").WithOpenApi();

        return group;
    }

    /// Resolve the caller + verify the contracts_enabled flag. Returns an
    /// IResult (Unauthorized/Forbid) to short-circuit on miss, else null.
    private static async Task<IResult?> RequireContractsFlagAsync(
        HttpContext http, IUserService users, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        if (userId == Guid.Empty) return Results.Unauthorized();
        if (!await users.GetContractsEnabledAsync(userId, ct)) return Results.Forbid();
        return null;
    }

    /// Resolve the admin's display status filter (DISPLAY-ONLY — the mirror
    /// holds every status). Empty = show all.
    private static async Task<IReadOnlyList<string>> ResolveStatusFilterAsync(
        ISettingsService settings, CancellationToken ct)
    {
        var raw = await settings.GetAsync<string>(SettingKeys.Adsolut.ErpContractsStatusFilter, ct) ?? string.Empty;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// Parse the contract status → colour map setting (JSON). Returns an empty
    /// map on empty/invalid JSON so the UI just falls back to a neutral pill.
    /// Public so the admin integration-page state endpoint can reuse it.
    public static async Task<IReadOnlyDictionary<string, string>> ResolveStatusColorsAsync(
        ISettingsService settings, CancellationToken ct)
    {
        var raw = (await settings.GetAsync<string>(SettingKeys.Adsolut.ErpContractsStatusColors, ct) ?? string.Empty).Trim();
        if (raw.Length == 0) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static async Task<IResult> ListContracts(
        string? search,
        int? page,
        int? pageSize,
        string? sort,
        string? dir,
        HttpContext http,
        IUserService users,
        IAdsolutContractRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var deny = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var statusFilter = await ResolveStatusFilterAsync(settings, ct);
        var statusColors = await ResolveStatusColorsAsync(settings, ct);
        var result = await repo.ListAsync(search, page ?? 1, pageSize ?? 50, sort, dir, statusFilter, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(ToHeaderDto),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
            statusFilter,
            statusColors,
        });
    }

    private static async Task<IResult> GetContractDetail(
        Guid id,
        HttpContext http,
        IUserService users,
        IAdsolutContractRepository repo,
        CancellationToken ct)
    {
        var deny = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var detail = await repo.GetDetailAsync(id, ct);
        if (detail is null) return Results.NotFound(new { error = "not_found" });
        return Results.Ok(ToDetailDto(detail));
    }

    private static async Task<IResult> TriggerSync(
        HttpContext http,
        IUserService users,
        IAdsolutContractsSyncSignal signal,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var deny = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpContractsEnabled, ct);
        if (!enabled)
        {
            return Results.BadRequest(new { error = "disabled", message = "Enable 'Pull contracts' first." });
        }
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_contracts.sync_requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    private static async Task<IResult> ResyncContract(
        Guid id,
        HttpContext http,
        IUserService users,
        IAdsolutConnectionStore connections,
        IAdsolutContractsClient client,
        IAdsolutContractRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var deny = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Adsolut is not connected or no dossier is active." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        AdsolutContract? full;
        try
        {
            full = await client.GetByIdAsync(administrationId, id, ct);
        }
        catch (AdsolutApiException ex)
        {
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        if (full is null)
        {
            return Results.NotFound(new { error = "not_found_upstream", message = "Adsolut returned no contract for this id." });
        }

        await repo.UpsertAsync(full, ct);

        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_contracts.resync_one",
            Actor: actor,
            ActorRole: role,
            Target: id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { contractId = id }), ct);

        var detail = await repo.GetDetailAsync(id, ct);
        return detail is null ? Results.Ok(new { ok = true }) : Results.Ok(ToDetailDto(detail));
    }

    // ---- DTO mapping ----------------------------------------------------

    private static object ToHeaderDto(AdsolutContractRow c) => new
    {
        id = c.Id,
        docNr = c.DocNr,
        customerAdsolutId = c.CustomerAdsolutId,
        customerName = c.CustomerName,
        companyId = c.CompanyId,
        companyName = c.CompanyName,
        relationCode = c.RelationCode,
        stateCode = c.StateCode,
        stateDescription = c.StateDescription,
        docDate = c.DocDate,
        startDate = c.StartDate,
        stopDate = c.StopDate,
        endDate = c.EndDate,
        description = c.Description,
        memo = c.Memo,
        periodicityCode = c.PeriodicityCode,
        periodicityLabel = c.PeriodicityLabel,
        invoicingPeriodicityCode = c.InvoicingPeriodicityCode,
        invoicingPeriodicityLabel = c.InvoicingPeriodicityLabel,
        numberOfTerms = c.NumberOfTerms,
        totalExclVat = c.TotalExclVat,
        totalVat = c.TotalVat,
        totalInclVat = c.TotalInclVat,
        adsolutCreatedUtc = c.AdsolutCreatedUtc,
        adsolutLastModified = c.AdsolutLastModified,
        syncedUtc = c.SyncedUtc,
    };

    private static object ToDetailDto(AdsolutContractDetail d) => new
    {
        header = ToHeaderDto(d.Header),
        lines = d.Lines.Select(l => new
        {
            id = l.Id,
            lineNr = l.LineNr,
            name = l.Name,
            description = l.Description,
            quantity = l.Quantity,
            grossUnitPrice = l.GrossUnitPrice,
            discount1 = l.Discount1,
            discount2 = l.Discount2,
            unitPrice = l.UnitPrice,
            unitPriceIncl = l.UnitPriceIncl,
            startDate = l.StartDate,
            endDate = l.EndDate,
        }),
    };
}
