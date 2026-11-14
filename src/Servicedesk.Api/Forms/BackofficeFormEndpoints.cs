using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tickets;
using Servicedesk.Infrastructure.Forms;

namespace Servicedesk.Api.Forms;

/// v0.1.54 — the Back office forms work list. Staff only (RequireAgent) and
/// every route behind the per-user BackofficeForms flag (403 when off, admins
/// included). Deliberately not queue-filtered: a flag holder sees every form
/// filled from a template marked "Send to back office" (owner decision). The
/// response carries the form plus a thin ticket context (number, subject,
/// company) — opening the ticket itself still goes through queue access.
public static class BackofficeFormEndpoints
{
    public const int PageSize = 50;

    public static IEndpointRouteBuilder MapBackofficeFormEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backoffice-forms")
            .WithTags("Forms")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.BackofficeForms);

        group.MapGet("/", async (
            [FromQuery] string? status, [FromQuery] Guid? templateId, [FromQuery] Guid? companyId,
            [FromQuery] Guid? submittedBy, [FromQuery] string? q, [FromQuery] int? offset, [FromQuery] int? limit,
            IBackofficeFormService svc, CancellationToken ct) =>
        {
            // Unknown status → All; the repository re-checks the whitelist.
            var st = status is not null && BackofficeFormStatus.All.Contains(status) ? status : null;
            var page = await svc.ListAsync(new BackofficeFormListQuery(
                st, templateId, companyId, submittedBy, q,
                Math.Max(0, offset ?? 0), Math.Clamp(limit ?? PageSize, 1, 200)), ct);
            return Results.Ok(new { items = page.Items.Select(MapItem), total = page.Total });
        }).WithName("ListBackofficeForms").WithOpenApi();

        group.MapGet("/counts", async (IBackofficeFormService svc, CancellationToken ct) =>
        {
            var c = await svc.CountsAsync(ct);
            return Results.Ok(new { open = c.Open, inProgress = c.InProgress });
        }).WithName("CountBackofficeForms").WithOpenApi();

        // Template filter for the list: the marked templates (name only —
        // the template editor itself stays admin-only).
        group.MapGet("/templates", async (IFormTemplateRepository templates, CancellationToken ct) =>
        {
            var list = await templates.ListAsync(ct);
            return Results.Ok(new
            {
                items = list.Where(t => t.SendToBackoffice)
                    .Select(t => new { id = t.Id, name = t.Name, isActive = t.IsActive }),
            });
        }).WithName("ListBackofficeFormTemplates").WithOpenApi();

        group.MapGet("/{formId:guid}", async (Guid formId, IBackofficeFormService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var v = await svc.GetAsync(formId, ct);
                return Results.Ok(new
                {
                    item = MapItem(v.Detail.Item),
                    fields = v.Detail.Snapshot.Fields,
                    answers = v.Detail.Answers,
                    log = v.Detail.Log.Select(l => new
                    {
                        id = l.Id, action = l.Action, userId = l.UserId, userName = l.UserName,
                        note = l.Note, formVersion = l.FormVersion, createdUtc = l.CreatedUtc,
                    }),
                    changesSinceHandled = v.ChangesSinceHandled.Select(c => new
                    {
                        fieldId = c.FieldId, label = c.Label, before = c.Before, after = c.After,
                    }),
                });
            })).WithName("GetBackofficeForm").WithOpenApi();

        group.MapPost("/{formId:guid}/claim", async (
            Guid formId, [FromBody] ClaimRequest? req, HttpContext http, IBackofficeFormService svc, CancellationToken ct) =>
            await Run(async () => Results.Ok(MapItem(
                await svc.ClaimAsync(TicketEndpoints.ResolveMutationActor(http), formId, req?.TakeOver ?? false, ct))))
        ).WithName("ClaimBackofficeForm").WithOpenApi();

        group.MapPost("/{formId:guid}/release", async (
            Guid formId, HttpContext http, IBackofficeFormService svc, CancellationToken ct) =>
            await Run(async () => Results.Ok(MapItem(
                await svc.ReleaseAsync(TicketEndpoints.ResolveMutationActor(http), formId, ct))))
        ).WithName("ReleaseBackofficeForm").WithOpenApi();

        group.MapPost("/{formId:guid}/handle", async (
            Guid formId, [FromBody] HandleRequest? req, HttpContext http, IBackofficeFormService svc, CancellationToken ct) =>
            await Run(async () => Results.Ok(MapItem(
                await svc.HandleAsync(TicketEndpoints.ResolveMutationActor(http), formId, req?.Note, req?.ExpectedVersion ?? 0, ct))))
        ).WithName("HandleBackofficeForm").WithOpenApi();

        group.MapPost("/{formId:guid}/reopen", async (
            Guid formId, [FromBody] NoteRequest? req, HttpContext http, IBackofficeFormService svc, CancellationToken ct) =>
            await Run(async () => Results.Ok(MapItem(
                await svc.ReopenAsync(TicketEndpoints.ResolveMutationActor(http), formId, req?.Note, ct))))
        ).WithName("ReopenBackofficeForm").WithOpenApi();

        return app;
    }

    private static async Task<IResult> Run(Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (FormRejectedException ex)
        {
            var status = ex.Code switch
            {
                FormRejectCode.NotFound => 404,
                FormRejectCode.Forbidden => 403,
                FormRejectCode.Invalid => 400,
                _ => 409,
            };
            return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: status);
        }
    }

    internal static object MapItem(BackofficeFormListItem i) => new
    {
        id = i.Id,
        ticketId = i.TicketId,
        ticketNumber = i.TicketNumber,
        ticketSubject = i.TicketSubject,
        companyId = i.CompanyId,
        companyName = i.CompanyName,
        templateId = i.TemplateId,
        name = i.Name,
        version = i.Version,
        status = i.Status,
        submittedByUserId = i.SubmittedByUserId,
        submittedByName = i.SubmittedByName,
        submittedUtc = i.SubmittedUtc,
        updatedByName = i.UpdatedByName,
        updatedUtc = i.UpdatedUtc,
        claimedByUserId = i.ClaimedByUserId,
        claimedByName = i.ClaimedByName,
        claimedUtc = i.ClaimedUtc,
        handledByUserId = i.HandledByUserId,
        handledByName = i.HandledByName,
        handledUtc = i.HandledUtc,
        handledVersion = i.HandledVersion,
        handledNote = i.HandledNote,
        // Changed after it was handled (back to Open by an edit).
        reopened = i.Reopened,
        // Edited while someone was handling it.
        updatedSinceClaim = i.Status == BackofficeFormStatus.InProgress
                            && i.ClaimedVersion is { } cv && i.Version > cv,
    };

    public sealed record ClaimRequest(bool TakeOver);
    public sealed record NoteRequest(string? Note);
    /// <paramref name="ExpectedVersion"/> = the form version the handler saw.
    public sealed record HandleRequest(string? Note, int ExpectedVersion);
}
