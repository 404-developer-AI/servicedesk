using System.Net.Mail;
using System.Text.Json;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

/// Read-only preview for <c>send_mail</c>: resolves recipients, renders
/// subject + body via the same template renderer, but never calls Graph
/// or writes <c>mail_messages</c>. Mirrors the validation order of
/// <see cref="SendMailHandler"/> so the surfaced failure reason matches
/// what a real run would report.
internal sealed class SendMailPreviewer : ITriggerActionPreviewer
{
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ICompanyRepository _companies;
    private readonly IUserService _users;
    private readonly IQueueAccessService _queueAccess;
    private readonly ITriggerTemplateRenderer _renderer;

    public SendMailPreviewer(
        ITaxonomyRepository taxonomy,
        ICompanyRepository companies,
        IUserService users,
        IQueueAccessService queueAccess,
        ITriggerTemplateRenderer renderer)
    {
        _taxonomy = taxonomy;
        _companies = companies;
        _users = users;
        _queueAccess = queueAccess;
        _renderer = renderer;
    }

    public string Kind => "send_mail";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadString(actionJson, "to", out var toSpec))
            return TriggerActionPreviewResult.Failed(Kind, "Action requires 'to' (one of: customer, owner-agent, queue-agents, address:foo@bar.com).");
        if (!ActionJson.TryReadString(actionJson, "subject", out var rawSubject))
            return TriggerActionPreviewResult.Failed(Kind, "Action requires 'subject'.");
        if (!ActionJson.TryReadString(actionJson, "body_html", out var bodyHtml))
            return TriggerActionPreviewResult.Failed(Kind, "Action requires 'body_html'.");

        if (ctx.RenderContext is { } rc)
        {
            rawSubject = _renderer.Render(rawSubject, TemplateEscapeMode.PlainText, rc);
            bodyHtml = _renderer.Render(bodyHtml, TemplateEscapeMode.Html, rc);
        }

        var recipients = await ResolveRecipientsAsync(toSpec, ctx, ct);
        if (recipients is null)
            return TriggerActionPreviewResult.Failed(Kind, $"Unknown recipient spec '{toSpec}'.");
        if (recipients.Count == 0)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = $"No recipients resolved for '{toSpec}'." });

        var queue = await _taxonomy.GetQueueAsync(ctx.Ticket.QueueId, ct);
        var fromMailbox = !string.IsNullOrWhiteSpace(queue?.OutboundMailboxAddress)
            ? queue!.OutboundMailboxAddress
            : queue?.InboundMailboxAddress;
        if (string.IsNullOrWhiteSpace(fromMailbox))
            return TriggerActionPreviewResult.Failed(Kind, "Queue has no inbound or outbound mailbox configured.");

        var fromName = !string.IsNullOrWhiteSpace(queue?.Name) ? queue!.Name : fromMailbox!;

        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            to = recipients,
            fromMailbox,
            fromName,
            subject = rawSubject,
            bodyHtml,
        });
    }

    private async Task<IReadOnlyList<RecipientPreview>?> ResolveRecipientsAsync(
        string toSpec, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        toSpec = toSpec.Trim();

        if (string.Equals(toSpec, "customer", StringComparison.OrdinalIgnoreCase))
        {
            var contact = await _companies.GetContactAsync(ctx.Ticket.RequesterContactId, ct);
            if (contact is null || string.IsNullOrWhiteSpace(contact.Email)) return Array.Empty<RecipientPreview>();
            var name = $"{contact.FirstName} {contact.LastName}".Trim();
            return new[] { new RecipientPreview(contact.Email, string.IsNullOrWhiteSpace(name) ? contact.Email : name) };
        }

        if (string.Equals(toSpec, "owner-agent", StringComparison.OrdinalIgnoreCase))
        {
            if (!ctx.Ticket.AssigneeUserId.HasValue) return Array.Empty<RecipientPreview>();
            var user = await _users.FindByIdAsync(ctx.Ticket.AssigneeUserId.Value, ct);
            if (user is null || string.IsNullOrWhiteSpace(user.Email)) return Array.Empty<RecipientPreview>();
            return new[] { new RecipientPreview(user.Email, user.Email) };
        }

        if (string.Equals(toSpec, "queue-agents", StringComparison.OrdinalIgnoreCase))
        {
            var userIds = await _queueAccess.GetUsersForQueueAsync(ctx.Ticket.QueueId, ct);
            if (userIds.Count == 0) return Array.Empty<RecipientPreview>();
            var list = new List<RecipientPreview>(userIds.Count);
            foreach (var uid in userIds)
            {
                var u = await _users.FindByIdAsync(uid, ct);
                if (u is null || string.IsNullOrWhiteSpace(u.Email)) continue;
                list.Add(new RecipientPreview(u.Email, u.Email));
            }
            return list;
        }

        if (toSpec.StartsWith("address:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = toSpec.Substring("address:".Length).Trim();
            if (raw.Length == 0) return Array.Empty<RecipientPreview>();
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<RecipientPreview>(parts.Length);
            foreach (var p in parts)
            {
                if (!IsValidEmail(p)) continue;
                list.Add(new RecipientPreview(p, p));
            }
            return list;
        }

        return null;
    }

    private static bool IsValidEmail(string s)
    {
        try { var _ = new MailAddress(s); return true; }
        catch { return false; }
    }

    private sealed record RecipientPreview(string Address, string Name);
}
