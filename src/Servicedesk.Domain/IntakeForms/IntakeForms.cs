using System.Text.Json;

namespace Servicedesk.Domain.IntakeForms;

public sealed record IntakeTemplate(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedBy,
    IReadOnlyList<IntakeQuestion> Questions);

public sealed record IntakeQuestion(
    long Id,
    Guid TemplateId,
    int SortOrder,
    IntakeQuestionType Type,
    string Label,
    string? HelpText,
    bool IsRequired,
    string? DefaultValue,
    string? DefaultToken,
    IReadOnlyList<IntakeQuestionOption> Options);

public sealed record IntakeQuestionOption(
    long Id,
    long QuestionId,
    int SortOrder,
    string Value,
    string Label);

public sealed record IntakeFormInstance(
    Guid Id,
    Guid TemplateId,
    Guid TicketId,
    long? SentEventId,
    long? SubmittedEventId,
    IntakeFormStatus Status,
    DateTime? ExpiresUtc,
    DateTime CreatedUtc,
    DateTime? SentUtc,
    DateTime? SubmittedUtc,
    string? SubmitterIp,
    string? SubmitterUserAgent,
    Guid? CreatedBy,
    string? SentToEmail,
    /// JSON object { questionId: value } — a snapshot of prefill values
    /// resolved at send time. Never mutated after the mail leaves the server.
    JsonDocument Prefill);

public sealed record IntakeFormAnswer(
    long Id,
    Guid InstanceId,
    long QuestionId,
    JsonDocument Answer);

/// Supported default-value tokens a template designer can bind to a question.
/// Resolved server-side at send time against ticket / requester-contact /
/// company metadata. Unknown tokens resolve to empty string.
public static class IntakeTokens
{
    public const string RequesterName = "{{requester.name}}";
    public const string RequesterEmail = "{{requester.email}}";
    public const string TicketSubject = "{{ticket.subject}}";
    public const string TicketCategory = "{{ticket.category}}";
    public const string TicketNumber = "{{ticket.number}}";
    public const string CompanyName = "{{company.name}}";

    public static readonly IReadOnlyList<string> Supported = new[]
    {
        RequesterName,
        RequesterEmail,
        TicketSubject,
        TicketCategory,
        TicketNumber,
        CompanyName,
    };

    public static bool IsSupported(string token) => Supported.Contains(token, StringComparer.Ordinal);
}
