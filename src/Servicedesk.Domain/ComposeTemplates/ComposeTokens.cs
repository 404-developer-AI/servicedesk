namespace Servicedesk.Domain.ComposeTemplates;

/// Supported `{{token}}` placeholders the admin can drop into a compose
/// template. Resolved on the client just before insertContent — the agent
/// sees the live values for the current ticket's contact + company.
/// Unsupported / unresolvable tokens are left untouched so an agent can
/// type the missing value by hand instead of mailing out a blank field.
public static class ComposeTokens
{
    public const string ContactFirstName = "{{contact.firstName}}";
    public const string ContactLastName  = "{{contact.lastName}}";
    public const string ContactFullName  = "{{contact.fullName}}";
    public const string ContactEmail     = "{{contact.email}}";
    public const string ContactPhone     = "{{contact.phone}}";
    public const string ContactMobile    = "{{contact.mobile}}";

    public const string CompanyName      = "{{company.name}}";
    public const string CompanyCode      = "{{company.code}}";

    public const string TicketNumber     = "{{ticket.number}}";
    public const string TicketSubject    = "{{ticket.subject}}";

    public const string AgentEmail       = "{{agent.email}}";

    /// Picker order — also the order the picker dropdown renders in.
    /// Contact fields first because they're what agents reach for most
    /// (mail greetings, signed-off replies); company + ticket below.
    public static readonly IReadOnlyList<ComposeTokenInfo> Supported = new[]
    {
        new ComposeTokenInfo(ContactFirstName, "Contact · First name"),
        new ComposeTokenInfo(ContactLastName,  "Contact · Last name"),
        new ComposeTokenInfo(ContactFullName,  "Contact · Full name"),
        new ComposeTokenInfo(ContactEmail,     "Contact · Email"),
        new ComposeTokenInfo(ContactPhone,     "Contact · Phone"),
        new ComposeTokenInfo(ContactMobile,    "Contact · Mobile"),
        new ComposeTokenInfo(CompanyName,      "Company · Name"),
        new ComposeTokenInfo(CompanyCode,      "Company · Customer code"),
        new ComposeTokenInfo(TicketNumber,     "Ticket · Number"),
        new ComposeTokenInfo(TicketSubject,    "Ticket · Subject"),
        new ComposeTokenInfo(AgentEmail,       "Agent · Email (current user)"),
    };
}

/// Picker metadata. Token is the literal `{{…}}` placeholder; Label is what
/// the admin sees in the editor dropdown.
public sealed record ComposeTokenInfo(string Token, string Label);
