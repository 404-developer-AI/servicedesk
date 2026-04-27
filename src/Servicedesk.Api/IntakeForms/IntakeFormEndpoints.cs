using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Domain.IntakeForms;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.IntakeForms;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.IntakeForms;

/// HTTP surface for the v0.0.19 Intake-Forms feature.
///
/// <para>Three concerns, three route groups:</para>
/// <list type="bullet">
/// <item><b>Admin</b> — CRUD over <c>intake_templates</c> under
/// <c>/api/settings/intake-templates</c>. Settings live next to SLA and
/// other admin-only config pages.</item>
/// <item><b>Agent</b> — per-ticket instance management under
/// <c>/api/tickets/{ticketId}/intake-forms</c>. Prefill drawer PUTs land
/// here. Send happens via the existing ticket-mail endpoint which accepts
/// <c>LinkedFormIds</c>.</item>
/// <item><b>Public</b> — <c>/api/intake-forms/{token}</c>. No auth, CSRF
/// exempt (see DoubleSubmitCsrfMiddleware), rate-limited per IP+token via
/// the <c>intake-public</c> policy registered in Program.cs.</item>
/// </list>
public static class IntakeFormEndpoints
{
    /// Reasonable ceilings that any sane template + submission will stay
    /// below. Protects the admin UI from pathological payloads before we
    /// even open a transaction.
    private const int MaxLabelLength = 500;
    private const int MaxHelpTextLength = 2000;
    private const int MaxOptionCount = 200;

    public static IEndpointRouteBuilder MapIntakeFormEndpoints(this IEndpointRouteBuilder app)
    {
        MapAdminEndpoints(app);
        MapAgentEndpoints(app);
        MapPublicEndpoints(app);
        return app;
    }

    // ============================================================
    // Admin — template CRUD
    // ============================================================
    private static void MapAdminEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/intake-templates")
            .WithTags("IntakeTemplates")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (
            bool? includeInactive, IIntakeTemplateRepository repo, CancellationToken ct) =>
        {
            var templates = await repo.ListAsync(includeInactive ?? false, ct);
            return Results.Ok(templates.Select(MapTemplateDto));
        }).WithName("ListIntakeTemplates").WithOpenApi();

        group.MapGet("/{id:guid}", async (
            Guid id, IIntakeTemplateRepository repo, CancellationToken ct) =>
        {
            var template = await repo.GetAsync(id, ct);
            return template is null ? Results.NotFound() : Results.Ok(MapTemplateDto(template));
        }).WithName("GetIntakeTemplate").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] TemplateUpsertRequest req, HttpContext http,
            IIntakeTemplateRepository repo, ISettingsService settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

            var err = await ValidateTemplateRequestAsync(req, settings, ct);
            if (err is not null) return Results.BadRequest(new { error = err });

            var questions = MapQuestionInputs(req.Questions!);
            Guid id;
            try
            {
                id = await repo.CreateAsync(req.Name!.Trim(), req.Description?.Trim(), questions, userId, ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_intake_templates_active_name")
            {
                return Results.Conflict(new
                {
                    error = "An active template with that name already exists. Pick a different name or reactivate the existing one.",
                });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "intake_template.create",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { questionCount = questions.Count }));

            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/settings/intake-templates/{id}", MapTemplateDto(created!));
        }).WithName("CreateIntakeTemplate").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] TemplateUpsertRequest req, HttpContext http,
            IIntakeTemplateRepository repo, ISettingsService settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = await ValidateTemplateRequestAsync(req, settings, ct);
            if (err is not null) return Results.BadRequest(new { error = err });

            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            try
            {
                await repo.UpdateAsync(
                    id, req.Name!.Trim(), req.Description?.Trim(),
                    req.IsActive ?? existing.IsActive,
                    MapQuestionInputs(req.Questions!), ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_intake_templates_active_name")
            {
                return Results.Conflict(new
                {
                    error = "Another active template already uses that name. Pick a different name before saving.",
                });
            }
            // Historical submissions render against their instance's
            // template_snapshot_json, so a full-replace on a used template
            // no longer needs to be blocked. The 23503 catch that used to
            // live here is dead code since the FK on
            // intake_form_answers.question_id was dropped.

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "intake_template.update",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { questionCount = req.Questions!.Count, isActive = req.IsActive }));

            var updated = await repo.GetAsync(id, ct);
            return Results.Ok(MapTemplateDto(updated!));
        }).WithName("UpdateIntakeTemplate").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, IIntakeTemplateRepository repo,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var deactivated = await repo.DeactivateAsync(id, ct);
            if (!deactivated) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "intake_template.deactivate",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()));

            return Results.NoContent();
        }).WithName("DeactivateIntakeTemplate").WithOpenApi();
    }

    // ============================================================
    // Agent — per-ticket instance management
    // ============================================================
    private static void MapAgentEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets/{ticketId:guid}/intake-forms")
            .WithTags("IntakeForms")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", async (
            Guid ticketId, HttpContext http, ITicketRepository tickets,
            IQueueAccessService queueAccess, IIntakeFormRepository forms,
            CancellationToken ct) =>
        {
            if (!await EnsureTicketAccessAsync(http, tickets, queueAccess, ticketId, ct))
                return Results.NotFound();

            var list = await forms.ListForTicketAsync(ticketId, ct);
            return Results.Ok(list.Select(MapSummaryDto));
        }).WithName("ListIntakeFormsForTicket").WithOpenApi();

        group.MapGet("/{instanceId:guid}", async (
            Guid ticketId, Guid instanceId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IIntakeFormRepository forms, CancellationToken ct) =>
        {
            if (!await EnsureTicketAccessAsync(http, tickets, queueAccess, ticketId, ct))
                return Results.NotFound();

            var view = await forms.GetAgentViewAsync(ticketId, instanceId, ct);
            return view is null ? Results.NotFound() : Results.Ok(MapAgentViewDto(view));
        }).WithName("GetIntakeFormInstance").WithOpenApi();

        group.MapGet("/{instanceId:guid}/pdf", async (
            Guid ticketId, Guid instanceId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IIntakeFormRepository forms, IIntakeFormPdfBuilder pdfBuilder,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await EnsureTicketAccessAsync(http, tickets, queueAccess, ticketId, ct))
                return Results.NotFound();

            var view = await forms.GetAgentViewAsync(ticketId, instanceId, ct);
            if (view is null) return Results.NotFound();
            if (view.Instance.Status != IntakeFormStatus.Submitted || view.Answers is null)
            {
                return Results.BadRequest(new
                {
                    error = "PDF is only available once the customer has submitted the form.",
                });
            }

            // Resolve ticket number for the file name + PDF header. The
            // ticket was already fetched during the access check, but we
            // look it up cheaply here to keep the helper independent.
            var ticket = await tickets.GetByIdAsync(ticketId, ct);
            var ticketNumber = ticket?.Ticket.Number ?? 0;

            byte[] bytes;
            try
            {
                bytes = pdfBuilder.Render(view, ticketNumber);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "intake_form.pdf_download",
                Actor: actor,
                ActorRole: role,
                Target: instanceId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId, templateId = view.Template.Id }));

            var safeName = SanitizeFilename(view.Template.Name);
            var fileName = $"intake-{ticketNumber}-{safeName}.pdf";
            return Results.File(bytes, "application/pdf", fileName);
        }).WithName("DownloadIntakeFormPdf").WithOpenApi();

        group.MapPost("/", async (
            Guid ticketId, [FromBody] CreateInstanceRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IIntakeTemplateRepository templates, IIntakeFormRepository forms,
            IIntakeTokenResolver tokens, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await EnsureTicketAccessAsync(http, tickets, queueAccess, ticketId, ct))
                return Results.NotFound();

            if (req.TemplateId == Guid.Empty) return Results.BadRequest(new { error = "templateId is required." });

            var template = await templates.GetAsync(req.TemplateId, ct);
            if (template is null || !template.IsActive)
                return Results.BadRequest(new { error = "Template not found or inactive." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

            var resolvedTokens = await tokens.ResolveAsync(ticketId, ct);
            var prefill = BuildInitialPrefill(template, resolvedTokens, req.Prefill);
            var prefillJson = JsonSerializer.Serialize(prefill);

            var instanceId = await forms.CreateDraftAsync(ticketId, req.TemplateId, prefillJson, userId, ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "intake_form.draft_created",
                Actor: actor,
                ActorRole: role,
                Target: instanceId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId, templateId = req.TemplateId }));

            var view = await forms.GetAgentViewAsync(ticketId, instanceId, ct);
            return Results.Created($"/api/tickets/{ticketId}/intake-forms/{instanceId}", MapAgentViewDto(view!));
        }).WithName("CreateIntakeFormDraft").WithOpenApi();

        group.MapPut("/{instanceId:guid}/prefill", async (
            Guid ticketId, Guid instanceId, [FromBody] UpdatePrefillRequest req,
            HttpContext http, ITicketRepository tickets, IQueueAccessService queueAccess,
            IIntakeFormRepository forms, CancellationToken ct) =>
        {
            if (!await EnsureTicketAccessAsync(http, tickets, queueAccess, ticketId, ct))
                return Results.NotFound();

            // Dictionary key = questionId (long-as-string in JSON).
            // Values are the already-serialised JSON values (string/bool/number/array).
            // We re-serialise to ensure the stored payload is canonical JSON.
            var prefill = req.Prefill ?? new Dictionary<string, JsonElement>();
            var prefillJson = JsonSerializer.Serialize(prefill);

            var updated = await forms.UpdateDraftPrefillAsync(instanceId, ticketId, prefillJson, ct);
            return updated ? Results.NoContent() : Results.NotFound();
        }).WithName("UpdateIntakeFormPrefill").WithOpenApi();

        group.MapDelete("/{instanceId:guid}", async (
            Guid ticketId, Guid instanceId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IIntakeFormRepository forms, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await EnsureTicketAccessAsync(http, tickets, queueAccess, ticketId, ct))
                return Results.NotFound();

            var removed = await forms.DeleteDraftAsync(instanceId, ticketId, ct);
            if (!removed)
            {
                // Not a Draft → caller wants to cancel a Sent instance.
                // Use the resend/cancel flow for that; refuse here.
                return Results.Conflict(new { error = "Only draft instances can be deleted. Cancel a sent instance via the resend endpoint." });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "intake_form.draft_deleted",
                Actor: actor,
                ActorRole: role,
                Target: instanceId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId }));

            return Results.NoContent();
        }).WithName("DeleteIntakeFormDraft").WithOpenApi();

        group.MapPost("/{instanceId:guid}/resend", async (
            Guid ticketId, Guid instanceId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IIntakeTemplateRepository templates, IIntakeFormRepository forms,
            IIntakeTokenResolver tokens, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await EnsureTicketAccessAsync(http, tickets, queueAccess, ticketId, ct))
                return Results.NotFound();

            var existing = await forms.GetAgentViewAsync(ticketId, instanceId, ct);
            if (existing is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

            // Re-resolve tokens because ticket context may have changed since
            // the original send. Agent overrides from the prefill snapshot
            // are preserved on top of the fresh resolution.
            var resolved = await tokens.ResolveAsync(ticketId, ct);

            // Merge: start from token-resolved defaults, then overlay the
            // previous instance's prefill. Missing question mappings fall
            // through as empty.
            var template = await templates.GetAsync(existing.Template.Id, ct);
            if (template is null || !template.IsActive)
                return Results.BadRequest(new { error = "Original template is no longer available." });

            var prefill = BuildInitialPrefill(template, resolved, null);
            MergeAgentOverrides(prefill, existing.Instance.Prefill);
            var prefillJson = JsonSerializer.Serialize(prefill);

            await forms.CancelSentAsync(instanceId, ticketId, userId, ct);
            var newInstanceId = await forms.CreateDraftAsync(ticketId, template.Id, prefillJson, userId, ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "intake_form.resend",
                Actor: actor,
                ActorRole: role,
                Target: newInstanceId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId, previousInstanceId = instanceId }));

            var newView = await forms.GetAgentViewAsync(ticketId, newInstanceId, ct);
            return Results.Created($"/api/tickets/{ticketId}/intake-forms/{newInstanceId}", MapAgentViewDto(newView!));
        }).WithName("ResendIntakeForm").WithOpenApi();
    }

    // ============================================================
    // Public — token-gated customer fill + submit
    // ============================================================
    private static void MapPublicEndpoints(IEndpointRouteBuilder app)
    {
        // NOTE: intentionally NOT calling RequireAuthorization. CSRF exempt
        // via DoubleSubmitCsrfMiddleware.ExemptPrefixes. Rate limit
        // partition per (IP, token) via the "intake-public" policy.
        var group = app.MapGroup("/api/intake-forms")
            .WithTags("IntakeFormsPublic")
            .RequireRateLimiting("intake-public");

        group.MapGet("/{token}", async (
            string token, IIntakeFormTokenService tokenSvc,
            IIntakeFormRepository forms, CancellationToken ct) =>
        {
            var hash = tokenSvc.HashForLookup(token);
            if (hash is null) return Results.NotFound();

            var view = await forms.GetByTokenHashForPublicAsync(hash, ct);
            if (view is null) return Results.NotFound();

            if (view.Status == IntakeFormStatus.Expired)
                return Results.Json(new { status = "expired" }, statusCode: StatusCodes.Status410Gone);

            if (view.Status == IntakeFormStatus.Submitted)
                return Results.Json(new { status = "submitted" }, statusCode: StatusCodes.Status409Conflict);

            if (view.ExpiresUtc is DateTime exp && exp <= DateTime.UtcNow)
                return Results.Json(new { status = "expired" }, statusCode: StatusCodes.Status410Gone);

            return Results.Ok(MapPublicDto(view));
        }).WithName("GetPublicIntakeForm").WithOpenApi();

        group.MapPost("/{token}/submit", async (
            string token, [FromBody] PublicSubmitRequest req, HttpContext http,
            IIntakeFormTokenService tokenSvc, IIntakeFormRepository forms,
            ISettingsService settings, IHubContext<TicketPresenceHub> hub,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var hash = tokenSvc.HashForLookup(token);
            if (hash is null) return Results.NotFound();

            if (req.Answers is null)
                return Results.BadRequest(new { error = "answers is required." });

            var view = await forms.GetByTokenHashForPublicAsync(hash, ct);
            if (view is null) return Results.NotFound();
            if (view.Status != IntakeFormStatus.Sent)
                return Results.Json(new { status = view.Status.ToString().ToLowerInvariant() },
                    statusCode: view.Status == IntakeFormStatus.Expired ? 410 : 409);

            var maxAnswerBytes = await settings.GetAsync<int>(SettingKeys.IntakeForms.MaxAnswerSizeBytes, ct);
            var maxTotalBytes = await settings.GetAsync<int>(SettingKeys.IntakeForms.MaxTotalAnswersBytes, ct);

            var validation = ValidateSubmission(view.Questions, req.Answers, maxAnswerBytes, maxTotalBytes);
            if (validation.Error is not null)
                return Results.Json(new { error = validation.Error }, statusCode: validation.Status);

            var ip = http.Connection.RemoteIpAddress?.ToString();
            var ua = http.Request.Headers.UserAgent.ToString();

            var autoPin = await settings.GetAsync<bool>(SettingKeys.IntakeForms.AutoPinSubmittedForms, ct);

            var result = await forms.TrySubmitAsync(hash, validation.Answers!, ip, ua, DateTime.UtcNow, autoPin, ct);
            if (result is null)
                return Results.Conflict(new { status = "submitted-or-expired" });

            await audit.LogAsync(new AuditEvent(
                EventType: "intake_form.submit",
                Actor: "customer",
                ActorRole: "anon",
                Target: result.InstanceId.ToString(),
                ClientIp: ip,
                UserAgent: ua,
                Payload: new { ticketId = result.TicketId, templateId = result.TemplateId, answerCount = validation.Answers!.Count }));

            // SignalR broadcast so any open agent tab on this ticket
            // refreshes without a polling tick. Ticket-list also gets a
            // nudge for overview pages.
            var ticketIdStr = result.TicketId.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            return Results.Ok(new
            {
                status = "submitted",
                templateName = result.TemplateName,
                answers = validation.Answers.Select(a => new { questionId = a.QuestionId, answer = JsonDocument.Parse(a.AnswerJson).RootElement }),
            });
        }).WithName("SubmitPublicIntakeForm").WithOpenApi();
    }

    // ============================================================
    // Request/response DTOs
    // ============================================================
    public sealed record TemplateUpsertRequest(
        string? Name,
        string? Description,
        bool? IsActive,
        IReadOnlyList<QuestionInputDto>? Questions);

    public sealed record QuestionInputDto(
        int SortOrder,
        string? Type,
        string? Label,
        string? HelpText,
        bool? IsRequired,
        string? DefaultValue,
        string? DefaultToken,
        IReadOnlyList<OptionInputDto>? Options);

    public sealed record OptionInputDto(int SortOrder, string Value, string Label);

    public sealed record CreateInstanceRequest(
        Guid TemplateId,
        IReadOnlyDictionary<string, JsonElement>? Prefill);

    public sealed record UpdatePrefillRequest(IReadOnlyDictionary<string, JsonElement>? Prefill);

    public sealed record PublicSubmitRequest(
        IReadOnlyDictionary<string, JsonElement>? Answers);

    // ============================================================
    // Helpers
    // ============================================================
    private static string SanitizeFilename(string input)
    {
        // Strip anything that could escape a Content-Disposition header or
        // break on Windows/Linux file systems. ASCII letters + digits +
        // dash + underscore only; fall back to "form" if nothing remains.
        var sb = new global::System.Text.StringBuilder(input.Length);
        foreach (var c in input)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                sb.Append(c);
            else if (c == '-' || c == '_')
                sb.Append(c);
            else if (c == ' ' || c == '.')
                sb.Append('-');
        }
        var cleaned = sb.ToString().Trim('-');
        return cleaned.Length == 0 ? "form" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static async Task<bool> EnsureTicketAccessAsync(
        HttpContext http, ITicketRepository tickets, IQueueAccessService queueAccess,
        Guid ticketId, CancellationToken ct)
    {
        var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
        var ticket = await tickets.GetByIdAsync(ticketId, ct);
        if (ticket is null) return false;
        return await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct);
    }

    private static async Task<string?> ValidateTemplateRequestAsync(
        TemplateUpsertRequest req, ISettingsService settings, CancellationToken ct)
    {
        if (req is null) return "Body is required.";
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 200)
            return "Name is required and must be ≤200 characters.";
        if (req.Description is not null && req.Description.Length > 2000)
            return "Description must be ≤2000 characters.";
        if (req.Questions is null || req.Questions.Count == 0)
            return "At least one question is required.";

        var maxQuestions = await settings.GetAsync<int>(SettingKeys.IntakeForms.MaxQuestionsPerTemplate, ct);
        if (req.Questions.Count > maxQuestions)
            return $"Too many questions (max {maxQuestions}).";

        var seenOrders = new HashSet<int>();
        foreach (var q in req.Questions)
        {
            if (!seenOrders.Add(q.SortOrder))
                return "sortOrder values must be unique across questions.";
            if (string.IsNullOrWhiteSpace(q.Label) || q.Label.Length > MaxLabelLength)
                return $"Every question needs a label ≤{MaxLabelLength} characters.";
            if (q.HelpText is not null && q.HelpText.Length > MaxHelpTextLength)
                return $"helpText must be ≤{MaxHelpTextLength} characters.";
            if (string.IsNullOrWhiteSpace(q.Type) || !Enum.TryParse<IntakeQuestionType>(q.Type, ignoreCase: false, out var parsed))
                return $"type must be one of {string.Join(", ", Enum.GetNames<IntakeQuestionType>())}.";
            if (q.DefaultToken is not null && q.DefaultToken.Length > 0 && !IntakeTokens.IsSupported(q.DefaultToken))
                return "defaultToken must be one of the supported tokens.";

            var needsOptions = parsed is IntakeQuestionType.DropdownSingle or IntakeQuestionType.DropdownMulti;
            var options = q.Options ?? Array.Empty<OptionInputDto>();
            if (needsOptions && options.Count == 0)
                return "Dropdown questions require at least one option.";
            if (options.Count > MaxOptionCount)
                return $"A dropdown cannot have more than {MaxOptionCount} options.";
            if (!needsOptions && options.Count > 0)
                return "Only dropdown questions can have options.";

            var seenOptOrders = new HashSet<int>();
            foreach (var opt in options)
            {
                if (string.IsNullOrWhiteSpace(opt.Value) || opt.Value.Length > 200)
                    return "Each option needs a value ≤200 characters.";
                if (string.IsNullOrWhiteSpace(opt.Label) || opt.Label.Length > 200)
                    return "Each option needs a label ≤200 characters.";
                if (!seenOptOrders.Add(opt.SortOrder))
                    return "Option sortOrder must be unique per question.";
            }
        }

        return null;
    }

    private static List<IntakeQuestionInput> MapQuestionInputs(IReadOnlyList<QuestionInputDto> src) =>
        src.Select(q => new IntakeQuestionInput(
                q.SortOrder,
                Enum.Parse<IntakeQuestionType>(q.Type!, ignoreCase: false),
                q.Label!.Trim(),
                string.IsNullOrWhiteSpace(q.HelpText) ? null : q.HelpText.Trim(),
                q.IsRequired ?? false,
                string.IsNullOrWhiteSpace(q.DefaultValue) ? null : q.DefaultValue,
                string.IsNullOrWhiteSpace(q.DefaultToken) ? null : q.DefaultToken,
                (q.Options ?? Array.Empty<OptionInputDto>())
                    .Select(o => new IntakeQuestionOptionInput(o.SortOrder, o.Value, o.Label))
                    .ToList()))
            .ToList();

    /// Builds an initial prefill dict keyed by question id. Resolution
    /// order: agent-supplied value wins (from the `create` endpoint), then
    /// the template's literal default, then the resolved token value.
    /// SectionHeader rows never get prefill — they're layout-only.
    private static Dictionary<string, JsonElement> BuildInitialPrefill(
        IntakeTemplate template,
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, JsonElement>? agentOverrides)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var q in template.Questions)
        {
            if (q.Type == IntakeQuestionType.SectionHeader) continue;

            var key = q.Id.ToString();
            if (agentOverrides is not null && agentOverrides.TryGetValue(key, out var provided))
            {
                result[key] = provided.Clone();
                continue;
            }

            string? value = null;
            if (!string.IsNullOrEmpty(q.DefaultToken) && tokens.TryGetValue(q.DefaultToken, out var resolved))
                value = resolved;
            if (string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(q.DefaultValue))
                value = q.DefaultValue;
            if (value is null) continue;

            result[key] = JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();
        }
        return result;
    }

    private static void MergeAgentOverrides(
        Dictionary<string, JsonElement> target,
        JsonDocument previous)
    {
        if (previous.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var kv in previous.RootElement.EnumerateObject())
        {
            target[kv.Name] = kv.Value.Clone();
        }
    }

    private readonly record struct ValidationOutcome(
        int Status,
        string? Error,
        IReadOnlyList<IntakeFormSubmitAnswer>? Answers);

    private static ValidationOutcome ValidateSubmission(
        IReadOnlyList<IntakeQuestion> questions,
        IReadOnlyDictionary<string, JsonElement> answers,
        int maxAnswerBytes,
        int maxTotalBytes)
    {
        var result = new List<IntakeFormSubmitAnswer>(answers.Count);
        long running = 0;

        foreach (var q in questions)
        {
            if (q.Type == IntakeQuestionType.SectionHeader) continue;

            var key = q.Id.ToString();
            if (!answers.TryGetValue(key, out var raw))
            {
                if (q.IsRequired)
                    return new ValidationOutcome(400, $"Question '{q.Label}' is required.", null);
                continue;
            }

            var stringified = raw.GetRawText();
            if (stringified.Length > maxAnswerBytes)
                return new ValidationOutcome(413, $"Answer to '{q.Label}' is too large.", null);
            running += stringified.Length;
            if (running > maxTotalBytes)
                return new ValidationOutcome(413, "Total answer payload too large.", null);

            var (ok, err) = ValidateAnswer(q, raw);
            if (!ok) return new ValidationOutcome(400, err, null);

            result.Add(new IntakeFormSubmitAnswer(q.Id, stringified));
        }

        // Any submitted key that didn't match a real question = reject.
        // Prevents a curious customer from injecting extra JSON keys that
        // land in the answers table and then show up in agent views.
        var questionKeys = questions.Where(q => q.Type != IntakeQuestionType.SectionHeader)
            .Select(q => q.Id.ToString()).ToHashSet();
        foreach (var key in answers.Keys)
        {
            if (!questionKeys.Contains(key))
                return new ValidationOutcome(400, "Unknown question id in answers.", null);
        }

        return new ValidationOutcome(200, null, result);
    }

    private static (bool Ok, string? Error) ValidateAnswer(IntakeQuestion q, JsonElement raw)
    {
        switch (q.Type)
        {
            case IntakeQuestionType.ShortText:
                if (raw.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return (false, $"'{q.Label}' must be a string.");
                if (raw.ValueKind == JsonValueKind.String && raw.GetString()!.Length > 500)
                    return (false, $"'{q.Label}' exceeds 500 characters.");
                return (true, null);
            case IntakeQuestionType.LongText:
                if (raw.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return (false, $"'{q.Label}' must be a string.");
                if (raw.ValueKind == JsonValueKind.String && raw.GetString()!.Length > 10_000)
                    return (false, $"'{q.Label}' exceeds 10000 characters.");
                return (true, null);
            case IntakeQuestionType.Number:
                if (raw.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                    return (false, $"'{q.Label}' must be a number.");
                return (true, null);
            case IntakeQuestionType.YesNo:
                if (raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    return (false, $"'{q.Label}' must be true/false.");
                return (true, null);
            case IntakeQuestionType.Date:
                if (raw.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return (false, $"'{q.Label}' must be an ISO-8601 date string.");
                if (raw.ValueKind == JsonValueKind.String)
                {
                    var s = raw.GetString()!;
                    if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", out _) && !DateTime.TryParse(s, out _))
                        return (false, $"'{q.Label}' must be an ISO-8601 date string.");
                }
                return (true, null);
            case IntakeQuestionType.DropdownSingle:
            {
                if (raw.ValueKind == JsonValueKind.Null) return (true, null);
                if (raw.ValueKind != JsonValueKind.String)
                    return (false, $"'{q.Label}' must be one of the option values.");
                var value = raw.GetString();
                if (!q.Options.Any(o => o.Value == value))
                    return (false, $"'{q.Label}' must match one of its options.");
                return (true, null);
            }
            case IntakeQuestionType.DropdownMulti:
            {
                if (raw.ValueKind == JsonValueKind.Null) return (true, null);
                if (raw.ValueKind != JsonValueKind.Array)
                    return (false, $"'{q.Label}' must be an array of option values.");
                var known = new HashSet<string>(q.Options.Select(o => o.Value), StringComparer.Ordinal);
                foreach (var item in raw.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !known.Contains(item.GetString()!))
                        return (false, $"'{q.Label}' contains an unknown option.");
                }
                return (true, null);
            }
            case IntakeQuestionType.SectionHeader:
                return (false, "Section headers cannot receive answers.");
            default:
                return (false, "Unknown question type.");
        }
    }

    // ============================================================
    // DTO mapping — JSON projection kept separate from domain shape so
    // the contract is stable even if domain records evolve.
    // ============================================================
    private static object MapTemplateDto(IntakeTemplate t) => new
    {
        id = t.Id,
        name = HtmlEncoder.Default.Encode(t.Name),
        description = t.Description is null ? null : HtmlEncoder.Default.Encode(t.Description),
        isActive = t.IsActive,
        createdUtc = t.CreatedUtc,
        updatedUtc = t.UpdatedUtc,
        questions = t.Questions.Select(q => new
        {
            id = q.Id,
            sortOrder = q.SortOrder,
            type = q.Type.ToString(),
            label = HtmlEncoder.Default.Encode(q.Label),
            helpText = q.HelpText is null ? null : HtmlEncoder.Default.Encode(q.HelpText),
            isRequired = q.IsRequired,
            defaultValue = q.DefaultValue,
            defaultToken = q.DefaultToken,
            options = q.Options.Select(o => new
            {
                id = o.Id,
                sortOrder = o.SortOrder,
                value = o.Value,
                label = HtmlEncoder.Default.Encode(o.Label),
            }),
        }),
    };

    private static object MapSummaryDto(IntakeFormInstanceSummary s) => new
    {
        id = s.Id,
        templateId = s.TemplateId,
        templateName = HtmlEncoder.Default.Encode(s.TemplateName),
        status = s.Status.ToString(),
        expiresUtc = s.ExpiresUtc,
        createdUtc = s.CreatedUtc,
        sentUtc = s.SentUtc,
        submittedUtc = s.SubmittedUtc,
        sentToEmail = s.SentToEmail,
    };

    private static object MapAgentViewDto(IntakeFormAgentView v) => new
    {
        instance = new
        {
            id = v.Instance.Id,
            templateId = v.Instance.TemplateId,
            ticketId = v.Instance.TicketId,
            status = v.Instance.Status.ToString(),
            expiresUtc = v.Instance.ExpiresUtc,
            createdUtc = v.Instance.CreatedUtc,
            sentUtc = v.Instance.SentUtc,
            submittedUtc = v.Instance.SubmittedUtc,
            sentToEmail = v.Instance.SentToEmail,
            prefill = v.Instance.Prefill.RootElement.Clone(),
        },
        template = MapTemplateDto(v.Template),
        answers = v.Answers?.RootElement.Clone(),
    };

    private static object MapPublicDto(IntakePublicView v) => new
    {
        templateName = HtmlEncoder.Default.Encode(v.TemplateName),
        templateDescription = v.TemplateDescription is null ? null : HtmlEncoder.Default.Encode(v.TemplateDescription),
        expiresUtc = v.ExpiresUtc,
        questions = v.Questions.Select(q => new
        {
            id = q.Id,
            sortOrder = q.SortOrder,
            type = q.Type.ToString(),
            label = HtmlEncoder.Default.Encode(q.Label),
            helpText = q.HelpText is null ? null : HtmlEncoder.Default.Encode(q.HelpText),
            isRequired = q.IsRequired,
            options = q.Options.Select(o => new
            {
                value = o.Value,
                label = HtmlEncoder.Default.Encode(o.Label),
            }),
        }),
        prefill = v.Prefill.RootElement.Clone(),
    };
}
