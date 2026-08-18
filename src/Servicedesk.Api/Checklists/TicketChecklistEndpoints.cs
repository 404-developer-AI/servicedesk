using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tickets;
using Servicedesk.Infrastructure.Checklists;

namespace Servicedesk.Api.Checklists;

/// v0.0.103 — the agent-side checklist surface on a ticket. Every route
/// resolves access through the shared mutation precheck (ticket exists +
/// queue access) inside <see cref="ITicketChecklistService"/>; customers
/// never reach it (RequireAgent). Item ids are always checked against the
/// ticket in the route so an id from another ticket cannot be driven here.
public static class TicketChecklistEndpoints
{
    public static IEndpointRouteBuilder MapTicketChecklistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets/{ticketId:guid}/checklists")
            .WithTags("Checklists")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", async (Guid ticketId, HttpContext http, ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var views = await svc.ListAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, ct);
                return Results.Ok(new { items = views.Select(MapView) });
            })).WithName("ListTicketChecklists").WithOpenApi();

        group.MapGet("/available-templates", async (Guid ticketId, HttpContext http, ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var list = await svc.ListAvailableTemplatesAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, ct);
                return Results.Ok(new
                {
                    items = list.Select(t => new
                    {
                        id = t.Id, name = t.Name, description = t.Description,
                        itemCount = t.ItemCount, blockClose = t.BlockClose,
                    }),
                });
            })).WithName("ListAvailableChecklistTemplates").WithOpenApi();

        group.MapPost("/", async (
            Guid ticketId, [FromBody] AttachChecklistRequest req, HttpContext http,
            ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                if (req is null || req.TemplateId == Guid.Empty)
                    return Results.BadRequest(new { error = "templateId is required.", code = ChecklistRejectCode.Invalid });
                var view = await svc.AttachAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, req.TemplateId, ct);
                return Results.Created($"/api/tickets/{ticketId}/checklists/{view.Checklist.Id}", MapView(view));
            })).WithName("AttachTicketChecklist").WithOpenApi();

        group.MapDelete("/{checklistId:guid}", async (
            Guid ticketId, Guid checklistId, HttpContext http, ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                await svc.DetachAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, checklistId, ct);
                return Results.NoContent();
            })).WithName("DetachTicketChecklist").WithOpenApi();

        group.MapPost("/{checklistId:guid}/items", async (
            Guid ticketId, Guid checklistId, [FromBody] ChecklistItemRequest req, HttpContext http,
            ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var item = await svc.AddItemAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, checklistId,
                    req.SectionId, ToInput(req), ct);
                return Results.Created($"/api/tickets/{ticketId}/checklists/items/{item.Id}", MapItem(item));
            })).WithName("AddTicketChecklistItem").WithOpenApi();

        group.MapPut("/items/{itemId:guid}", async (
            Guid ticketId, Guid itemId, [FromBody] ChecklistItemRequest req, HttpContext http,
            ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var item = await svc.UpdateItemAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, itemId, ToInput(req), ct);
                return Results.Ok(MapItem(item));
            })).WithName("UpdateTicketChecklistItem").WithOpenApi();

        group.MapDelete("/items/{itemId:guid}", async (
            Guid ticketId, Guid itemId, HttpContext http, ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                await svc.RemoveItemAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, itemId, ct);
                return Results.NoContent();
            })).WithName("RemoveTicketChecklistItem").WithOpenApi();

        group.MapPatch("/items/{itemId:guid}/state", async (
            Guid ticketId, Guid itemId, [FromBody] ChecklistItemStateRequest req, HttpContext http,
            ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var item = await svc.SetItemStateAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, itemId,
                    req.State ?? string.Empty, req.Reason, req.Comment, ct);
                return Results.Ok(MapItem(item));
            })).WithName("SetTicketChecklistItemState").WithOpenApi();

        group.MapPost("/items/{itemId:guid}/comments", async (
            Guid ticketId, Guid itemId, [FromBody] ChecklistCommentRequest req, HttpContext http,
            ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var item = await svc.AddCommentAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, itemId, req.Comment ?? string.Empty, ct);
                return Results.Ok(MapItem(item));
            })).WithName("AddTicketChecklistItemComment").WithOpenApi();

        group.MapGet("/items/{itemId:guid}/events", async (
            Guid ticketId, Guid itemId, HttpContext http, ITicketChecklistService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var events = await svc.ListItemEventsAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, itemId, ct);
                return Results.Ok(new
                {
                    items = events.Select(e => new
                    {
                        id = e.Id, kind = e.Kind, userId = e.UserId, userName = e.UserName,
                        fromState = e.FromState, toState = e.ToState, comment = e.Comment, createdUtc = e.CreatedUtc,
                    }),
                });
            })).WithName("ListTicketChecklistItemEvents").WithOpenApi();

        return app;
    }

    /// Maps the service's stable rejection codes to HTTP. Not-found and
    /// no-access collapse to 404 like the rest of the ticket API.
    private static async Task<IResult> Run(Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (ChecklistRejectedException ex)
        {
            var status = ex.Code switch
            {
                ChecklistRejectCode.NotFound => 404,
                ChecklistRejectCode.Disabled => 404,
                ChecklistRejectCode.Forbidden => 403,
                ChecklistRejectCode.Invalid => 400,
                _ => 409,
            };
            return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: status);
        }
    }

    private static ChecklistItemInput ToInput(ChecklistItemRequest req) => new(
        req.Title ?? string.Empty, req.Description, req.TeamLabel, req.TimingLabel, req.LinkUrl, req.LinkLabel, req.IsRequired);

    internal static object MapView(TicketChecklistView v) => new
    {
        id = v.Checklist.Id,
        ticketId = v.Checklist.TicketId,
        templateId = v.Checklist.TemplateId,
        name = v.Checklist.Name,
        description = v.Checklist.Description,
        blockClose = v.Checklist.BlockClose,
        sortOrder = v.Checklist.SortOrder,
        attachedByUserId = v.Checklist.AttachedByUserId,
        attachedByName = v.Checklist.AttachedByName,
        attachedUtc = v.Checklist.AttachedUtc,
        completedUtc = v.Checklist.CompletedUtc,
        requiredTotal = v.Checklist.RequiredTotal,
        requiredDone = v.Checklist.RequiredDone,
        totalItems = v.Checklist.TotalItems,
        doneItems = v.Checklist.DoneItems,
        touched = v.Checklist.Touched,
        sections = v.Sections.Select(s => new { id = s.Id, title = s.Title, sortOrder = s.SortOrder }),
        items = v.Items.Select(MapItem),
    };

    internal static object MapItem(TicketChecklistItem i) => new
    {
        id = i.Id,
        checklistId = i.ChecklistId,
        sectionId = i.SectionId,
        title = i.Title,
        description = i.Description,
        teamLabel = i.TeamLabel,
        timingLabel = i.TimingLabel,
        linkUrl = i.LinkUrl,
        linkLabel = i.LinkLabel,
        isRequired = i.IsRequired,
        sortOrder = i.SortOrder,
        isAdHoc = i.IsAdHoc,
        addedByUserId = i.AddedByUserId,
        addedByName = i.AddedByName,
        state = i.State,
        stateChangedUtc = i.StateChangedUtc,
        stateChangedByUserId = i.StateChangedByUserId,
        stateChangedByName = i.StateChangedByName,
        naReason = i.NaReason,
        commentCount = i.CommentCount,
        createdUtc = i.CreatedUtc,
    };

    public sealed record AttachChecklistRequest(Guid TemplateId);

    public sealed record ChecklistItemRequest(
        Guid? SectionId,
        string? Title,
        string? Description,
        string? TeamLabel,
        string? TimingLabel,
        string? LinkUrl,
        string? LinkLabel,
        bool? IsRequired);

    public sealed record ChecklistItemStateRequest(string? State, string? Reason, string? Comment);

    public sealed record ChecklistCommentRequest(string? Comment);
}
