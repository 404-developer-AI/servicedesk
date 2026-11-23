using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Surveys;
using Servicedesk.Infrastructure.TaggingMailboxes;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Servicedesk.Infrastructure.Triggers.FirstOpenGate;
using Servicedesk.Infrastructure.Workflow;

namespace Servicedesk.Api.Tickets;

public static partial class TicketEndpoints
{
    // v0.0.40 polish — parse comma-separated guid query strings sent by
    // the view editor's multi-select. Invalid tokens are silently dropped.
    // Returns null for empty input so the repository treats it as "no
    // multi-filter" and falls back to the singular field path.
    private static IReadOnlyList<Guid>? ParseGuidList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var result = new List<Guid>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(token, out var g)) result.Add(g);
        }
        return result.Count == 0 ? null : result;
    }

    /// v0.1.18 — a normalized search term that is a clean positive integer is
    /// also probed against the ticket number (typed bigint, indexed column).
    internal static long? ParseTicketNumberProbe(string? normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var span = normalized.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];
        if (span.Length is 0 or > 18) return null;
        foreach (var c in span)
            if (c is < '0' or > '9') return null;
        return long.TryParse(span, global::System.Globalization.NumberStyles.None,
            global::System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// v0.0.57 — normalize a user-typed ticket search so a pasted reference
    /// ("Ticket#1234", "#1234", "[Ticket#1234]") resolves to the bare number
    /// the list/picker SQL matches on. Free text and bare numbers pass through
    /// unchanged. A settings-store hiccup falls back to the factory prefix so
    /// search never 500s.
    private static async Task<string?> NormalizeTicketSearchAsync(
        string? search, ISettingsService settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(search)) return search;
        var raw = await settings.GetOrAsync<string?>(SettingKeys.Tickets.ReferencePrefix, null, ct);
        var prefix = string.IsNullOrWhiteSpace(raw) ? TicketReference.DefaultPrefix : raw;
        return TicketReference.NormalizeSearchTerm(search, prefix);
    }

    /// Shared projection for both the probe endpoint and the PATCH 409
    /// payload. Keeps the shape identical so the FE renders an
    /// out-of-band match (409 fallback) the same as a probe-then-PATCH
    /// match. The TriggerId, kind, dialog labels and (when applicable)
    /// the requester contact info are the only fields the dialog
    /// needs — note_template stays server-side.
    private static object ProjectGateForApi(MatchedStatusGate g) => new
    {
        triggerId = g.TriggerId,
        name = g.Name,
        kind = g.Kind,
        title = g.Title,
        message = g.Message,
        confirmLabel = g.ConfirmLabel,
        cancelLabel = g.CancelLabel,
        questions = g.Questions.Select(q => new
        {
            key = q.Key,
            type = q.Type,
            label = q.Label,
            required = q.Required,
            yesLabel = q.YesLabel,
            noLabel = q.NoLabel,
            // v0.0.89 — only populated for choice questions; the outcome is
            // surfaced so the dialog can hint what each option will do.
            options = (q.Options ?? Array.Empty<GateChoiceOption>())
                .Select(o => new { label = o.Label, outcome = o.Outcome }),
        }),
        requesterContactId = g.RequesterContactId,
        requesterDisplayName = g.RequesterDisplayName,
    };

    private static object ProjectFirstOpenGateForApi(MatchedFirstOpenGate g, Guid ticketId) => new
    {
        triggerId = g.TriggerId,
        name = g.Name,
        title = g.Title,
        message = g.Message,
        fieldLabel = g.FieldLabel,
        confirmLabel = g.ConfirmLabel,
        currentSubject = g.CurrentSubject,
        showRequest = g.ShowRequest,
        requestBodyHtml = g.RequestBodyHtml,
        requestBodyText = g.RequestBodyText,
        requestSource = g.RequestFromMail ? "mail" : "ticket",
        // Reuses the existing audited raw-mail endpoint (Agent + queue-access
        // gated); null when the mail's .eml blob was never stored.
        requestEmlUrl = g.RequestMailId is Guid mailId
            ? $"/api/tickets/{ticketId}/mail/{mailId}/raw"
            : null,
    };

    /// v0.0.102 — one actor object for the shared mutation service: the
    /// authorization identity (user id + role claim) plus the audit identity
    /// the endpoints already derived via <see cref="ActorContext.Resolve"/>.
    internal static TicketMutationActor ResolveMutationActor(HttpContext http)
    {
        var userId = ActorContext.GetUserId(http);
        var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;
        var (actor, role) = ActorContext.Resolve(http);
        return new TicketMutationActor(
            userId, userRole, actor, role,
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.UserAgent.ToString())
        {
            Amr = http.User.FindFirst(Servicedesk.Api.Auth.SessionAuthenticationHandler.AmrClaimType)?.Value,
        };
    }

    private const int MaxEditAttachmentIds = 20;

    private static IReadOnlyList<GraphRecipient> MapRecipients(IReadOnlyList<MailRecipientInput>? input)
    {
        if (input is null || input.Count == 0) return Array.Empty<GraphRecipient>();
        var list = new List<GraphRecipient>(input.Count);
        foreach (var r in input)
        {
            if (string.IsNullOrWhiteSpace(r.Address)) continue;
            list.Add(new GraphRecipient(r.Address.Trim(), (r.Name ?? r.Address).Trim()));
        }
        return list;
    }

    /// Hide survey-result events from the timeline for non-admin agents.
    /// SurveySent stays visible as operational context ("we asked the
    /// customer for feedback"), but SurveySubmitted and SurveyExpired carry
    /// the response-cycle outcome — that's admin-only per the v0.0.38
    /// post-test access tightening. EventType is stored on the record as a
    /// string (matches the DB CHECK constraint), so we compare names rather
    /// than enum values.
    /// v0.1.33 — event types a role never sees on the timeline (survey events
    /// are Admin-only), applied in SQL by the paged reader so page sizes and
    /// counts match.
    private static readonly string[] AdminOnlyTimelineEventTypes =
    {
        nameof(Servicedesk.Domain.Tickets.TicketEventType.SurveySubmitted),
        nameof(Servicedesk.Domain.Tickets.TicketEventType.SurveyExpired),
    };

    /// The event types a personal or group tag can point to (the
    /// MentionNotificationService sources) — the only ones the tag hover
    /// preview serves.
    private static readonly HashSet<string> TagPreviewEventTypes = new(StringComparer.Ordinal)
    {
        nameof(Servicedesk.Domain.Tickets.TicketEventType.Note),
        nameof(Servicedesk.Domain.Tickets.TicketEventType.Comment),
        nameof(Servicedesk.Domain.Tickets.TicketEventType.Call),
        nameof(Servicedesk.Domain.Tickets.TicketEventType.MailSent),
    };

    private static IReadOnlyCollection<string> HiddenTimelineEventTypes(string role) =>
        string.Equals(role, "Admin", StringComparison.Ordinal)
            ? Array.Empty<string>()
            : AdminOnlyTimelineEventTypes;

    /// Tickets.TimelinePageSize: 0 = whole timeline, else clamped to 10–1000.
    private static async Task<int> ResolveTimelinePageSizeAsync(ISettingsService settings, CancellationToken ct)
    {
        int raw;
        raw = await settings.GetOrAsync<int>(SettingKeys.Tickets.TimelinePageSize, 50, ct);
        return raw <= 0 ? 0 : Math.Clamp(raw, 10, 1000);
    }

    // Hard ceiling for one "load older" request, also when it loads back to a
    // linked event or loads everything — keeps a single response bounded.
    private const int MaxTimelineSliceEvents = 2000;

    /// v0.0.51 — shared validation + side-effect for the two endpoints
    /// that let the agent attach a ticket to a specific company
    /// (POST /api/tickets and PATCH /api/tickets/{id}/company).
    ///
    /// Rules:
    ///   * Company must exist and be active.
    ///   * If the company is already on the contact's link list →
    ///     newLinkRole must be null/empty (anything else = 400).
    ///   * If the company is NOT yet linked → newLinkRole must be
    ///     'primary' | 'secondary' | 'supplier' (anything else = 400).
    ///   * newLinkRole='primary' while the contact already has a
    ///     primary link to another company → 409 (no silent demotion;
    ///     the agent must change the existing primary on the Contact
    ///     page first).
    ///
    /// Returns a non-null <c>ErrorResult</c> when validation rejects
    /// the request — caller must return it before persisting the
    /// ticket / company-assignment. <c>NewLinkCreated</c> is true when
    /// a fresh contact_companies row was upserted.
    private static async Task<TicketCompanyChoiceResult> ApplyTicketCompanyChoiceAsync(
        ICompanyRepository companies,
        Guid contactId,
        Guid pickedCompanyId,
        string? newLinkRole,
        CancellationToken ct)
    {
        if (pickedCompanyId == Guid.Empty)
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new { error = "companyId is empty." }));

        var picked = await companies.GetCompanyAsync(pickedCompanyId, ct);
        if (picked is null)
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new { error = "Unknown company." }));
        if (!picked.IsActive)
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new { error = "Cannot assign an inactive company." }));

        var trimmedRole = string.IsNullOrWhiteSpace(newLinkRole) ? null : newLinkRole.Trim().ToLowerInvariant();
        var links = await companies.ListContactLinksAsync(contactId, ct);
        var alreadyLinked = links.Any(l => l.CompanyId == pickedCompanyId);

        if (alreadyLinked)
        {
            if (trimmedRole is not null)
            {
                return TicketCompanyChoiceResult.Error(Results.BadRequest(new
                {
                    error = "newLinkRole must be omitted when the company is already linked to the contact.",
                    code = "company_already_linked",
                }));
            }
            return TicketCompanyChoiceResult.Ok(NewLinkCreated: false);
        }

        // Company is not yet linked → a role choice is mandatory.
        if (trimmedRole is null)
        {
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new
            {
                error = "newLinkRole is required when picking a company that is not yet linked to the contact.",
                code = "new_link_role_required",
            }));
        }
        if (trimmedRole is not ("primary" or "secondary" or "supplier"))
        {
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new
            {
                error = "newLinkRole must be one of 'primary', 'secondary', 'supplier'.",
                code = "new_link_role_invalid",
            }));
        }

        // Primary slot is unique per contact. Refuse rather than
        // silently demote so the agent always knows what changes.
        if (trimmedRole == "primary")
        {
            var existingPrimary = links.FirstOrDefault(l => l.Role == "primary");
            if (existingPrimary is not null)
            {
                return TicketCompanyChoiceResult.Error(Results.Conflict(new
                {
                    error = "This contact already has a primary company. Pick a different role, or change the primary on the contact page first.",
                    code = "primary_already_set",
                    existingPrimaryCompanyId = existingPrimary.CompanyId,
                }));
            }
        }

        await companies.UpsertContactLinkAsync(contactId, pickedCompanyId, trimmedRole, ct);
        return TicketCompanyChoiceResult.Ok(NewLinkCreated: true);
    }

    private readonly record struct TicketCompanyChoiceResult(IResult? ErrorResult, bool NewLinkCreated)
    {
        public static TicketCompanyChoiceResult Ok(bool NewLinkCreated) => new(null, NewLinkCreated);
        public static TicketCompanyChoiceResult Error(IResult result) => new(result, false);
    }

    private static async Task<TicketCompanyAlert?> BuildCompanyAlertAsync(
        ICompanyRepository companies, Guid? ticketCompanyId, Guid requesterContactId, CancellationToken ct)
    {
        // Resolve the alert from the ticket's OWN frozen company first — that is
        // the company this ticket belongs to, set at intake (t.company_id). The
        // requester's *current* primary-company link is only a fallback for
        // tickets that never got a company assigned. Resolving via the requester
        // link alone meant older tickets — whose requester contact has no primary
        // contact_companies row even though the ticket has a company — never
        // surfaced the on-open pop-up. (v0.0.66 fix)
        var company = ticketCompanyId is { } cid
            ? await companies.GetCompanyAsync(cid, ct)
            : null;
        company ??= await companies.GetPrimaryCompanyForContactAsync(requesterContactId, ct);
        if (company is null) return null;
        return new TicketCompanyAlert(
            CompanyId: company.Id,
            CompanyName: company.Name,
            Code: company.Code,
            AlertText: company.AlertText,
            AlertOnCreate: company.AlertOnCreate,
            AlertOnOpen: company.AlertOnOpen,
            AlertOnOpenMode: company.AlertOnOpenMode);
    }

    /// v0.0.23 split: when this ticket was created by splitting a mail off
    /// another ticket, surface the source-mail's non-inline attachments so the
    /// description block can render download chips. URLs route through the
    /// source ticket's per-mail attachment endpoint — the bytes never move.
    /// Returns an empty list for tickets that aren't splits.
    private static async Task<IReadOnlyList<object>> BuildSplitDescriptionAttachmentsAsync(
        Servicedesk.Domain.Tickets.Ticket ticket, string? createdEventMetadataJson,
        Npgsql.NpgsqlDataSource dataSource, CancellationToken ct)
    {
        if (ticket.SplitFromTicketId is null) return Array.Empty<object>();

        // Created event carries splitFromMailMessageId in its metadata (set by
        // SplitAsync). v0.1.33 — read straight from that event (the timeline
        // is paged, so it is usually not in the loaded events).
        if (string.IsNullOrWhiteSpace(createdEventMetadataJson))
            return Array.Empty<object>();

        Guid? sourceMailMessageId = null;
        try
        {
            using var doc = JsonDocument.Parse(createdEventMetadataJson);
            if (doc.RootElement.TryGetProperty("splitFromMailMessageId", out var prop)
                && prop.ValueKind == JsonValueKind.String
                && Guid.TryParse(prop.GetString(), out var g))
            {
                sourceMailMessageId = g;
            }
        }
        catch { /* malformed metadata — treat as no attachments */ }

        if (sourceMailMessageId is null) return Array.Empty<object>();

        const string sql = """
            SELECT a.id, a.original_filename, a.mime_type, a.size_bytes
            FROM attachments a
            WHERE a.owner_kind = 'Mail'
              AND a.owner_id = @mailId
              AND a.processing_state = 'Ready'
              AND a.is_inline = FALSE
            ORDER BY a.created_utc, a.id
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await Dapper.SqlMapper.QueryAsync<(Guid Id, string OriginalFilename, string MimeType, long SizeBytes)>(
            conn, new Dapper.CommandDefinition(sql, new { mailId = sourceMailMessageId.Value }, cancellationToken: ct));

        var sourceTicketId = ticket.SplitFromTicketId.Value;
        var mailId = sourceMailMessageId.Value;
        return rows.Select(r => (object)new
        {
            id = r.Id,
            name = r.OriginalFilename,
            mimeType = r.MimeType,
            size = r.SizeBytes,
            url = $"/api/tickets/{sourceTicketId}/mail/{mailId}/attachments/{r.Id}",
        }).ToList();
    }
}
