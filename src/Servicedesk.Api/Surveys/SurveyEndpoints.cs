using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Domain.Surveys;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Surveys;

namespace Servicedesk.Api.Surveys;

/// HTTP surface for the v0.0.38 surveys feature.
///
/// <list type="bullet">
/// <item><b>Admin</b> — CRUD over <c>surveys</c> + <c>survey_questions</c>
/// at <c>/api/settings/surveys</c>; results aggregation + invitation list at
/// <c>/api/surveys/{id}/results</c> + <c>/api/surveys/{id}/invitations</c>
/// for agent + admin.</item>
/// <item><b>Agent</b> — list-active picker for the compose-template editor
/// and trigger-editor at <c>/api/surveys/usable</c>.</item>
/// <item><b>Public</b> — <c>/api/public/surveys/{token}</c>. No auth, CSRF
/// exempt, rate-limited via the <c>survey-public</c> policy.</item>
/// </list>
public static class SurveyEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 2000;
    private const int MaxIntroHtmlLength = 20_000;
    private const int MaxInviteSubjectLength = 300;
    private const int MaxInviteBodyHtmlLength = 100_000;
    private const int MaxQuestionLabelLength = 500;
    private const int MaxHelpTextLength = 2000;
    private const int MaxOptionCount = 50;
    private const int MaxScalePoints = 10;
    private const int MinScalePoints = 2;
    // Five admin-defined label fields the public page renders. All are
    // required-non-empty at save time except AgentBlockHeading (empty =
    // suppress heading even when Agent-scope questions exist).
    private const int MaxLabelLength = 500;
    private const int MaxMessageLength = 5000;

    public static IEndpointRouteBuilder MapSurveyEndpoints(this IEndpointRouteBuilder app)
    {
        MapAdminEndpoints(app);
        MapAgentEndpoints(app);
        MapPublicEndpoints(app);
        return app;
    }

    // ============================================================
    // Admin — survey CRUD + question editor + results
    // ============================================================
    private static void MapAdminEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/surveys")
            .WithTags("SurveysAdmin")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (
            bool? includeInactive, ISurveyRepository repo, CancellationToken ct) =>
        {
            var surveys = await repo.ListAsync(includeInactive ?? true, ct);
            return Results.Ok(surveys.Select(MapSummaryDto));
        }).WithName("ListSurveys").WithOpenApi();

        group.MapGet("/{id:guid}", async (
            Guid id, ISurveyRepository repo, CancellationToken ct) =>
        {
            var survey = await repo.GetAsync(id, ct);
            return survey is null ? Results.NotFound() : Results.Ok(MapSurveyDto(survey));
        }).WithName("GetSurvey").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] SurveyUpsertRequest req, HttpContext http,
            ISurveyRepository repo, IKbHtmlSanitizer sanitizer,
            ISurveyInviteHtmlSanitizer inviteSanitizer, ISettingsService settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = await ValidateAsync(req, settings, ct);
            if (err is not null) return Results.BadRequest(new { error = err });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var metadata = BuildMetadataInput(req, sanitizer, inviteSanitizer);

            Guid id;
            try
            {
                id = await repo.CreateAsync(metadata, userId, ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_surveys_active_name")
            {
                return Results.Conflict(new { error = "An active survey with that name already exists." });
            }

            if (req.Questions is not null)
            {
                var questions = MapQuestionInputs(req.Questions);
                await repo.ReplaceQuestionsAsync(id, questions, ct);
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "survey.create",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { questions = req.Questions?.Count ?? 0 }));

            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/settings/surveys/{id}", MapSurveyDto(created!));
        }).WithName("CreateSurvey").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] SurveyUpsertRequest req, HttpContext http,
            ISurveyRepository repo, IKbHtmlSanitizer sanitizer,
            ISurveyInviteHtmlSanitizer inviteSanitizer, ISettingsService settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = await ValidateAsync(req, settings, ct);
            if (err is not null) return Results.BadRequest(new { error = err });

            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var metadata = BuildMetadataInput(req, sanitizer, inviteSanitizer);

            try
            {
                await repo.UpdateMetadataAsync(id, metadata, req.IsActive ?? existing.IsActive, ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_surveys_active_name")
            {
                return Results.Conflict(new { error = "Another active survey already uses that name." });
            }

            if (req.Questions is not null)
            {
                var questions = MapQuestionInputs(req.Questions);
                await repo.ReplaceQuestionsAsync(id, questions, ct);
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "survey.update",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { isActive = req.IsActive, questions = req.Questions?.Count }));

            var updated = await repo.GetAsync(id, ct);
            return Results.Ok(MapSurveyDto(updated!));
        }).WithName("UpdateSurvey").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ISurveyRepository repo,
            IAuditLogger audit, CancellationToken ct) =>
        {
            // Repo refuses to hard-delete when responses exist — surface as
            // 409 with a clear message; the admin should deactivate instead
            // to keep historical leaderboards intact.
            if (await repo.HasResponsesAsync(id, ct))
            {
                return Results.Conflict(new
                {
                    error = "Survey has responses. Deactivate it instead of deleting to keep historical results readable.",
                });
            }

            var deleted = await repo.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "survey.delete",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()));

            return Results.NoContent();
        }).WithName("DeleteSurvey").WithOpenApi();

        // Admin-only: results aggregation. Agents can see SurveySent on the
        // timeline as context, but the response body + leaderboard + per-
        // agent breakdown live behind admin role.
        var results = app.MapGroup("/api/surveys")
            .WithTags("SurveysResults")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        results.MapGet("/{id:guid}/results", async (
            Guid id, ISurveyInvitationRepository invitations, CancellationToken ct) =>
        {
            var agg = await invitations.GetAggregateResultsAsync(id, ct);
            return Results.Ok(MapResultsDto(agg));
        }).WithName("GetSurveyResults").WithOpenApi();

        results.MapGet("/{id:guid}/invitations", async (
            Guid id, string? status, int? limit, ISurveyInvitationRepository invitations, CancellationToken ct) =>
        {
            SurveyInvitationStatus? statusFilter = null;
            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<SurveyInvitationStatus>(status, ignoreCase: true, out var parsed))
            {
                statusFilter = parsed;
            }
            var rows = await invitations.ListForSurveyAsync(id, statusFilter, Math.Clamp(limit ?? 50, 1, 200), ct);
            return Results.Ok(rows.Select(MapInvitationSummaryDto));
        }).WithName("ListSurveyInvitations").WithOpenApi();

        results.MapGet("/invitations/{invitationId:guid}", async (
            Guid invitationId, ISurveyInvitationRepository invitations, CancellationToken ct) =>
        {
            var detail = await invitations.GetResponseDetailAsync(invitationId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(MapResponseDetailDto(detail));
        }).WithName("GetSurveyResponse").WithOpenApi();

        // Admin: cancel a Sent invitation manually (retire the link without
        // waiting for expiry).
        var admin = app.MapGroup("/api/settings/surveys/invitations")
            .WithTags("SurveysAdmin")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapPost("/{invitationId:guid}/cancel", async (
            Guid invitationId, HttpContext http,
            ISurveyInvitationRepository invitations, IAuditLogger audit, CancellationToken ct) =>
        {
            var ok = await invitations.CancelSentAsync(invitationId, ct);
            if (!ok) return Results.NotFound();
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "survey.invitation.cancel",
                Actor: actor,
                ActorRole: role,
                Target: invitationId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()));
            return Results.NoContent();
        }).WithName("CancelSurveyInvitation").WithOpenApi();
    }

    // ============================================================
    // Agent — picker + per-ticket invitation list
    // ============================================================
    private static void MapAgentEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/surveys")
            .WithTags("SurveysAgent")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // Used by the compose-template editor + trigger-editor pickers.
        group.MapGet("/usable", async (ISurveyRepository repo, CancellationToken ct) =>
        {
            var rows = await repo.ListAsync(includeInactive: false, ct);
            return Results.Ok(rows.Select(s => new
            {
                id = s.Id,
                name = HtmlEncoder.Default.Encode(s.Name),
                ttlDays = s.TtlDays,
            }));
        }).WithName("ListUsableSurveys").WithOpenApi();

        // Per-ticket invitation list. Admin-only: agents see SurveySent on
        // the timeline but never the response detail or invitation status.
        app.MapGet("/api/tickets/{ticketId:guid}/surveys", async (
            Guid ticketId, ISurveyInvitationRepository invitations, CancellationToken ct) =>
        {
            var rows = await invitations.ListForTicketAsync(ticketId, ct);
            return Results.Ok(rows.Select(MapInvitationSummaryDto));
        })
            .WithTags("SurveysAdmin")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .WithName("ListTicketSurveys")
            .WithOpenApi();
    }

    // ============================================================
    // Public — token-protected GET + submit
    // ============================================================
    private static void MapPublicEndpoints(IEndpointRouteBuilder app)
    {
        // NOTE: NOT calling RequireAuthorization. CSRF exempt via
        // DoubleSubmitCsrfMiddleware.ExemptPrefixes. Rate-limited per
        // (IP, token) via the "survey-public" policy.
        var group = app.MapGroup("/api/public/surveys")
            .WithTags("SurveysPublic")
            .RequireRateLimiting("survey-public");

        group.MapGet("/{token}", async (
            string token, ISurveyTokenService tokenSvc,
            ISurveyInvitationRepository invitations, CancellationToken ct) =>
        {
            var hash = tokenSvc.HashForLookup(token);
            if (hash is null) return Results.NotFound();

            var view = await invitations.GetByTokenHashForPublicAsync(hash, ct);
            if (view is null) return Results.NotFound();

            // The public page renders each lifecycle state from admin-
            // supplied text on the survey, so we surface the relevant
            // label even on expired/submitted to avoid a blank screen.
            if (view.Status == SurveyInvitationStatus.Expired || view.ExpiresUtc <= DateTime.UtcNow)
                return Results.Json(new { status = "expired", message = view.ExpiredMessage }, statusCode: StatusCodes.Status410Gone);
            if (view.Status == SurveyInvitationStatus.Submitted)
                return Results.Json(new { status = "submitted", message = view.ThankYouMessage }, statusCode: StatusCodes.Status409Conflict);

            return Results.Ok(MapPublicDto(view));
        }).WithName("GetPublicSurvey").WithOpenApi();

        group.MapPost("/{token}/submit", async (
            string token, [FromBody] PublicSubmitRequest req, HttpContext http,
            ISurveyTokenService tokenSvc, ISurveyResponseService response,
            ISurveyInvitationRepository invitations,
            ISettingsService settings, IHubContext<TicketPresenceHub> hub,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var hash = tokenSvc.HashForLookup(token);
            if (hash is null) return Results.NotFound();

            if (req is null) return Results.BadRequest(new { error = "Body is required." });

            var view = await invitations.GetByTokenHashForPublicAsync(hash, ct);
            if (view is null) return Results.NotFound();
            if (view.Status != SurveyInvitationStatus.Sent)
            {
                return Results.Json(new { status = view.Status.ToString().ToLowerInvariant() },
                    statusCode: view.Status == SurveyInvitationStatus.Expired ? 410 : 409);
            }

            var maxComment = await settings.GetAsync<int>(SettingKeys.Surveys.MaxCommentLength, ct);
            if (req.Comment is not null && req.Comment.Length > Math.Max(maxComment, 200))
            {
                return Results.Json(new { error = $"Comment exceeds the {maxComment}-character limit." }, statusCode: 413);
            }

            var validation = ValidateSubmission(view, req);
            if (validation.Error is not null)
                return Results.Json(new { error = validation.Error }, statusCode: validation.Status);

            var ip = http.Connection.RemoteIpAddress?.ToString();
            var ua = http.Request.Headers.UserAgent.ToString();

            var input = new SurveySubmitInput(
                Comment: req.Comment,
                Answers: validation.Answers!,
                AgentAnswers: validation.AgentAnswers!);

            var result = await response.SubmitAsync(hash, input, ip, ua, ct);
            if (result is null)
                return Results.Conflict(new { status = "submitted-or-expired" });

            await audit.LogAsync(new AuditEvent(
                EventType: "survey.submit",
                Actor: "customer",
                ActorRole: "anon",
                Target: result.InvitationId.ToString(),
                ClientIp: ip,
                UserAgent: ua,
                Payload: new
                {
                    ticketId = result.TicketId,
                    surveyId = result.SurveyId,
                    answerCount = validation.Answers!.Count,
                    agentAnswerCount = validation.AgentAnswers!.Count,
                }));

            // SignalR broadcast (carve-out: SurveyResponseService does NOT
            // fire the trigger evaluator, so reopen-rules never trigger on
            // SurveySubmitted).
            var ticketIdStr = result.TicketId.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            return Results.Ok(new
            {
                status = "submitted",
                surveyName = result.SurveyName,
                message = view.ThankYouMessage,
            });
        }).WithName("SubmitPublicSurvey").WithOpenApi();
    }

    // ============================================================
    // Request / response DTOs
    // ============================================================
    public sealed record SurveyUpsertRequest(
        [Required] string? Name,
        string? Description,
        string? IntroHtml,
        string? InviteSubject,
        string? InviteBodyHtml,
        bool? IsActive,
        int? TtlDays,
        string? AgentBlockHeading,
        string? SubmitButtonLabel,
        string? ThankYouMessage,
        string? ExpiredMessage,
        string? NotFoundMessage,
        IReadOnlyList<QuestionInputDto>? Questions);

    public sealed record QuestionInputDto(
        int SortOrder,
        string? Type,
        /// "Survey" (default) or "Agent". Agent-scope questions are
        /// rendered once per contributing agent at submit time.
        string? AppliesTo,
        string? Label,
        string? HelpText,
        bool? IsRequired,
        // Type-specific config. Caller wins — the server clamps obvious
        // garbage (negative points, empty option lists) but otherwise trusts
        // the structure. Empty/null = empty object on disk.
        JsonElement? Config);

    public sealed record PublicSubmitRequest(
        string? Comment,
        /// Answers to Survey-scoped questions (one per question id).
        IReadOnlyList<PublicAnswerDto>? Answers,
        /// Answers to Agent-scoped questions (one per (agent, question) pair).
        IReadOnlyList<PublicAgentAnswerDto>? AgentAnswers);

    public sealed record PublicAnswerDto(long QuestionId, JsonElement? Value);

    public sealed record PublicAgentAnswerDto(Guid AgentUserId, long QuestionId, JsonElement? Value);

    // ============================================================
    // Validation
    // ============================================================
    private static async Task<string?> ValidateAsync(SurveyUpsertRequest req, ISettingsService settings, CancellationToken ct)
    {
        if (req is null) return "Body is required.";
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > MaxNameLength)
            return $"Name is required and must be ≤{MaxNameLength} characters.";
        if (req.Description is not null && req.Description.Length > MaxDescriptionLength)
            return $"Description must be ≤{MaxDescriptionLength} characters.";
        if (req.IntroHtml is not null && req.IntroHtml.Length > MaxIntroHtmlLength)
            return $"Intro must be ≤{MaxIntroHtmlLength} characters.";
        if (req.InviteSubject is not null && req.InviteSubject.Length > MaxInviteSubjectLength)
            return $"Invite subject must be ≤{MaxInviteSubjectLength} characters.";
        if (req.InviteBodyHtml is not null && req.InviteBodyHtml.Length > MaxInviteBodyHtmlLength)
            return $"Invite body must be ≤{MaxInviteBodyHtmlLength} characters.";
        if (req.TtlDays is int ttl && (ttl < 1 || ttl > 365))
            return "ttlDays must be between 1 and 365 (leave blank for the system default).";

        // Required label fields: every string the customer sees must be
        // admin-supplied so a Dutch tenant never gets an English fallback.
        // AgentBlockHeading is the one exception — empty is a valid "no
        // heading" choice.
        if (string.IsNullOrWhiteSpace(req.SubmitButtonLabel))
            return "submitButtonLabel is required (this is the text on the public Submit button).";
        if (req.SubmitButtonLabel.Length > MaxLabelLength)
            return $"submitButtonLabel must be ≤{MaxLabelLength} characters.";
        if (string.IsNullOrWhiteSpace(req.ThankYouMessage))
            return "thankYouMessage is required (shown after the customer submits).";
        if (req.ThankYouMessage.Length > MaxMessageLength)
            return $"thankYouMessage must be ≤{MaxMessageLength} characters.";
        if (string.IsNullOrWhiteSpace(req.ExpiredMessage))
            return "expiredMessage is required (shown when the survey link has expired).";
        if (req.ExpiredMessage.Length > MaxMessageLength)
            return $"expiredMessage must be ≤{MaxMessageLength} characters.";
        if (string.IsNullOrWhiteSpace(req.NotFoundMessage))
            return "notFoundMessage is required (shown when the survey link is invalid).";
        if (req.NotFoundMessage.Length > MaxMessageLength)
            return $"notFoundMessage must be ≤{MaxMessageLength} characters.";
        if (req.AgentBlockHeading is not null && req.AgentBlockHeading.Length > MaxLabelLength)
            return $"agentBlockHeading must be ≤{MaxLabelLength} characters.";

        if (req.Questions is null) return null;

        var maxQuestions = await settings.GetAsync<int>(SettingKeys.Surveys.MaxQuestionsPerSurvey, ct);
        if (req.Questions.Count > maxQuestions)
            return $"Too many questions (max {maxQuestions}).";

        // Sort-order is unique within (scope) — Survey and Agent question
        // lists are independent in the designer.
        var seenSurveyOrders = new HashSet<int>();
        var seenAgentOrders = new HashSet<int>();
        foreach (var q in req.Questions)
        {
            var scope = q.AppliesTo;
            if (string.IsNullOrWhiteSpace(scope)) scope = "Survey";
            if (!Enum.TryParse<SurveyQuestionScope>(scope, ignoreCase: false, out var scopeEnum))
                return $"appliesTo must be one of {string.Join(", ", Enum.GetNames<SurveyQuestionScope>())}.";

            var bucket = scopeEnum == SurveyQuestionScope.Agent ? seenAgentOrders : seenSurveyOrders;
            if (!bucket.Add(q.SortOrder))
                return $"sortOrder values must be unique within '{scopeEnum}' questions.";

            if (string.IsNullOrWhiteSpace(q.Label) || q.Label.Length > MaxQuestionLabelLength)
                return $"Every question needs a label ≤{MaxQuestionLabelLength} characters.";
            if (q.HelpText is not null && q.HelpText.Length > MaxHelpTextLength)
                return $"helpText must be ≤{MaxHelpTextLength} characters.";
            if (string.IsNullOrWhiteSpace(q.Type) ||
                !Enum.TryParse<SurveyQuestionType>(q.Type, ignoreCase: false, out var parsed))
            {
                return $"Question type must be one of {string.Join(", ", Enum.GetNames<SurveyQuestionType>())}.";
            }

            var err = ValidateQuestionConfig(parsed, q.Config);
            if (err is not null) return err;
        }
        return null;
    }

    private static string? ValidateQuestionConfig(SurveyQuestionType type, JsonElement? config)
    {
        switch (type)
        {
            case SurveyQuestionType.Rating:
                if (config is not JsonElement c || c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Number)
                    return "Rating questions need config.points (number of scale points).";
                if (!pts.TryGetInt32(out var pointsValue) || pointsValue < MinScalePoints || pointsValue > MaxScalePoints)
                    return $"Rating points must be between {MinScalePoints} and {MaxScalePoints}.";
                if (c.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array && labels.GetArrayLength() > 0 && labels.GetArrayLength() != pointsValue)
                    return "When labels are provided for a rating question, the array length must equal points.";
                return null;
            case SurveyQuestionType.SingleChoice:
            case SurveyQuestionType.MultiChoice:
                if (config is not JsonElement cc || cc.ValueKind != JsonValueKind.Object || !cc.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array || opts.GetArrayLength() == 0)
                    return "Choice questions need config.options (non-empty array of {value,label}).";
                if (opts.GetArrayLength() > MaxOptionCount)
                    return $"At most {MaxOptionCount} options per choice question.";
                foreach (var opt in opts.EnumerateArray())
                {
                    if (opt.ValueKind != JsonValueKind.Object) return "Each option must be an object {value, label}.";
                    if (!opt.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) return "Each option needs a string 'value'.";
                    if (!opt.TryGetProperty("label", out var l) || l.ValueKind != JsonValueKind.String) return "Each option needs a string 'label'.";
                }
                return null;
            default:
                return null;
        }
    }

    private static ValidationResult ValidateSubmission(SurveyPublicView view, PublicSubmitRequest req)
    {
        var surveyById = view.Questions.ToDictionary(q => q.Id);
        var agentQuestionById = view.AgentQuestions.ToDictionary(q => q.Id);
        var attributedIds = view.AttributedAgents.Select(a => a.UserId).ToHashSet();

        var answers = new List<SurveySubmitAnswer>(req.Answers?.Count ?? 0);
        var agentAnswers = new List<SurveySubmitAgentAnswer>(req.AgentAnswers?.Count ?? 0);

        // Survey-scope answers.
        foreach (var a in req.Answers ?? Array.Empty<PublicAnswerDto>())
        {
            if (!surveyById.TryGetValue(a.QuestionId, out var question))
                return new ValidationResult(400, $"Unknown questionId {a.QuestionId}.");
            if (a.Value is null || a.Value.Value.ValueKind == JsonValueKind.Null)
            {
                if (question.IsRequired) return new ValidationResult(400, $"Question '{question.Label}' is required.");
                continue;
            }

            var (numeric, text, json, err) = NormalizeAnswer(question, a.Value.Value);
            if (err is not null) return new ValidationResult(400, err);
            answers.Add(new SurveySubmitAnswer(question.Id, numeric, text, json));
        }

        foreach (var q in view.Questions)
        {
            if (!q.IsRequired) continue;
            if (!answers.Any(ans => ans.QuestionId == q.Id))
                return new ValidationResult(400, $"Question '{q.Label}' is required.");
        }

        // Agent-scope answers. Each (agent, question) pair is independent;
        // we de-dupe the (agent, question) key so a buggy client can't
        // double-insert into the partial unique index downstream.
        var seenPairs = new HashSet<(Guid, long)>();
        foreach (var aa in req.AgentAnswers ?? Array.Empty<PublicAgentAnswerDto>())
        {
            if (!attributedIds.Contains(aa.AgentUserId))
                return new ValidationResult(400, "Unknown agentUserId in agentAnswers.");
            if (!agentQuestionById.TryGetValue(aa.QuestionId, out var question))
                return new ValidationResult(400, $"Unknown agent-question id {aa.QuestionId}.");
            if (!seenPairs.Add((aa.AgentUserId, aa.QuestionId)))
                return new ValidationResult(400, "Duplicate (agentUserId, questionId) pair in agentAnswers.");

            if (aa.Value is null || aa.Value.Value.ValueKind == JsonValueKind.Null)
            {
                if (question.IsRequired) return new ValidationResult(400, $"Agent question '{question.Label}' is required.");
                continue;
            }

            var (numeric, text, json, err) = NormalizeAnswer(question, aa.Value.Value);
            if (err is not null) return new ValidationResult(400, err);
            agentAnswers.Add(new SurveySubmitAgentAnswer(aa.AgentUserId, question.Id, numeric, text, json));
        }

        // Required agent-question check: every required Agent-scope
        // question must be answered for every attributed agent.
        foreach (var q in view.AgentQuestions)
        {
            if (!q.IsRequired) continue;
            foreach (var agent in view.AttributedAgents)
            {
                if (!agentAnswers.Any(a => a.QuestionId == q.Id && a.AgentUserId == agent.UserId))
                    return new ValidationResult(400, $"Agent question '{q.Label}' is required for {agent.DisplayName}.");
            }
        }

        return new ValidationResult(200, null, answers, agentAnswers);
    }

    private static (decimal? num, string? text, string? json, string? err) NormalizeAnswer(SurveyQuestion question, JsonElement value)
    {
        switch (question.Type)
        {
            case SurveyQuestionType.Rating:
            case SurveyQuestionType.Nps:
                if (value.ValueKind != JsonValueKind.Number) return (null, null, null, $"Question '{question.Label}' expects a number.");
                if (!value.TryGetDecimal(out var n)) return (null, null, null, $"Question '{question.Label}' has an invalid number.");
                if (question.Type == SurveyQuestionType.Nps && (n < 0 || n > 10)) return (null, null, null, "NPS values must be between 0 and 10.");
                return (n, null, null, null);
            case SurveyQuestionType.Text:
                if (value.ValueKind != JsonValueKind.String) return (null, null, null, $"Question '{question.Label}' expects a string.");
                return (null, value.GetString(), null, null);
            case SurveyQuestionType.SingleChoice:
                if (value.ValueKind != JsonValueKind.String) return (null, null, null, $"Question '{question.Label}' expects a string option value.");
                return (null, value.GetString(), null, null);
            case SurveyQuestionType.MultiChoice:
                if (value.ValueKind != JsonValueKind.Array) return (null, null, null, $"Question '{question.Label}' expects an array of option values.");
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String) return (null, null, null, $"Multi-choice values must be strings.");
                return (null, null, value.GetRawText(), null);
            default:
                return (null, null, null, $"Unsupported question type {question.Type}.");
        }
    }

    private sealed record ValidationResult(
        int Status,
        string? Error,
        IReadOnlyList<SurveySubmitAnswer>? Answers = null,
        IReadOnlyList<SurveySubmitAgentAnswer>? AgentAnswers = null);

    // ============================================================
    // Mapping helpers
    // ============================================================
    private static SurveyMetadataInput BuildMetadataInput(
        SurveyUpsertRequest req, IKbHtmlSanitizer sanitizer, ISurveyInviteHtmlSanitizer inviteSanitizer)
    {
        return new SurveyMetadataInput(
            Name: req.Name!.Trim(),
            Description: string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            IntroHtml: sanitizer.Sanitize(req.IntroHtml ?? string.Empty),
            InviteSubject: (req.InviteSubject ?? string.Empty).Trim(),
            // Invite body uses the token-aware sanitizer so `href="{{survey.link}}"`
            // survives the save and is substituted at dispatch time.
            InviteBodyHtml: inviteSanitizer.Sanitize(req.InviteBodyHtml ?? string.Empty),
            TtlDays: req.TtlDays,
            AgentBlockHeading: string.IsNullOrWhiteSpace(req.AgentBlockHeading) ? null : req.AgentBlockHeading.Trim(),
            SubmitButtonLabel: req.SubmitButtonLabel!.Trim(),
            ThankYouMessage: req.ThankYouMessage!.Trim(),
            ExpiredMessage: req.ExpiredMessage!.Trim(),
            NotFoundMessage: req.NotFoundMessage!.Trim());
    }

    private static IReadOnlyList<SurveyQuestionInput> MapQuestionInputs(IReadOnlyList<QuestionInputDto> dtos)
    {
        return dtos.Select(d => new SurveyQuestionInput(
            SortOrder: d.SortOrder,
            Type: Enum.Parse<SurveyQuestionType>(d.Type!, ignoreCase: false),
            AppliesTo: Enum.Parse<SurveyQuestionScope>(
                string.IsNullOrWhiteSpace(d.AppliesTo) ? "Survey" : d.AppliesTo, ignoreCase: false),
            Label: d.Label!.Trim(),
            HelpText: string.IsNullOrWhiteSpace(d.HelpText) ? null : d.HelpText.Trim(),
            IsRequired: d.IsRequired ?? false,
            ConfigJson: d.Config is null ? "{}" : d.Config.Value.GetRawText())).ToList();
    }

    private static object MapSummaryDto(SurveySummary s) => new
    {
        id = s.Id,
        name = HtmlEncoder.Default.Encode(s.Name),
        description = s.Description is null ? null : HtmlEncoder.Default.Encode(s.Description),
        isActive = s.IsActive,
        ttlDays = s.TtlDays,
        questionCount = s.QuestionCount,
        agentQuestionCount = s.AgentQuestionCount,
        invitationCount = s.InvitationCount,
        responseCount = s.ResponseCount,
        createdUtc = s.CreatedUtc,
        updatedUtc = s.UpdatedUtc,
    };

    private static object MapSurveyDto(Survey s) => new
    {
        id = s.Id,
        name = s.Name,
        description = s.Description,
        introHtml = s.IntroHtml,
        inviteSubject = s.InviteSubject,
        inviteBodyHtml = s.InviteBodyHtml,
        isActive = s.IsActive,
        ttlDays = s.TtlDays,
        agentBlockHeading = s.AgentBlockHeading,
        submitButtonLabel = s.SubmitButtonLabel,
        thankYouMessage = s.ThankYouMessage,
        expiredMessage = s.ExpiredMessage,
        notFoundMessage = s.NotFoundMessage,
        createdUtc = s.CreatedUtc,
        updatedUtc = s.UpdatedUtc,
        questions = s.Questions.Select(MapQuestionDto),
    };

    private static object MapQuestionDto(SurveyQuestion q) => new
    {
        id = q.Id,
        sortOrder = q.SortOrder,
        type = q.Type.ToString(),
        appliesTo = q.AppliesTo.ToString(),
        label = q.Label,
        helpText = q.HelpText,
        isRequired = q.IsRequired,
        config = q.Config.RootElement.Clone(),
    };

    private static object MapPublicDto(SurveyPublicView v) => new
    {
        invitationId = v.InvitationId,
        surveyId = v.SurveyId,
        surveyName = v.SurveyName,
        introHtml = v.IntroHtml,
        agentBlockHeading = v.AgentBlockHeading,
        submitButtonLabel = v.SubmitButtonLabel,
        thankYouMessage = v.ThankYouMessage,
        expiredMessage = v.ExpiredMessage,
        notFoundMessage = v.NotFoundMessage,
        status = v.Status.ToString(),
        expiresUtc = v.ExpiresUtc,
        attributedAgents = v.AttributedAgents.Select(a => new { userId = a.UserId, displayName = a.DisplayName }),
        questions = v.Questions.Select(MapQuestionDto),
        agentQuestions = v.AgentQuestions.Select(MapQuestionDto),
    };

    private static object MapInvitationSummaryDto(SurveyInvitationSummary i) => new
    {
        id = i.Id,
        surveyId = i.SurveyId,
        surveyName = HtmlEncoder.Default.Encode(i.SurveyName),
        ticketId = i.TicketId,
        ticketNumber = i.TicketNumber,
        ticketSubject = i.TicketSubject,
        status = i.Status.ToString(),
        sentToEmail = i.SentToEmail,
        sentUtc = i.SentUtc,
        expiresUtc = i.ExpiresUtc,
        submittedUtc = i.SubmittedUtc,
        contactName = i.ContactName,
        companyName = i.CompanyName,
    };

    private static object MapResultsDto(SurveyAggregateResults r) => new
    {
        surveyId = r.SurveyId,
        totalSent = r.TotalSent,
        totalSubmitted = r.TotalSubmitted,
        totalExpired = r.TotalExpired,
        totalCancelled = r.TotalCancelled,
        responseRate = r.TotalSent == 0 ? 0d : Math.Round((double)r.TotalSubmitted / r.TotalSent, 4),
        agentLeaderboard = r.AgentLeaderboard.Select(a => new
        {
            agentUserId = a.AgentUserId,
            displayName = a.DisplayName,
            responseCount = a.ResponseCount,
            averageRating = a.AverageRating,
        }),
        questionAggregates = r.QuestionAggregates.Select(q => new
        {
            questionId = q.QuestionId,
            label = q.Label,
            type = q.Type.ToString(),
            answerCount = q.AnswerCount,
            averageNumeric = q.AverageNumeric,
            tally = q.Tally is null ? null : (object)q.Tally.RootElement.Clone(),
        }),
        agentQuestionAggregates = r.AgentQuestionAggregates.Select(q => new
        {
            questionId = q.QuestionId,
            label = q.Label,
            type = q.Type.ToString(),
            agentUserId = q.AgentUserId,
            agentDisplayName = q.AgentDisplayName,
            answerCount = q.AnswerCount,
            averageNumeric = q.AverageNumeric,
            tally = q.Tally is null ? null : (object)q.Tally.RootElement.Clone(),
        }),
    };

    private static object MapResponseDetailDto(SurveyResponseDetail d) => new
    {
        invitationId = d.InvitationId,
        ticketId = d.TicketId,
        ticketNumber = d.TicketNumber,
        ticketSubject = d.TicketSubject,
        sentToEmail = d.SentToEmail,
        sentUtc = d.SentUtc,
        submittedUtc = d.SubmittedUtc,
        comment = d.Comment,
        surveySnapshot = d.SurveySnapshot.RootElement.Clone(),
        answers = d.Answers.Select(a => new
        {
            questionId = a.QuestionId,
            valueNumeric = a.ValueNumeric,
            valueText = a.ValueText,
            valueJson = a.ValueJson is null ? null : (object)a.ValueJson.RootElement.Clone(),
        }),
        agentAnswers = d.AgentAnswers.Select(a => new
        {
            agentUserId = a.AgentUserId,
            agentDisplayName = a.AgentDisplayName,
            questionId = a.QuestionId,
            valueNumeric = a.ValueNumeric,
            valueText = a.ValueText,
            valueJson = a.ValueJson is null ? null : (object)a.ValueJson.RootElement.Clone(),
        }),
    };

    // ============================================================
    // Static metadata endpoints for the designer
    // ============================================================
    public static void MapSurveyTokenMetadata(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/surveys/tokens", () => Results.Ok(new
        {
            // The survey designer reuses compose tokens (contact / ticket /
            // agent placeholders) plus the survey-specific ones below.
            extraTokens = new[]
            {
                new { token = SurveyTokens.SurveyLink, label = "Survey · Link (always required)" },
                new { token = SurveyTokens.TicketAgentNames, label = "Ticket · Agent names (comma-separated)" },
            },
        }))
            .WithTags("SurveysAdmin")
            .WithName("ListSurveyTokens")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .WithOpenApi();
    }
}
