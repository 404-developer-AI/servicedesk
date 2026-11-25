using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Campaigns;
using Servicedesk.Domain.ComposeTemplates;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Campaigns;
using Servicedesk.Infrastructure.Maintenance;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.TicketTemplates;

namespace Servicedesk.Api.Maintenance;

public sealed record MaintenanceMemberRequest(Guid? Id, Guid? CompanyId, Guid? ContactId, string? Label, bool? IsActive, bool? SkipNextRound);

public sealed record MaintenancePlanRequest(
    string? Name, string? Notes, Guid? QueueId, Guid? TicketTemplateId, string? TicketSubject,
    string? MailSubject, string? MailBodyHtml, string? ScheduleMode, int? IntervalMonths,
    DateTime? NextInterventionDate, int? InterventionMinute, int? MailDaysBefore, bool? AutoSend,
    Guid? PendingStatusId, int? PendingDays, bool? IsActive, IReadOnlyList<MaintenanceMemberRequest>? Members,
    int? MailMinute, bool? ReminderEnabled, int? ReminderDays, string? ReminderSubject, string? ReminderBodyHtml,
    string? OtherTimeNotice);

/// v0.1.67 (Campaigns phase 3) — the Maintenance planner (ARCHITECTURE.md →
/// Maintenance planner). Same access boundary as Campaigns: RequireAgent +
/// the per-user Campaigns flag on every route (admins included). A non-admin
/// may only pick a queue they can access. Plans carry no ticket content; a
/// round's tickets and answers live on its (queue-scoped) campaign.
public static class MaintenanceEndpoints
{
    public const int MaxMembers = 500;

    public static IEndpointRouteBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/maintenance")
            .WithTags("Maintenance")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.Campaigns);

        group.MapGet("/plans", async ([FromQuery] bool? includeInactive, IMaintenancePlanRepository plans, CancellationToken ct) =>
            Results.Ok(new { items = (await plans.ListAsync(includeInactive ?? true, ct)).Select(MapPlan) }))
            .WithName("ListMaintenancePlans");

        group.MapGet("/plans/{id:guid}", async (Guid id, IMaintenancePlanRepository plans, CancellationToken ct) =>
        {
            var detail = await plans.GetAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(new
            {
                plan = MapPlan(detail.Plan),
                members = detail.Members.Select(m => new
                {
                    id = m.Id, companyId = m.CompanyId, companyName = m.CompanyName, contactId = m.ContactId,
                    contactName = m.ContactName, contactEmail = m.ContactEmail, label = m.Label, isActive = m.IsActive,
                    skipNextRound = m.SkipNextRound, noteCount = m.NoteCount,
                }),
            });
        }).WithName("GetMaintenancePlan");

        group.MapPost("/plans", async ([FromBody] MaintenancePlanRequest req, HttpContext http, IMaintenancePlanRepository plans,
            Deps deps, IAuditLogger audit, CancellationToken ct) =>
        {
            var (input, error) = await ValidateAsync(req, http, deps, ct);
            if (error is not null) return Results.BadRequest(new { error });
            var id = await plans.CreateAsync(input!, ActorContext.GetUserId(http), ct);
            await CampaignEndpoints.AuditAsync(audit, http, "maintenance.plan.created", id, Summary(input!), ct);
            return Results.Created($"/api/maintenance/plans/{id}", new { id });
        }).WithName("CreateMaintenancePlan");

        group.MapPut("/plans/{id:guid}", async (Guid id, [FromBody] MaintenancePlanRequest req, HttpContext http,
            IMaintenancePlanRepository plans, Deps deps, IAuditLogger audit, CancellationToken ct) =>
        {
            var (input, error) = await ValidateAsync(req, http, deps, ct);
            if (error is not null) return Results.BadRequest(new { error });
            // One round per plan and date: re-planning a date that already ran
            // would be skipped by the worker, so say so now.
            if (input!.NextInterventionDate is { } date && await plans.HasRoundOnAsync(id, date, ct))
                return Results.BadRequest(new
                {
                    error = $"This plan already had a round for {date.ToString("d MMM yyyy", global::System.Globalization.CultureInfo.InvariantCulture)}. Pick another intervention date.",
                    code = "round_date_used",
                });
            if (!await plans.UpdateAsync(id, input!, ActorContext.GetUserId(http), ct)) return Results.NotFound();
            await CampaignEndpoints.AuditAsync(audit, http, "maintenance.plan.updated", id, Summary(input!), ct);
            return Results.NoContent();
        }).WithName("UpdateMaintenancePlan");

        group.MapDelete("/plans/{id:guid}", async (Guid id, HttpContext http, IMaintenancePlanRepository plans,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await plans.DeleteUnstartedAsync(id, ct))
                return Results.Conflict(new
                {
                    error = "This plan already ran. Switch it off instead — its tickets and campaigns stay.",
                    code = "plan_has_rounds",
                });
            await CampaignEndpoints.AuditAsync(audit, http, "maintenance.plan.deleted", id, null, ct);
            return Results.NoContent();
        }).WithName("DeleteMaintenancePlan");

        // Starts the next round now instead of waiting for its mail day.
        group.MapPost("/plans/{id:guid}/start-now", async (Guid id, HttpContext http, IMaintenanceRoundService rounds,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await rounds.StartNowAsync(id, ct))
                return Results.Conflict(new { error = "Only an active plan with a next intervention date can start a round.", code = "plan_not_startable" });
            await CampaignEndpoints.AuditAsync(audit, http, "maintenance.plan.started", id, null, ct);
            return Results.NoContent();
        }).WithName("StartMaintenanceRoundNow").RequireRateLimiting("bulk");

        group.MapGet("/tokens", () => Results.Ok(new
        {
            ticket = TicketTokens.Select(t => new { token = t.Token, label = t.Label }),
            mail = MailTokens.Select(t => new { token = t.Token, label = t.Label }),
        })).WithName("ListMaintenanceTokens");

        return app;
    }

    /// The services validation needs, bound as one parameter.
    public sealed record Deps(
        IQueueAccessService QueueAccess, ITaxonomyRepository Taxonomy, ITicketTemplateRepository Templates,
        ICompanyRepository Companies, ITicketHtmlSanitizer Sanitizer)
    {
        public static ValueTask<Deps> BindAsync(HttpContext http) => ValueTask.FromResult(new Deps(
            http.RequestServices.GetRequiredService<IQueueAccessService>(),
            http.RequestServices.GetRequiredService<ITaxonomyRepository>(),
            http.RequestServices.GetRequiredService<ITicketTemplateRepository>(),
            http.RequestServices.GetRequiredService<ICompanyRepository>(),
            http.RequestServices.GetRequiredService<ITicketHtmlSanitizer>()));
    }

    private static async Task<(MaintenancePlanInput? Input, string? Error)> ValidateAsync(MaintenancePlanRequest r,
        HttpContext http, Deps d, CancellationToken ct)
    {
        var name = (r.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 200) return (null, "Name is required (at most 200 characters).");
        if (r.QueueId is not { } queueId || await d.Taxonomy.GetQueueAsync(queueId, ct) is null)
            return (null, "Pick the queue for the tickets.");
        if (!await d.QueueAccess.HasQueueAccessAsync(ActorContext.GetUserId(http), ActorContext.GetRole(http.User), queueId, ct))
            return (null, "Pick a queue you have access to.");
        if (r.TicketTemplateId is { } tid && await d.Templates.GetAsync(tid, ct) is null) return (null, "That ticket template does not exist.");

        var mode = r.ScheduleMode ?? MaintenanceScheduleModes.Manual;
        if (!MaintenanceScheduleModes.IsValid(mode)) return (null, "Schedule must be Manual or Automatic.");
        if (mode == MaintenanceScheduleModes.Automatic)
        {
            if (r.IntervalMonths is not (>= 1 and <= 60)) return (null, "An automatic schedule repeats every 1 to 60 months.");
            if (r.NextInterventionDate is null) return (null, "An automatic schedule needs the next intervention date.");
        }
        var minute = r.InterventionMinute ?? 540;
        var days = r.MailDaysBefore ?? 14;
        var mailMinute = r.MailMinute ?? 540;
        if (mailMinute is < 0 or > 1439) return (null, "Pick the time the mail goes out.");
        if (minute is < 0 or > 1439) return (null, "Pick the time of the intervention.");
        if (days is < 0 or > 180) return (null, "Mail 0 to 180 days before the intervention.");
        if ((r.TicketSubject ?? "").Length > 500 || (r.MailSubject ?? "").Length > 500) return (null, "Subjects are at most 500 characters.");
        if ((r.MailBodyHtml ?? "").Length > 200_000) return (null, "The message is too long.");
        if ((r.Notes ?? "").Length > 4000) return (null, "Notes are at most 4000 characters.");
        var unknownTicket = MaintenanceTokens.Unknown(r.TicketSubject, TicketTokens.Select(t => t.Token));
        if (unknownTicket.Count > 0) return (null, $"The ticket subject uses a placeholder that does not exist: {string.Join(", ", unknownTicket)}.");
        var unknownMail = MaintenanceTokens.Unknown((r.MailSubject ?? "") + " " + (r.MailBodyHtml ?? ""), MailTokens.Select(t => t.Token));
        if (unknownMail.Count > 0)
            return (null, $"The mail uses a placeholder that cannot be filled in there: {string.Join(", ", unknownMail)}. Pick placeholders from the mail's own menu.");
        var reminderOn = r.ReminderEnabled == true;
        var reminderDays = r.ReminderDays ?? 3;
        if (reminderOn)
        {
            if (r.AutoSend != true) return (null, "A reminder needs automatic sending.");
            if (reminderDays is < 1 or > 60) return (null, "Send the reminder 1 to 60 days after the first mail.");
            if (string.IsNullOrWhiteSpace(r.ReminderSubject) || string.IsNullOrWhiteSpace(r.ReminderBodyHtml))
                return (null, "The reminder needs a subject and a message.");
        }
        if ((r.ReminderSubject ?? "").Length > 500) return (null, "Subjects are at most 500 characters.");
        if ((r.OtherTimeNotice ?? "").Length > CampaignEndpoints.MaxOtherTimeNoticeLength)
            return (null, $"The note for \"Another moment\" is at most {CampaignEndpoints.MaxOtherTimeNoticeLength} characters.");
        if ((r.ReminderBodyHtml ?? "").Length > 200_000) return (null, "The reminder message is too long.");
        var unknownReminder = MaintenanceTokens.Unknown((r.ReminderSubject ?? "") + " " + (r.ReminderBodyHtml ?? ""), MailTokens.Select(t => t.Token));
        if (unknownReminder.Count > 0)
            return (null, $"The reminder uses a placeholder that cannot be filled in there: {string.Join(", ", unknownReminder)}.");
        if (r.AutoSend == true && (string.IsNullOrWhiteSpace(r.MailSubject) || string.IsNullOrWhiteSpace(r.MailBodyHtml)))
            return (null, "Automatic sending needs a mail subject and message.");
        if (r.PendingStatusId is { } sid)
        {
            var status = (await d.Taxonomy.ListStatusesAsync(ct)).FirstOrDefault(s => s.Id == sid && s.IsActive);
            if (status is null || status.StateCategory != "Pending") return (null, "Pick a Pending status, or none.");
        }
        if (r.PendingDays is not (null or (>= 1 and <= 365))) return (null, "Wait 1 to 365 days, or until the intervention.");

        var members = r.Members ?? Array.Empty<MaintenanceMemberRequest>();
        if (members.Count > MaxMembers) return (null, $"A plan has at most {MaxMembers} customers.");
        var memberInputs = new List<MaintenanceMemberInput>(members.Count);
        foreach (var m in members)
        {
            if (m.ContactId is not { } contactId || await d.Companies.GetContactAsync(contactId, ct) is null)
                return (null, "Every customer needs an existing contact.");
            if (m.CompanyId is { } cid)
            {
                var company = await d.Companies.GetCompanyAsync(cid, ct);
                if (company is null) return (null, "A selected company does not exist.");
                // The mail goes to this contact on behalf of this company: never a contact of another company.
                if (!(await d.Companies.ListContactLinksAsync(contactId, ct)).Any(l => l.CompanyId == cid))
                    return (null, $"A contact is not linked to {company.Name}. Pick a contact of that company.");
            }
            var label = (m.Label ?? string.Empty).Trim();
            if (label.Length > 100) return (null, "Labels are at most 100 characters.");
            memberInputs.Add(new MaintenanceMemberInput(m.Id, m.CompanyId, contactId, label, m.IsActive ?? true, m.SkipNextRound ?? false));
        }

        return (new MaintenancePlanInput(
            name, (r.Notes ?? string.Empty).Trim(), queueId, r.TicketTemplateId, (r.TicketSubject ?? string.Empty).Trim(),
            (r.MailSubject ?? string.Empty).Trim(), d.Sanitizer.Sanitize(r.MailBodyHtml ?? string.Empty), mode,
            mode == MaintenanceScheduleModes.Automatic ? r.IntervalMonths : null,
            r.NextInterventionDate is { } date ? DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified) : null,
            minute, days, r.AutoSend ?? false, r.PendingStatusId, r.PendingStatusId is null ? null : r.PendingDays,
            r.IsActive ?? true, memberInputs, mailMinute,
            new MaintenanceReminderInput(reminderOn, reminderDays, (r.ReminderSubject ?? string.Empty).Trim(),
                d.Sanitizer.Sanitize(r.ReminderBodyHtml ?? string.Empty)),
            (r.OtherTimeNotice ?? string.Empty).Trim()), null);
    }

    private static readonly IReadOnlyList<(string Token, string Label)> TicketTokens =
        ComposeTokens.Supported.Select(t => (t.Token, t.Label)).Concat(MaintenanceTokens.Supported).ToList();
    private static readonly IReadOnlyList<(string Token, string Label)> MailTokens =
        ComposeTokens.Supported.Select(t => (t.Token, t.Label)).Concat(CampaignTokens.Supported).Concat(MaintenanceTokens.Mail).ToList();

    private static object Summary(MaintenancePlanInput i) => new
    {
        i.Name, i.ScheduleMode, i.IntervalMonths, i.NextInterventionDate, i.AutoSend, i.IsActive, members = i.Members.Count,
    };

    private static object MapPlan(MaintenancePlanRow p) => new
    {
        id = p.Id,
        name = p.Name,
        notes = p.Notes,
        queueId = p.QueueId,
        queueName = p.QueueName,
        ticketTemplateId = p.TicketTemplateId,
        ticketTemplateName = p.TicketTemplateName,
        ticketSubject = p.TicketSubject,
        mailSubject = p.MailSubject,
        mailBodyHtml = p.MailBodyHtml,
        scheduleMode = p.ScheduleMode,
        intervalMonths = p.IntervalMonths,
        nextInterventionDate = p.NextInterventionDate?.ToString("yyyy-MM-dd"),
        interventionMinute = p.InterventionMinute,
        mailMinute = p.MailMinute,
        otherTimeNotice = p.OtherTimeNotice,
        reminderEnabled = p.ReminderEnabled,
        reminderDays = p.ReminderDays,
        reminderSubject = p.ReminderSubject,
        reminderBodyHtml = p.ReminderBodyHtml,
        mailDaysBefore = p.MailDaysBefore,
        autoSend = p.AutoSend,
        sendAsEmail = p.SendAsEmail,
        pendingStatusId = p.PendingStatusId,
        pendingDays = p.PendingDays,
        isActive = p.IsActive,
        memberCount = p.MemberCount,
        lastRound = p.LastRoundId is null ? null : new
        {
            id = p.LastRoundId,
            interventionDate = p.LastInterventionDate?.ToString("yyyy-MM-dd"),
            state = p.LastRoundState,
            error = p.LastRoundError,
            campaignId = p.LastCampaignId,
            campaignName = p.LastCampaignName,
            tickets = p.LastTickets,
            sent = p.LastSent,
            approved = p.LastApproved,
            otherTime = p.LastOtherTime,
            declined = p.LastDeclined,
            skipped = p.LastSkipped,
            reminderUtc = p.LastReminderUtc,
            reminderNote = p.LastReminderNote,
        },
    };
}
