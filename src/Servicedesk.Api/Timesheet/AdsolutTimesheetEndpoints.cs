using System.Globalization;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Timesheet;

/// Agent-facing read endpoints for the Timesheet → Adsolut tab: the mirrored
/// sales-receipts (verkoopbonnen) list, a per-receipt detail (product +
/// performance lines for the expand view) and a per-row manual resync that
/// refreshes one receipt straight from Adsolut. The tab itself is gated by
/// the per-user `adsolut_timesheet_enabled` flag in the SPA; consistent with
/// the other timesheet endpoints, the backend allows any authenticated agent
/// (the flag controls feature visibility, not security authorization — the
/// data is install-wide company sales data, not per-user rows).
public static class AdsolutTimesheetEndpoints
{
    public static IEndpointRouteBuilder MapAdsolutTimesheetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/timesheet/adsolut")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/receipts", GetReceipts).WithName("ListAdsolutSalesReceipts").WithOpenApi();
        group.MapGet("/receipts/{id:guid}", GetReceiptDetail).WithName("GetAdsolutSalesReceiptDetail").WithOpenApi();
        group.MapGet("/receipts/{id:guid}/hours", GetReceiptHours).WithName("GetAdsolutSalesReceiptHours").WithOpenApi();
        group.MapPost("/receipts/{id:guid}/resync", ResyncReceipt).WithName("ResyncAdsolutSalesReceipt").WithOpenApi();

        return group;
    }

    private static async Task<IResult> GetReceipts(
        string? search,
        int? page,
        int? pageSize,
        string? sort,
        string? dir,
        IAdsolutSalesReceiptRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var rate = await ReadHourlyRateAsync(settings, ct);
        var result = await repo.ListAsync(search, page ?? 1, pageSize ?? 50, sort, dir, rate, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(r => ToHeaderDto(r, ComputeBruto(rate, r.TotalMinutes))),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }

    private static async Task<IResult> GetReceiptDetail(
        Guid id,
        IAdsolutSalesReceiptRepository repo,
        CancellationToken ct)
    {
        var detail = await repo.GetDetailAsync(id, ct);
        if (detail is null) return Results.NotFound(new { error = "not_found" });
        return Results.Ok(ToDetailDto(detail));
    }

    private static async Task<IResult> GetReceiptHours(
        Guid id,
        IAdsolutSalesReceiptRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var rows = await repo.GetHoursBreakdownAsync(id, ct);
        var rate = await ReadHourlyRateAsync(settings, ct);
        var totalMinutes = rows.Sum(r => r.Minutes);
        return Results.Ok(new
        {
            totalMinutes,
            totalBrutoPrice = ComputeBruto(rate, totalMinutes),
            byTask = rows.Select(r => new
            {
                taskName = r.TaskName,
                isAbsence = r.IsAbsence,
                minutes = r.Minutes,
                brutoPrice = ComputeBruto(rate, r.Minutes),
            }),
        });
    }

    /// Read the gross hourly rate tolerantly — accepts "75", "75.50" or the
    /// Belgian "75,50". Returns 0 when unset/unparseable (Bruto Price stays
    /// blank). Never throws (GetAsync&lt;decimal&gt; would on a comma).
    private static async Task<decimal> ReadHourlyRateAsync(ISettingsService settings, CancellationToken ct)
    {
        var raw = (await settings.GetAsync<string>(SettingKeys.Timesheet.HourlyRate, ct) ?? string.Empty)
            .Trim()
            .Replace(',', '.');
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : 0m;
    }

    /// rate × minutes/60, rounded to cents. Null when there is no rate or no
    /// minutes, so the Bruto Price column/pill stays blank in that case.
    private static decimal? ComputeBruto(decimal rate, int? minutes)
    {
        if (rate <= 0 || minutes is null || minutes <= 0) return null;
        return Math.Round(rate * minutes.Value / 60m, 2, MidpointRounding.AwayFromZero);
    }

    private static async Task<IResult> ResyncReceipt(
        Guid id,
        HttpContext http,
        IAdsolutConnectionStore connections,
        IAdsolutSalesReceiptsClient client,
        IAdsolutSalesReceiptRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Adsolut is not connected or no dossier is active." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        AdsolutSalesReceipt? full;
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
            return Results.NotFound(new { error = "not_found_upstream", message = "Adsolut returned no receipt for this id." });
        }

        var total = AdsolutSalesReceiptsSyncWorker.ComputeTotalExclVat(full);
        await repo.UpsertAsync(full, total, ct);

        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_sales_receipts.resync_one",
            Actor: actor,
            ActorRole: role,
            Target: id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { receiptId = id }), ct);

        var detail = await repo.GetDetailAsync(id, ct);
        return detail is null
            ? Results.Ok(new { ok = true })
            : Results.Ok(ToDetailDto(detail));
    }

    private static object ToHeaderDto(AdsolutSalesReceiptRow r, decimal? brutoPrice) => new
    {
        id = r.Id,
        docNr = r.DocNr,
        bookCode = r.BookCode,
        customerName = r.CustomerName,
        customerCode = r.CustomerCode,
        stateCode = r.StateCode,
        stateDescription = r.StateDescription,
        salesReceiptDate = r.SalesReceiptDate,
        description = r.Description,
        employeeName = r.EmployeeName,
        employeeCode = r.EmployeeCode,
        representativeName = r.RepresentativeName,
        totalExclVat = r.TotalExclVat,
        currencyIso = r.CurrencyIso,
        adsolutCreatedUtc = r.AdsolutCreatedUtc,
        adsolutLastModified = r.AdsolutLastModified,
        syncedUtc = r.SyncedUtc,
        ticketNumber = r.TicketNumber,
        totalMinutes = r.TotalMinutes,
        brutoPrice,
    };

    private static object ToDetailDto(AdsolutSalesReceiptDetail d) => new
    {
        // The expand view doesn't surface Bruto Price (that's the list column),
        // so the header here carries a null price — the list endpoint computes it.
        header = ToHeaderDto(d.Header, null),
        lines = d.Lines.Select(l => new
        {
            id = l.Id,
            lineNr = l.LineNr,
            productCode = l.ProductCode,
            name = l.Name,
            description = l.Description,
            quantity = l.Quantity,
            unitCode = l.UnitCode,
            unitPrice = l.UnitPrice,
            totalExclVat = l.TotalExclVat,
            totalInclVat = l.TotalInclVat,
            vatCode = l.VatCode,
        }),
        performances = d.Performances.Select(p => new
        {
            id = p.Id,
            employeeCode = p.EmployeeCode,
            performanceDate = p.PerformanceDate,
            fromTime = p.FromTime,
            untilTime = p.UntilTime,
            durationMinutes = p.DurationMinutes,
            invoiceDurationMinutes = p.InvoiceDurationMinutes,
            invoiceUnitPrice = p.InvoiceUnitPrice,
            invoiceTotal = p.InvoiceTotal,
            performanceCode = p.PerformanceCode,
            description = p.Description,
        }),
    };
}
