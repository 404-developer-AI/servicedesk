namespace Servicedesk.Domain.Signatures;

/// Supported `{{agent.*}}` placeholders an admin can drop into a signature
/// text block. Resolved server-side at send-time from the sending agent's
/// Microsoft Entra ID profile (with a per-user local override). Mirrors the
/// dotted `{{contact.*}}` / `{{agent.email}}` convention used by
/// <see cref="ComposeTemplates.ComposeTokens"/> so the two pickers feel the
/// same. An unresolved token collapses (its containing line is dropped)
/// rather than mailing out an empty label.
///
/// The agent photo is NOT a text token — it is selected on an image block via
/// its variable source (<c>SignatureBlock.Variable = "agent.photo"</c>), so it
/// can carry width/height/shape like any other image. <see cref="AgentPhoto"/>
/// is the canonical variable name for that source.
public static class SignatureTokens
{
    public const string AgentFullName  = "{{agent.fullName}}";
    public const string AgentFirstName = "{{agent.firstName}}";
    public const string AgentLastName  = "{{agent.lastName}}";
    public const string AgentJobTitle  = "{{agent.jobTitle}}";
    public const string AgentEmail     = "{{agent.email}}";
    public const string AgentPhone     = "{{agent.phone}}";
    public const string AgentMobile    = "{{agent.mobile}}";

    /// Image-block variable source for the sender's profile photo.
    public const string AgentPhoto = "agent.photo";

    /// Picker order — also the order the token dropdown renders in.
    public static readonly IReadOnlyList<SignatureTokenInfo> Supported = new[]
    {
        new SignatureTokenInfo(AgentFullName,  "Agent · Full name"),
        new SignatureTokenInfo(AgentFirstName, "Agent · First name"),
        new SignatureTokenInfo(AgentLastName,  "Agent · Last name"),
        new SignatureTokenInfo(AgentJobTitle,  "Agent · Job title"),
        new SignatureTokenInfo(AgentEmail,     "Agent · Email"),
        new SignatureTokenInfo(AgentPhone,     "Agent · Phone"),
        new SignatureTokenInfo(AgentMobile,    "Agent · Mobile"),
    };
}

/// Picker metadata. Token is the literal `{{…}}` placeholder; Label is what
/// the admin sees in the builder dropdown.
public sealed record SignatureTokenInfo(string Token, string Label);
