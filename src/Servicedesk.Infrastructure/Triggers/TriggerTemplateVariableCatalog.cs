namespace Servicedesk.Infrastructure.Triggers;

/// Whitelist of <c>#{...}</c> paths the renderer recognises. Surfaced to
/// the admin UI so the <c>::</c> popover lists exactly what will resolve
/// at evaluation time — anything else falls through to empty + Debug log.
/// Keep this in sync with the dictionary keys in
/// <see cref="Templating.TriggerRenderContextFactory"/>.
public static class TriggerTemplateVariableCatalog
{
    public static readonly IReadOnlyList<TriggerTemplateVariable> All = new TriggerTemplateVariable[]
    {
        new("ticket.number",            "Ticket number",      "string", "12345"),
        new("ticket.subject",           "Ticket subject",     "string", "Printer not working"),
        new("ticket.url",               "Ticket deep-link",   "string", "https://servicedesk.example/tickets/12345"),
        new("ticket.queue.name",        "Queue name",         "string", "Support"),
        new("ticket.priority.name",     "Priority name",      "string", "High"),
        new("ticket.status.name",       "Status name",        "string", "Open"),
        new("ticket.owner.email",       "Owner email",        "string", "agent@example.com"),
        new("ticket.customer.firstname","Customer first name","string", "Alex"),
        new("ticket.customer.lastname", "Customer last name", "string", "Doe"),
        new("ticket.customer.email",    "Customer email",     "string", "alex@example.com"),
        new("ticket.company.name",      "Customer company",   "string", "Contoso"),
        new("article.body_text",        "Article body (text)","string", ""),
        new("article.from_email",       "Article from",       "string", "alex@example.com"),
        new("article.subject",          "Article subject",    "string", ""),
        new("config.app.name",          "App name",           "string", "Servicedesk"),
        new("config.app.public_base_url","Public base URL",   "string", "https://servicedesk.example"),

        new("now",                      "Now (UTC)",                       "datetime", "2026-04-27T12:00:00Z"),
        new("ticket.created_utc",       "Ticket created (UTC)",            "datetime", "2026-04-27T08:30:00Z"),
        new("ticket.updated_utc",       "Ticket updated (UTC)",            "datetime", "2026-04-27T11:00:00Z"),
        new("ticket.due_utc",           "Ticket due (UTC)",                "datetime", "2026-04-30T17:00:00Z"),
        new("ticket.first_response_utc","Ticket first response (UTC)",     "datetime", "2026-04-27T09:00:00Z"),
        new("ticket.resolved_utc",      "Ticket resolved (UTC)",           "datetime", "2026-04-27T15:00:00Z"),
        new("ticket.closed_utc",        "Ticket closed (UTC)",             "datetime", "2026-04-27T15:30:00Z"),
        new("article.created_utc",      "Article created (UTC)",           "datetime", "2026-04-27T08:30:00Z"),
    };
}

/// One entry in the template-variable catalog. <see cref="Type"/> is
/// either <c>"string"</c> (use as <c>#{path}</c>) or <c>"datetime"</c>
/// (must be wrapped in <c>#{dt(path, "format", "tz")}</c>; using the
/// path bare resolves to empty per the renderer contract).
public sealed record TriggerTemplateVariable(string Path, string Label, string Type, string Example);
