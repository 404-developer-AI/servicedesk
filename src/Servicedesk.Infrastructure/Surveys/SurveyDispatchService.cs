using System.Net.Mail;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Domain.ComposeTemplates;
using Servicedesk.Domain.Surveys;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.ComposeTemplates;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Surveys;

/// Single dispatch path shared by the <c>send_survey</c> trigger action and
/// the compose-template "linked survey" hook. Resolves contributing agents,
/// renders the invitation mail (compose tokens + the new survey-specific
/// tokens), mints a token, persists the invitation, and ships the mail via
/// Graph. All side-effects atomic within the repo transaction; the mail
/// send happens after commit so a Graph hiccup never leaves an orphan row.
public interface ISurveyDispatchService
{
    /// Idempotency contract: if an active (status=Sent) invitation already
    /// exists for this (survey, ticket) pair, returns <see cref="DispatchSkipReason.AlreadyActive"/>
    /// without sending a duplicate. Other skip reasons surface for missing
    /// recipient, inactive survey, etc.
    Task<SurveyDispatchOutcome> DispatchAsync(SurveyDispatchRequest request, CancellationToken ct);
}

public sealed record SurveyDispatchRequest(
    Guid SurveyId,
    Guid TicketId,
    /// Optional TTL override (days). When null the survey-level TTL is used,
    /// falling back to <c>SettingKeys.Surveys.DefaultTtlDays</c>.
    int? TtlDaysOverride,
    /// Optional recipient override (single address). When null the ticket's
    /// requester contact email is used.
    string? RecipientOverride,
    /// User id of the actor — agent for compose-template-linked dispatch,
    /// nullable for trigger-driven dispatch (system actor).
    Guid? ActorUserId);

public sealed record SurveyDispatchOutcome(
    SurveyDispatchStatus Status,
    Guid? InvitationId = null,
    long? SentEventId = null,
    string? Reason = null,
    string? RawToken = null);

public enum SurveyDispatchStatus
{
    Sent,
    Skipped,
    Failed,
}

public enum DispatchSkipReason
{
    AlreadyActive,
    SurveyInactive,
    SurveyNotFound,
    TicketNotFound,
    NoRecipient,
    NoMailbox,
}

public sealed class SurveyDispatchService : ISurveyDispatchService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISurveyRepository _surveys;
    private readonly ISurveyInvitationRepository _invitations;
    private readonly ISurveyTokenService _tokenService;
    private readonly ITicketRepository _tickets;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ICompanyRepository _companies;
    private readonly IComposeTokenResolver _composeTokens;
    private readonly ISettingsService _settings;
    private readonly IGraphMailClient _graph;
    private readonly ILogger<SurveyDispatchService> _logger;

    public SurveyDispatchService(
        NpgsqlDataSource dataSource,
        ISurveyRepository surveys,
        ISurveyInvitationRepository invitations,
        ISurveyTokenService tokenService,
        ITicketRepository tickets,
        ITaxonomyRepository taxonomy,
        ICompanyRepository companies,
        IComposeTokenResolver composeTokens,
        ISettingsService settings,
        IGraphMailClient graph,
        ILogger<SurveyDispatchService> logger)
    {
        _dataSource = dataSource;
        _surveys = surveys;
        _invitations = invitations;
        _tokenService = tokenService;
        _tickets = tickets;
        _taxonomy = taxonomy;
        _companies = companies;
        _composeTokens = composeTokens;
        _settings = settings;
        _graph = graph;
        _logger = logger;
    }

    public async Task<SurveyDispatchOutcome> DispatchAsync(SurveyDispatchRequest request, CancellationToken ct)
    {
        var ticketDetail = await _tickets.GetByIdAsync(request.TicketId, ct);
        if (ticketDetail is null)
        {
            return new SurveyDispatchOutcome(SurveyDispatchStatus.Skipped,
                Reason: nameof(DispatchSkipReason.TicketNotFound));
        }
        var ticket = ticketDetail.Ticket;

        var survey = await _surveys.GetAsync(request.SurveyId, ct);
        if (survey is null)
        {
            return new SurveyDispatchOutcome(SurveyDispatchStatus.Skipped,
                Reason: nameof(DispatchSkipReason.SurveyNotFound));
        }
        if (!survey.IsActive)
        {
            return new SurveyDispatchOutcome(SurveyDispatchStatus.Skipped,
                Reason: nameof(DispatchSkipReason.SurveyInactive));
        }

        // Early idempotency check — saves a token-mint + mail-render when
        // the partial unique index would just trip anyway. The repo also
        // re-checks under transaction so a concurrent dispatch is still
        // safely deduplicated.
        if (await _invitations.ActiveExistsAsync(survey.Id, ticket.Id, ct))
        {
            return new SurveyDispatchOutcome(SurveyDispatchStatus.Skipped,
                Reason: nameof(DispatchSkipReason.AlreadyActive));
        }

        // Recipient resolution: explicit override > ticket requester email.
        var recipient = request.RecipientOverride;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            var contact = await _companies.GetContactAsync(ticket.RequesterContactId, ct);
            recipient = contact?.Email;
        }
        if (string.IsNullOrWhiteSpace(recipient) || !IsValidEmail(recipient!))
        {
            return new SurveyDispatchOutcome(SurveyDispatchStatus.Skipped,
                Reason: nameof(DispatchSkipReason.NoRecipient));
        }

        var queue = await _taxonomy.GetQueueAsync(ticket.QueueId, ct);
        var fromMailbox = FirstNonEmpty(queue?.OutboundMailboxAddress, queue?.InboundMailboxAddress);
        if (string.IsNullOrWhiteSpace(fromMailbox))
        {
            return new SurveyDispatchOutcome(SurveyDispatchStatus.Skipped,
                Reason: nameof(DispatchSkipReason.NoMailbox));
        }

        // Attributed agents: snapshot taken at send time so reassignments
        // afterwards don't reshape the per-agent rating-blocks the customer
        // sees. Includes anyone who authored a contributing event + the
        // current assignee, de-duplicated and ordered by first contribution.
        var attributedAgents = await ResolveContributingAgentsAsync(ticket, ct);

        // TTL precedence: trigger-action override > survey-level TTL > global default.
        var ttlDays = request.TtlDaysOverride
                      ?? survey.TtlDays
                      ?? await _settings.GetAsync<int>(SettingKeys.Surveys.DefaultTtlDays, ct);
        if (ttlDays <= 0) ttlDays = 7;
        var nowUtc = DateTime.UtcNow;
        var expiresUtc = nowUtc.AddDays(ttlDays);

        var (rawToken, hash, cipher) = _tokenService.Mint();

        // Render the invite body + subject with compose tokens AND the
        // survey-specific {{survey.link}} / {{ticket.agentNames}} tokens.
        // The agent-email used for {{agent.email}} comes from the first
        // attributed agent (or the actor) — survey emails are signed by the
        // helpdesk in general so this is a hint, not a contract.
        var composeMap = await _composeTokens.ResolveForTicketAsync(ticket.Id, agentEmail: null, ct);
        var publicBaseUrl = await _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct);
        var surveyLink = BuildSurveyLink(publicBaseUrl, rawToken);
        var agentNames = string.Join(", ", attributedAgents.Select(a => a.Email).Where(e => !string.IsNullOrWhiteSpace(e)));

        var tokens = new Dictionary<string, string>(composeMap, StringComparer.Ordinal)
        {
            [SurveyTokens.SurveyLink] = surveyLink,
            [SurveyTokens.TicketAgentNames] = agentNames,
        };

        var subject = ApplyTokens(survey.InviteSubject, tokens);
        if (string.IsNullOrWhiteSpace(subject)) subject = $"How did we do? Ticket #{ticket.Number}";
        var bodyHtml = ApplyTokens(survey.InviteBodyHtml, tokens);
        if (string.IsNullOrWhiteSpace(bodyHtml))
        {
            // Fallback body when an admin saved an empty invite template —
            // we still want the link to land. Plain HTML, no styling, gets
            // the job done.
            bodyHtml = $"<p>Hi,</p><p>We'd like a minute of your time on ticket <strong>#{ticket.Number}</strong>.</p>" +
                       $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(surveyLink)}\">Open the survey</a></p>";
        }

        var snapshotJson = BuildSnapshotJson(survey);

        var fromName = await _settings.GetAsync<string>(SettingKeys.Surveys.InviteFromName, ct);
        if (string.IsNullOrWhiteSpace(fromName))
            fromName = !string.IsNullOrWhiteSpace(queue?.Name) ? queue!.Name : fromMailbox!;

        var sentEventMetadata = JsonSerializer.Serialize(new
        {
            surveyId = survey.Id,
            surveyName = survey.Name,
            sentTo = recipient,
            expiresUtc,
            attributedAgentIds = attributedAgents.Select(a => a.Id),
        });

        var created = await _invitations.CreateSentAsync(
            surveyId: survey.Id,
            ticketId: ticket.Id,
            tokenHash: hash,
            tokenCipher: cipher,
            sentToEmail: recipient!,
            expiresUtc: expiresUtc,
            attributedAgentIds: attributedAgents.Select(a => a.Id).ToList(),
            surveySnapshotJson: snapshotJson,
            createdBy: request.ActorUserId,
            sentEventMetadataJson: sentEventMetadata,
            ct: ct);

        if (created is null)
        {
            // Lost the race against another dispatch — same idempotency
            // outcome as the early check, just observed later.
            return new SurveyDispatchOutcome(SurveyDispatchStatus.Skipped,
                Reason: nameof(DispatchSkipReason.AlreadyActive));
        }

        // Ship the mail OUTSIDE the DB transaction so a Graph hiccup doesn't
        // roll back the invitation row (the link survives and the admin can
        // see + resend it from the agent-side timeline).
        try
        {
            var graphMsg = new GraphOutboundMessage(
                FromMailbox: fromMailbox!,
                Subject: subject,
                BodyHtml: bodyHtml,
                To: new[] { new GraphRecipient(recipient!, recipient!) },
                Cc: Array.Empty<GraphRecipient>(),
                Bcc: Array.Empty<GraphRecipient>(),
                ReplyTo: new[] { new GraphRecipient(fromMailbox!, fromName!) },
                Attachments: null,
                InternetMessageHeaders: new[]
                {
                    new GraphOutboundHeader("X-Auto-Submitted", "auto-generated"),
                    new GraphOutboundHeader("X-Servicedesk-Survey-Invitation", created.InvitationId.ToString()),
                });
            await _graph.SendMailAsync(graphMsg, ct);
        }
        catch (Exception ex)
        {
            // We log + return Sent because the invitation row landed; the
            // admin can resend from the timeline. Surfacing as Failed here
            // would tempt the caller (e.g. the trigger evaluator) to retry
            // and create a duplicate invitation on the next match.
            _logger.LogError(ex,
                "Survey {SurveyId} invitation {InvitationId} on ticket {TicketId}: Graph send failed; invitation persisted but no mail dispatched.",
                survey.Id, created.InvitationId, ticket.Id);
        }

        return new SurveyDispatchOutcome(
            Status: SurveyDispatchStatus.Sent,
            InvitationId: created.InvitationId,
            SentEventId: created.SentEventId,
            RawToken: rawToken);
    }

    private async Task<IReadOnlyList<ContributingAgent>> ResolveContributingAgentsAsync(Ticket ticket, CancellationToken ct)
    {
        // Anyone who authored a Note/Comment/MailSent/StatusChange/AssignmentChange
        // event is considered to have contributed. The current assignee is
        // unioned in even if they never wrote anything (e.g. ticket created
        // by mail-ingest, assigned but never touched). Customer-authored
        // events are filtered by author_user_id IS NOT NULL.
        //
        // UNION (not UNION ALL) inside the CTE deduplicates user_ids before
        // we ever touch the users table, which sidesteps Postgres' rule that
        // SELECT DISTINCT + ORDER BY must reference identical expressions
        // (a citext column vs its ::text cast counts as two expressions).
        // Explicit ::uuid casts on the assignee parameter so Postgres can
        // resolve the type even when the ticket has no assignee (Dapper
        // sends NULL, which has no inferable type in a bare SELECT).
        const string sql = """
            WITH contributing_user_ids AS (
                SELECT te.author_user_id AS user_id
                FROM ticket_events te
                WHERE te.ticket_id = @ticketId
                  AND te.author_user_id IS NOT NULL
                  AND te.event_type IN ('Note','Comment','MailSent','StatusChange',
                                         'AssignmentChange','IntakeFormSent','SurveySent')
                UNION
                SELECT (@assigneeUserId)::uuid
                WHERE (@assigneeUserId)::uuid IS NOT NULL
            )
            SELECT u.id AS Id, u.email::text AS Email
            FROM contributing_user_ids c
            JOIN users u ON u.id = c.user_id
            WHERE u.role_name IN ('Agent','Admin')
            ORDER BY u.email
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AgentLookupRow>(new CommandDefinition(sql,
            new { ticketId = ticket.Id, assigneeUserId = (object?)ticket.AssigneeUserId ?? DBNull.Value },
            cancellationToken: ct));
        return rows.Select(r => new ContributingAgent(r.Id, r.Email)).ToList();
    }

    private static string ApplyTokens(string template, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        var result = template;
        foreach (var (token, value) in tokens)
        {
            if (string.IsNullOrEmpty(value)) continue;
            result = result.Replace(token, value, StringComparison.Ordinal);
        }
        return result;
    }

    private static string BuildSurveyLink(string? publicBaseUrl, string rawToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? string.Empty : publicBaseUrl!.TrimEnd('/');
        return $"{baseUrl}/surveys/{rawToken}";
    }

    private static string BuildSnapshotJson(Survey survey)
    {
        // Snapshot fields the public page reads from this JSON (vs reading
        // live off the surveys row): every question's full definition,
        // including its scope (Survey vs Agent). Header/label text is NOT
        // snapshotted on purpose — see GetByTokenHashForPublicAsync — so
        // admins can fix a typo on the thank-you message and have pending
        // links pick it up.
        var dto = new
        {
            name = survey.Name,
            description = survey.Description,
            introHtml = survey.IntroHtml,
            questions = survey.Questions.Select(q => new
            {
                id = q.Id,
                sortOrder = q.SortOrder,
                type = q.Type.ToString(),
                appliesTo = q.AppliesTo.ToString(),
                label = q.Label,
                helpText = q.HelpText,
                isRequired = q.IsRequired,
                configJson = q.Config.RootElement.GetRawText(),
            }),
        };
        return JsonSerializer.Serialize(dto);
    }

    private static string? FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);

    private static bool IsValidEmail(string s)
    {
        try { var _ = new MailAddress(s); return true; }
        catch { return false; }
    }

    private sealed record ContributingAgent(Guid Id, string Email);

    private sealed class AgentLookupRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = string.Empty;
    }
}
