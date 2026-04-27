namespace Servicedesk.Infrastructure.Triggers.Templating;

/// Server-side string interpolation for trigger action templates. The
/// admin types <c>#{ticket.number}</c> in a subject or note and the
/// renderer substitutes the matching value from the per-pass
/// <see cref="TriggerRenderContext"/> snapshot — no Razor, no Liquid, no
/// reflection, no expression evaluator. Two placeholder shapes are
/// supported:
/// <list type="bullet">
/// <item>plain path lookup: <c>#{ticket.subject}</c></item>
/// <item>date/time helper: <c>#{dt(ticket.created_utc, "yyyy-MM-dd HH:mm", "Europe/Brussels")}</c></item>
/// </list>
/// Whitelist (anything else renders as empty + logs a debug line):
/// ticket.number, ticket.subject, ticket.url, ticket.queue.name,
/// ticket.priority.name, ticket.status.name, ticket.owner.email,
/// ticket.customer.firstname/lastname/email, ticket.company.name,
/// article.body_text, article.from_email, article.subject,
/// config.app.name, config.app.public_base_url.
/// dt() accepts: now, ticket.created_utc, ticket.updated_utc,
/// ticket.due_utc, ticket.first_response_utc, ticket.resolved_utc,
/// ticket.closed_utc, article.created_utc.
public interface ITriggerTemplateRenderer
{
    string Render(string template, TemplateEscapeMode mode, TriggerRenderContext renderCtx);
}
