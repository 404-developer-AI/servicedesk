using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tickets;
using Servicedesk.Infrastructure.Forms;

namespace Servicedesk.Api.Forms;

/// v0.1.52 — the agent-side forms surface. Staff only (RequireAgent): the
/// customer portal never reaches it and forms never leave the server through
/// the portal query layer (the Note they write is internal). Every route
/// resolves access through the shared mutation precheck inside
/// <see cref="ITicketFormService"/>; a form id is always resolved to its
/// ticket and checked, so it cannot be used to read across queues.
public static class TicketFormEndpoints
{
    public static IEndpointRouteBuilder MapTicketFormEndpoints(this IEndpointRouteBuilder app)
    {
        var ticket = app.MapGroup("/api/tickets/{ticketId:guid}/forms")
            .WithTags("Forms")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        ticket.MapGet("/", async (Guid ticketId, HttpContext http, ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var list = await svc.ListAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, ct);
                return Results.Ok(new { items = list.Select(MapSummary) });
            })).WithName("ListTicketForms").WithOpenApi();

        ticket.MapGet("/templates", async (Guid ticketId, HttpContext http, ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var list = await svc.ListUsableTemplatesAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, ct);
                return Results.Ok(new
                {
                    items = list.Select(t => new { id = t.Id, name = t.Name, description = t.Description, fieldCount = t.FieldCount }),
                });
            })).WithName("ListUsableFormTemplates").WithOpenApi();

        ticket.MapGet("/templates/{templateId:guid}", async (
            Guid ticketId, Guid templateId, HttpContext http, ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var t = await svc.GetUsableTemplateAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, templateId, ct);
                return Results.Ok(new { id = t.Id, name = t.Name, description = t.Description, fields = t.Definition.Fields });
            })).WithName("GetUsableFormTemplate").WithOpenApi();

        ticket.MapPost("/", async (
            Guid ticketId, [FromBody] CreateTicketFormRequest req, HttpContext http,
            ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                if (req is null || req.TemplateId == Guid.Empty)
                    return Results.BadRequest(new { error = "templateId is required.", code = FormRejectCode.Invalid });
                var result = await svc.CreateAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, req.TemplateId, req.Answers, ct);
                return Results.Created($"/api/ticket-forms/{result.Form.Summary.Id}", MapDetail(result.Form, result.EventId));
            })).WithName("CreateTicketForm").WithOpenApi();

        ticket.MapGet("/article-options", async (
            Guid ticketId, [FromQuery] string? search, [FromQuery] string? scope, HttpContext http,
            ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var contractOnly = !string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);
                var list = await svc.SearchArticlesAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, contractOnly, search, ct);
                // Code + name only: prices and contract details stay behind
                // the Contracts feature flag.
                return Results.Ok(new { items = list.Select(a => new { id = a.Id, code = a.Code, name = a.Name }) });
            })).WithName("ListTicketFormArticleOptions").WithOpenApi();

        ticket.MapGet("/contact-options", async (
            Guid ticketId, [FromQuery] string? search, HttpContext http,
            ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var list = await svc.SearchContactsAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, search, ct);
                return Results.Ok(new { items = list.Select(c => new { id = c.Id, name = c.Name, email = c.Email }) });
            })).WithName("ListTicketFormContactOptions").WithOpenApi();

        var form = app.MapGroup("/api/ticket-forms/{formId:guid}")
            .WithTags("Forms")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        form.MapGet("/", async (Guid formId, HttpContext http, ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var f = await svc.GetAsync(TicketEndpoints.ResolveMutationActor(http), formId, ct);
                return Results.Ok(MapDetail(f, null));
            })).WithName("GetTicketForm").WithOpenApi();

        form.MapGet("/versions", async (Guid formId, HttpContext http, ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var list = await svc.ListVersionsAsync(TicketEndpoints.ResolveMutationActor(http), formId, ct);
                return Results.Ok(new
                {
                    items = list.Select(v => new
                    {
                        version = v.Version, answers = v.Answers, eventId = v.EventId,
                        savedByUserId = v.SavedByUserId, savedByName = v.SavedByName, savedUtc = v.SavedUtc,
                    }),
                });
            })).WithName("ListTicketFormVersions").WithOpenApi();

        form.MapPut("/", async (
            Guid formId, [FromBody] UpdateTicketFormRequest req, HttpContext http,
            ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                if (req is null || req.ExpectedVersion < 1)
                    return Results.BadRequest(new { error = "expectedVersion is required.", code = FormRejectCode.Invalid });
                var result = await svc.UpdateAsync(TicketEndpoints.ResolveMutationActor(http), formId, req.ExpectedVersion, req.Answers, ct);
                return Results.Ok(MapDetail(result.Form, result.EventId));
            })).WithName("UpdateTicketForm").WithOpenApi();

        form.MapPost("/remove", async (
            Guid formId, [FromBody] RemoveTicketFormRequest req, HttpContext http,
            ITicketFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                await svc.DeleteAsync(TicketEndpoints.ResolveMutationActor(http), formId, req?.Reason, ct);
                return Results.NoContent();
            })).WithName("RemoveTicketForm").WithOpenApi();

        return app;
    }

    /// Maps the service's stable rejection codes to HTTP. Not-found and
    /// no-access collapse to 404 like the rest of the ticket API.
    private static async Task<IResult> Run(Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (FormRejectedException ex)
        {
            var status = ex.Code switch
            {
                FormRejectCode.NotFound => 404,
                FormRejectCode.Disabled => 404,
                FormRejectCode.Forbidden => 403,
                FormRejectCode.Invalid => 400,
                _ => 409,
            };
            return Results.Json(new { error = ex.Message, code = ex.Code, fieldId = ex.FieldId }, statusCode: status);
        }
    }

    internal static object MapSummary(TicketFormSummary s) => new
    {
        id = s.Id,
        ticketId = s.TicketId,
        name = s.Name,
        version = s.Version,
        createdByUserId = s.CreatedByUserId,
        createdByName = s.CreatedByName,
        createdUtc = s.CreatedUtc,
        updatedByUserId = s.UpdatedByUserId,
        updatedByName = s.UpdatedByName,
        updatedUtc = s.UpdatedUtc,
    };

    private static object MapDetail(TicketFormDetail f, long? eventId) => new
    {
        id = f.Summary.Id,
        ticketId = f.Summary.TicketId,
        templateId = f.TemplateId,
        name = f.Summary.Name,
        version = f.Summary.Version,
        createdByUserId = f.Summary.CreatedByUserId,
        createdByName = f.Summary.CreatedByName,
        createdUtc = f.Summary.CreatedUtc,
        updatedByUserId = f.Summary.UpdatedByUserId,
        updatedByName = f.Summary.UpdatedByName,
        updatedUtc = f.Summary.UpdatedUtc,
        fields = f.Snapshot.Fields,
        answers = f.Answers,
        eventId,
    };

    public sealed record CreateTicketFormRequest(Guid TemplateId, JsonObject? Answers);
    public sealed record UpdateTicketFormRequest(int ExpectedVersion, JsonObject? Answers);
    public sealed record RemoveTicketFormRequest(string? Reason);
}
