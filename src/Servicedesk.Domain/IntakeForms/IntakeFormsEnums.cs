namespace Servicedesk.Domain.IntakeForms;

/// Question types supported in v0.0.19 intake templates. The serialised value
/// is the enum member name (kept in sync with the `chk_intake_question_type`
/// DB CHECK constraint in <c>DatabaseBootstrapper</c>).
///
/// <see cref="SectionHeader"/> is a layout-only row — it carries a label and
/// help text but collects no input, and any answer submitted against it is
/// rejected server-side.
public enum IntakeQuestionType
{
    ShortText,
    LongText,
    DropdownSingle,
    DropdownMulti,
    Number,
    Date,
    YesNo,
    SectionHeader,
}

/// Lifecycle of a form instance. Only <c>Sent</c> rows are reachable via the
/// public token endpoint; all others 404. Kept in sync with the
/// `chk_intake_form_status` DB CHECK.
public enum IntakeFormStatus
{
    Draft,
    Sent,
    Submitted,
    Expired,
    Cancelled,
}
