using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Sla;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Sla;

namespace Servicedesk.Api.Sla;

/// v0.1.1 SLA admin + read endpoints. Admin-only for CRUD; agents can read
/// per-ticket state via the ticket detail enrichment already handled by the
/// ticket endpoints. Business hours and holidays are global (not per-queue).
public static class SlaEndpoints
{
    public static IEndpointRouteBuilder MapSlaEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/sla")
            .WithTags("Sla")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        MapBusinessHoursEndpoints(admin);
        MapHolidayEndpoints(admin);
        MapPolicyEndpoints(admin);
        MapLogEndpoints(admin);
        MapDashboardEndpoints(admin);

        // Ticket-state lookup is allowed for any agent who can see the ticket.
        app.MapGet("/api/sla/tickets/{ticketId:guid}", async (Guid ticketId, ISlaRepository repo, CancellationToken ct) =>
        {
            var state = await repo.GetStateAsync(ticketId, ct);
            return state is null ? Results.NotFound() : Results.Ok(state);
        })
        .WithTags("Sla")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetTicketSlaState")
        .WithOpenApi();

        return app;
    }

    // ---------- Business hours ----------
    private static void MapBusinessHoursEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/business-hours", async (ISlaRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListSchemasAsync(ct)));

        admin.MapPost("/business-hours", async (
            [FromBody] BusinessHoursInput req, ISlaRepository repo, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            var resolvedTz = req.Timezone ?? "Europe/Brussels";
            if (!IsKnownTimezone(resolvedTz))
                return Results.BadRequest(new { error = $"Timezone '{resolvedTz}' is not a recognised IANA id." });
            var id = await repo.CreateSchemaAsync(req.Name.Trim(), resolvedTz, req.CountryCode ?? "", req.IsDefault, ct);
            if (req.Slots is not null) await repo.SetSlotsAsync(id, req.Slots.Select(s => (s.DayOfWeek, s.StartMinute, s.EndMinute)).ToList(), ct);
            await LogAsync(audit, http, "sla.business_hours.created", id.ToString(), req);
            return Results.Created($"/api/sla/business-hours/{id}", await repo.GetSchemaAsync(id, ct));
        });

        admin.MapPut("/business-hours/{id:guid}", async (
            Guid id, [FromBody] BusinessHoursInput req, ISlaRepository repo, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await repo.GetSchemaAsync(id, ct);
            if (existing is null) return Results.NotFound();
            var resolvedTz = req.Timezone ?? existing.Timezone;
            if (!IsKnownTimezone(resolvedTz))
                return Results.BadRequest(new { error = $"Timezone '{resolvedTz}' is not a recognised IANA id." });
            await repo.UpdateSchemaAsync(id, req.Name.Trim(), resolvedTz, req.CountryCode ?? existing.CountryCode, req.IsDefault, ct);
            if (req.Slots is not null) await repo.SetSlotsAsync(id, req.Slots.Select(s => (s.DayOfWeek, s.StartMinute, s.EndMinute)).ToList(), ct);
            await LogAsync(audit, http, "sla.business_hours.updated", id.ToString(), req);
            return Results.Ok(await repo.GetSchemaAsync(id, ct));
        });

        admin.MapDelete("/business-hours/{id:guid}", async (
            Guid id, ISlaRepository repo, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            await repo.DeleteSchemaAsync(id, ct);
            await LogAsync(audit, http, "sla.business_hours.deleted", id.ToString(), null);
            return Results.NoContent();
        });
    }

    private static bool IsKnownTimezone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    // ---------- Holidays ----------
    private static void MapHolidayEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/business-hours/{schemaId:guid}/holidays", async (
            Guid schemaId, int? year, ISlaRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListHolidaysAsync(schemaId, year, ct)));

        admin.MapPost("/business-hours/{schemaId:guid}/holidays", async (
            Guid schemaId, [FromBody] HolidayInput req, ISlaRepository repo, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!DateOnly.TryParse(req.Date, out var date))
                return Results.BadRequest(new { error = "Date must be ISO yyyy-MM-dd." });
            await repo.AddHolidayAsync(schemaId, date, req.Name ?? "", "manual", req.CountryCode ?? "", ct);
            await LogAsync(audit, http, "sla.holiday.added", $"{schemaId}:{req.Date}", req);
            return Results.NoContent();
        });

        admin.MapDelete("/holidays/{id:long}", async (
            long id, ISlaRepository repo, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            await repo.DeleteHolidayAsync(id, ct);
            await LogAsync(audit, http, "sla.holiday.deleted", id.ToString(), null);
            return Results.NoContent();
        });

        admin.MapPost("/business-hours/{schemaId:guid}/holidays/sync", async (
            Guid schemaId, [FromBody] SyncHolidaysRequest req, IHolidaySyncService sync, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.CountryCode)) return Results.BadRequest(new { error = "countryCode is required." });
            var year = req.Year ?? DateTime.UtcNow.Year;
            await sync.SyncAsync(schemaId, req.CountryCode, year, ct);
            await LogAsync(audit, http, "sla.holiday.sync", $"{schemaId}:{req.CountryCode}:{year}", req);
            return Results.NoContent();
        });

        admin.MapGet("/holidays/countries", async (IHolidaySyncService sync, CancellationToken ct) =>
        {
            try { return Results.Ok(await sync.ListCountriesAsync(ct)); }
            catch { return Results.Ok(Array.Empty<NagerCountry>()); }
        });
    }

    // ---------- Policies ----------
    private static void MapPolicyEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/policies", async (ISlaRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListPoliciesAsync(ct)));

        admin.MapPut("/policies", async (
            [FromBody] PolicyInput req, ISlaRepository repo, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.PriorityId == Guid.Empty) return Results.BadRequest(new { error = "priorityId is required." });
            // Normalize 0 → null (means "not tracked").
            var frMin = req.FirstResponseMinutes is > 0 ? req.FirstResponseMinutes : null;
            var resMin = req.ResolutionMinutes is > 0 ? req.ResolutionMinutes : null;
            if (frMin is null && resMin is null)
                return Results.BadRequest(new { error = "At least one target (first response or resolution) is required." });
            var id = await repo.UpsertPolicyAsync(req.QueueId, req.PriorityId, req.BusinessHoursSchemaId, frMin, resMin, req.PauseOnPending, ct);
            await LogAsync(audit, http, "sla.policy.upserted", id.ToString(), req);
            return Results.Ok(await repo.GetPolicyAsync(id, ct));
        });

        admin.MapDelete("/policies/{id:guid}", async (
            Guid id, ISlaRepository repo, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            await repo.DeletePolicyAsync(id, ct);
            await LogAsync(audit, http, "sla.policy.deleted", id.ToString(), null);
            return Results.NoContent();
        });
    }

    // ---------- Log ----------
    private static void MapLogEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/log", async (
            Guid? queueId, Guid? priorityId, Guid? statusId, bool? breachedOnly,
            DateTime? fromUtc, DateTime? toUtc, string? search, long? cursorNumber, int? limit,
            ISlaRepository repo, CancellationToken ct) =>
        {
            var filter = new SlaLogFilter(queueId, priorityId, statusId, breachedOnly, fromUtc, toUtc, search,
                Math.Clamp(limit ?? 50, 1, 200), cursorNumber);
            var rows = await repo.QueryLogAsync(filter, ct);
            var next = rows.Count == filter.Limit ? rows[^1].Number : (long?)null;
            return Results.Ok(new { items = rows, nextCursor = next });
        });
    }

    // ---------- Dashboard ----------
    private static void MapDashboardEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/dashboard/avg-pickup", async (
            int? days, ISlaRepository repo, CancellationToken ct) =>
        {
            var range = Math.Clamp(days ?? 7, 1, 365);
            var rows = await repo.AvgPickupPerQueueAsync(range, ct);
            return Results.Ok(new { days = range, items = rows });
        });
    }

    private static async Task LogAsync(IAuditLogger audit, HttpContext http, string eventType, string? target, object? payload)
    {
        var actor = http.User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown";
        var role = http.User.FindFirst(ClaimTypes.Role)?.Value ?? "unknown";
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target,
            Payload: payload ?? new { }));
    }

    // ---------- DTOs ----------
    public sealed record BusinessHoursInput(
        [property: Required] string Name,
        string? Timezone,
        string? CountryCode,
        bool IsDefault,
        SlotInput[]? Slots);

    public sealed record SlotInput(int DayOfWeek, int StartMinute, int EndMinute);

    public sealed record HolidayInput([property: Required] string Date, string? Name, string? CountryCode);
    public sealed record SyncHolidaysRequest([property: Required] string CountryCode, int? Year);

    public sealed record PolicyInput(
        Guid? QueueId,
        [property: Required] Guid PriorityId,
        [property: Required] Guid BusinessHoursSchemaId,
        int? FirstResponseMinutes,
        int? ResolutionMinutes,
        bool PauseOnPending);
}
