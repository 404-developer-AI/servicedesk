using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Servicedesk.Infrastructure.Forms;

/// v0.1.52 — the field types an admin can put on a form template. Strings
/// (not an enum) because they travel as-is in the JSONB definition and the
/// API; <see cref="All"/> is the whitelist every boundary checks against.
public static class FormFieldType
{
    public const string SectionHeader = "SectionHeader";
    public const string ShortText = "ShortText";
    public const string LongText = "LongText";
    public const string Number = "Number";
    public const string Dropdown = "Dropdown";
    public const string MultiSelect = "MultiSelect";
    public const string Checkbox = "Checkbox";
    public const string CheckboxGroup = "CheckboxGroup";
    public const string Date = "Date";
    public const string DateTime = "DateTime";
    public const string Duration = "Duration";
    public const string Agent = "Agent";
    public const string Contact = "Contact";
    public const string Article = "Article";
    /// v0.1.58 — a queue the filler has access to (stored as {"id","name"});
    /// a trigger can move the ticket there (set_queue from a form field).
    public const string Queue = "Queue";
    /// v0.1.61 — a postal address (plain multi-line text, default may hold
    /// <c>{{company.address}}</c>); the Outlook appointment's location.
    public const string Address = "Address";
    /// v0.1.61 — files attached to the form (ticket attachments of this
    /// ticket, stored as {"id","name","size","contentType"} rows).
    public const string Attachments = "Attachments";
    /// v0.1.61 — people to invite: servicedesk contacts and/or free e-mail
    /// addresses (stored as {"contactId"?, "email", "name"} rows).
    public const string Invitees = "Invitees";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        SectionHeader, ShortText, LongText, Number, Dropdown, MultiSelect, Checkbox,
        CheckboxGroup, Date, DateTime, Duration, Agent, Contact, Article, Queue,
        Address, Attachments, Invitees,
    };

    public static bool HasOptions(string type) => type is Dropdown or MultiSelect or CheckboxGroup;

    /// SectionHeader is layout only — it never carries an answer.
    public static bool IsInput(string type) => type != SectionHeader;
}

/// The content of a form template: an ordered list of fields. Stored as one
/// JSON document on <c>form_templates.definition</c> and copied verbatim to
/// <c>ticket_forms.template_snapshot</c> when a form is filled in.
public sealed class FormDefinition
{
    [JsonPropertyName("fields")]
    public List<FormField> Fields { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static FormDefinition Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FormDefinition();
        try
        {
            return JsonSerializer.Deserialize<FormDefinition>(json, JsonOptions) ?? new FormDefinition();
        }
        catch (JsonException)
        {
            return new FormDefinition();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public int InputFieldCount => Fields.Count(f => FormFieldType.IsInput(f.Type));
}

public sealed class FormField
{
    /// Stable id; answers are keyed on it. Generated server-side when the
    /// editor sends a new field without one.
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = FormFieldType.ShortText;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("help")]
    public string Help { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// "full" | "half" — layout hint for the fill dialog (two half fields
    /// sit side by side).
    [JsonPropertyName("width")]
    public string Width { get; set; } = "full";

    [JsonPropertyName("options")]
    public List<FormFieldOption>? Options { get; set; }

    /// Number: bounds. Article: per-row quantity bounds.
    [JsonPropertyName("min")]
    public decimal? Min { get; set; }

    [JsonPropertyName("max")]
    public decimal? Max { get; set; }

    /// Number only: allowed decimal places (0 = integers).
    [JsonPropertyName("decimals")]
    public int? Decimals { get; set; }

    /// Article only: row-count bounds.
    [JsonPropertyName("minRows")]
    public int? MinRows { get; set; }

    [JsonPropertyName("maxRows")]
    public int? MaxRows { get; set; }

    /// Text/Number/Dropdown/Checkbox: pre-filled value in the fill dialog
    /// (Dropdown = option id, Checkbox = "true"). Never trusted as an answer.
    [JsonPropertyName("defaultValue")]
    public string DefaultValue { get; set; } = string.Empty;

    /// v0.1.58 — optional name triggers use to address this field
    /// (<c>form.field.&lt;key&gt;</c> conditions, actions that take their value
    /// from a form field). Unique within a template, lowercase slug; null =
    /// triggers cannot see the field. Frozen with the snapshot like the rest
    /// of the definition, so a form filled before a key was added has none.
    [JsonPropertyName("triggerKey")]
    public string? TriggerKey { get; set; }

    /// v0.1.61 — Agent only: several agents may be picked (stored as an
    /// array, optionally with an organizer). Null/false = one agent.
    [JsonPropertyName("multiple")]
    public bool? Multiple { get; set; }

    /// v0.1.61 — conditional display: the field is shown (and validated)
    /// only while this Checkbox field of the same form is ticked. A hidden
    /// field is never required and its answer is dropped on save.
    [JsonPropertyName("showWhen")]
    public Guid? ShowWhen { get; set; }

    /// v0.1.61 — inverts <see cref="ShowWhen"/>: shown only while that
    /// checkbox is NOT ticked (never answered counts as not ticked).
    /// Null/false = shown while ticked.
    [JsonPropertyName("showWhenUnchecked")]
    public bool? ShowWhenUnchecked { get; set; }

    /// v0.1.61 — Duration only: the DateTime field (above this one) the
    /// duration counts from, so the end is start + duration ("until 14:30";
    /// an Outlook appointment can take its end from it). Null = a plain duration.
    [JsonPropertyName("durationFrom")]
    public Guid? DurationFrom { get; set; }
}

public sealed class FormFieldOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

/// Field limits shared by the template validator and the answer validator.
/// Admin-tunable caps live in <see cref="FormRuntimeSettings"/>; these are the
/// absolute ceilings those settings are clamped to.
public static class FormLimits
{
    public const int NameMax = 200;
    /// v0.1.61 — label of the optional button next to the ticket title.
    public const int HeaderButtonLabelMax = 40;
    public const int DescriptionMax = 2000;
    public const int LabelMax = 300;
    public const int HelpMax = 1000;
    public const int OptionLabelMax = 200;
    public const int OptionIdMax = 40;
    public const int OptionsMax = 100;
    public const int ShortTextMax = 500;
    /// Order references a short text answer may carry (`::` picks).
    public const int ShortTextOrdersMax = 10;
    public const int DeleteReasonMax = 1000;
    public const int BackofficeNoteMax = 4000;
    public const int DurationMaxMinutes = 100_000;
    public const decimal NumberAbsMax = 1_000_000_000_000m;
    public const int DecimalsMax = 6;

    public const int HardMaxFields = 200;
    public const int HardMaxArticleRows = 500;
    public const int HardMaxLongText = 20_000;
    public const int HardMaxPerTicket = 200;
    public const int TriggerKeyMax = 40;
    public const decimal HardMaxArticleQuantity = 10_000_000m;
    /// v0.1.61
    public const int AddressMax = 1000;
    public const int AgentsMax = 20;
    public const int InviteesMax = 100;
    public const int InviteeNameMax = 200;
    public const int EmailMax = 320;
    public const int AttachmentsMax = 20;
    /// Graph's upload-session ceiling for one appointment attachment.
    public const int HardMaxAttachmentTotalMb = 150;
}

/// Validation + normalization of a template definition. Returns an English
/// error message (surfaced verbatim by the API) or null.
public static class FormTemplateValidator
{
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (name.Trim().Length > FormLimits.NameMax) return $"Name must be at most {FormLimits.NameMax} characters.";
        return null;
    }

    public static string? ValidateDescription(string? description)
    {
        if (description is not null && description.Length > FormLimits.DescriptionMax)
            return $"Description must be at most {FormLimits.DescriptionMax} characters.";
        return null;
    }

    /// Validates and normalizes the definition in place (trims, assigns ids
    /// to new fields/options, drops properties that do not apply to a type).
    public static string? ValidateAndNormalize(FormDefinition def, int maxFields)
    {
        def.Fields ??= new List<FormField>();
        if (def.Fields.Count > maxFields)
            return $"A form can have at most {maxFields} fields (Settings → Tickets → Forms).";

        var ids = new HashSet<Guid>();
        var triggerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in def.Fields)
        {
            if (f is null) return "Invalid field.";
            f.Type = (f.Type ?? string.Empty).Trim();
            if (!FormFieldType.All.Contains(f.Type)) return $"Unknown field type '{Truncate(f.Type, 40)}'.";
            if (f.Id == Guid.Empty) f.Id = Guid.NewGuid();
            if (!ids.Add(f.Id)) return "Field ids must be unique.";

            f.Label = (f.Label ?? string.Empty).Trim();
            f.Help = (f.Help ?? string.Empty).Trim();
            f.DefaultValue = (f.DefaultValue ?? string.Empty).Trim();
            f.Width = f.Width == "half" ? "half" : "full";
            if (f.Label.Length == 0) return "Every field needs a label.";
            if (f.Label.Length > FormLimits.LabelMax) return $"Labels must be at most {FormLimits.LabelMax} characters.";
            if (f.Help.Length > FormLimits.HelpMax) return $"Help texts must be at most {FormLimits.HelpMax} characters.";

            if (f.Type == FormFieldType.SectionHeader)
            {
                f.Required = false;
                f.Width = "full";
            }

            f.TriggerKey = string.IsNullOrWhiteSpace(f.TriggerKey) || f.Type == FormFieldType.SectionHeader
                ? null
                : f.TriggerKey.Trim().ToLowerInvariant();
            if (f.TriggerKey is not null)
            {
                if (!TriggerKeyPattern.IsMatch(f.TriggerKey))
                    return $"'{Truncate(f.Label, 40)}': the trigger key may only contain a-z, 0-9 and _, must start with a letter and be at most {FormLimits.TriggerKeyMax} characters.";
                if (!triggerKeys.Add(f.TriggerKey))
                    return $"The trigger key '{f.TriggerKey}' is used twice.";
            }

            if (FormFieldType.HasOptions(f.Type))
            {
                f.Options ??= new List<FormFieldOption>();
                f.Options.RemoveAll(o => o is null || string.IsNullOrWhiteSpace(o.Label));
                if (f.Options.Count == 0) return $"'{Truncate(f.Label, 40)}' needs at least one option.";
                if (f.Options.Count > FormLimits.OptionsMax) return $"A field can have at most {FormLimits.OptionsMax} options.";
                var optIds = new HashSet<string>(StringComparer.Ordinal);
                var optLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in f.Options)
                {
                    o.Label = o.Label.Trim();
                    o.Id = (o.Id ?? string.Empty).Trim();
                    if (o.Id.Length == 0) o.Id = Guid.NewGuid().ToString("N")[..12];
                    if (o.Id.Length > FormLimits.OptionIdMax) return "Option ids are too long.";
                    if (o.Label.Length > FormLimits.OptionLabelMax) return $"Options must be at most {FormLimits.OptionLabelMax} characters.";
                    if (!optIds.Add(o.Id)) return "Option ids must be unique within a field.";
                    if (!optLabels.Add(o.Label)) return $"'{Truncate(f.Label, 40)}' has the option '{Truncate(o.Label, 40)}' twice.";
                }
            }
            else
            {
                f.Options = null;
            }

            if (f.Type is FormFieldType.Number or FormFieldType.Article)
            {
                if (f.Min is { } min && Math.Abs(min) > FormLimits.NumberAbsMax) return "Minimum is out of range.";
                if (f.Max is { } max && Math.Abs(max) > FormLimits.NumberAbsMax) return "Maximum is out of range.";
                if (f.Min is { } a && f.Max is { } b && a > b) return $"'{Truncate(f.Label, 40)}': minimum is larger than maximum.";
            }
            else
            {
                f.Min = null;
                f.Max = null;
            }

            if (f.Type == FormFieldType.Number)
            {
                f.Decimals = Math.Clamp(f.Decimals ?? 0, 0, FormLimits.DecimalsMax);
            }
            else
            {
                f.Decimals = null;
            }

            if (f.Type == FormFieldType.Article)
            {
                if (f.MinRows is < 0 || f.MaxRows is < 1) return "Row limits must be positive.";
                if (f.MinRows is { } r1 && f.MaxRows is { } r2 && r1 > r2) return $"'{Truncate(f.Label, 40)}': minimum rows is larger than maximum rows.";
                if (f.Min is < 0) return "Article quantities cannot have a negative minimum.";
            }
            else
            {
                f.MinRows = null;
                f.MaxRows = null;
            }

            f.Multiple = f.Type == FormFieldType.Agent && f.Multiple == true ? true : null;

            // Defaults only for the types the fill dialog can pre-fill.
            if (f.Type is not (FormFieldType.ShortText or FormFieldType.LongText or FormFieldType.Number
                or FormFieldType.Dropdown or FormFieldType.Checkbox or FormFieldType.Address))
            {
                f.DefaultValue = string.Empty;
            }
            if (f.DefaultValue.Length > FormLimits.ShortTextMax) return "Default values must be at most 500 characters.";
            // v0.1.55 — a text default may hold compose variables
            // ({{company.name}}), filled in for the ticket when the form
            // opens. Only the supported set; a typo would otherwise surface
            // as an empty field on every ticket.
            if (f.Type is FormFieldType.ShortText or FormFieldType.LongText or FormFieldType.Address
                && UnknownToken(f.DefaultValue) is { } unknown)
            {
                return $"'{Truncate(f.Label, 40)}': unknown variable {unknown}.";
            }
            if (f.Type == FormFieldType.Dropdown && f.DefaultValue.Length > 0
                && f.Options!.All(o => o.Id != f.DefaultValue))
            {
                f.DefaultValue = string.Empty;
            }
        }

        // v0.1.61 — "show when": the controlling field must be a Checkbox of
        // this form that comes before the field (no cycles, no forward refs).
        var seen = new Dictionary<Guid, FormField>();
        foreach (var f in def.Fields)
        {
            if (f.ShowWhen is { } ctl)
            {
                if (f.Type == FormFieldType.SectionHeader) { f.ShowWhen = null; f.ShowWhenUnchecked = null; }
                else if (!seen.TryGetValue(ctl, out var c) || c.Type != FormFieldType.Checkbox)
                    return $"'{Truncate(f.Label, 40)}': 'Show when' must point to a checkbox above this field.";
            }
            else f.ShowWhenUnchecked = null;
            if (f.ShowWhenUnchecked == false) f.ShowWhenUnchecked = null;
            // v0.1.61 — "counts from": a DateTime field above this Duration.
            if (f.DurationFrom is { } from)
            {
                if (f.Type != FormFieldType.Duration) f.DurationFrom = null;
                else if (!seen.TryGetValue(from, out var s) || s.Type != FormFieldType.DateTime)
                    return $"'{Truncate(f.Label, 40)}': 'Counts from' must point to a date and time field above this field.";
            }
            seen[f.Id] = f;
        }

        if (def.InputFieldCount == 0) return "Add at least one input field.";
        return null;
    }

    /// v0.1.61 — whether a field is shown for these answers: no "show when",
    /// or its controlling checkbox is ticked (or, with ShowWhenUnchecked,
    /// not ticked — a missing answer counts as not ticked).
    public static bool IsShown(FormField f, JsonObject answers)
    {
        if (f.ShowWhen is not { } ctl) return true;
        var ticked = answers[ctl.ToString()] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return f.ShowWhenUnchecked == true ? !ticked : ticked;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// Same shape the trigger condition key <c>form.field.&lt;key&gt;</c> accepts.
    public static readonly System.Text.RegularExpressions.Regex TriggerKeyPattern =
        new(@"^[a-z][a-z0-9_]{0,39}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex TokenPattern =
        new(@"\{\{\s*([\w.]+)\s*\}\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string? UnknownToken(string text)
    {
        foreach (System.Text.RegularExpressions.Match m in TokenPattern.Matches(text))
        {
            var canonical = "{{" + m.Groups[1].Value + "}}";
            if (Domain.ComposeTemplates.ComposeTokens.Supported.All(t => t.Token != canonical))
                return Truncate(canonical, 40);
        }
        return null;
    }
}

/// An Adsolut order picked through `::` in a ShortText answer: the id behind
/// the pill and the label the agent sees in the text. Like a composer pill it
/// is a pointer only — opening it goes through the gated order endpoint.
public sealed record FormOrderRef(Guid Id, string Label);

/// A reference an answer points to that the service must resolve against the
/// database (and reject when it does not exist / is out of scope).
public sealed record FormReferenceSet(
    IReadOnlySet<Guid> AgentIds,
    IReadOnlySet<Guid> ContactIds,
    IReadOnlySet<Guid> ArticleIds)
{
    /// Queue fields; resolved against the filler's own queue access.
    public IReadOnlySet<Guid> QueueIds { get; init; } = new HashSet<Guid>();
    /// v0.1.61 — Invitees fields: servicedesk contacts (any active contact).
    public IReadOnlySet<Guid> InviteeContactIds { get; init; } = new HashSet<Guid>();
    /// v0.1.61 — Attachments fields: attachments of this ticket.
    public IReadOnlySet<Guid> AttachmentIds { get; init; } = new HashSet<Guid>();
}

/// Resolved display data for references (written into the stored answer so
/// history keeps the name an agent saw, even after a rename).
public sealed record FormResolvedReferences(
    IReadOnlyDictionary<Guid, string> Agents,
    IReadOnlyDictionary<Guid, (string Name, string Email)> Contacts,
    IReadOnlyDictionary<Guid, (string Code, string Name)> Articles)
{
    public IReadOnlyDictionary<Guid, string> Queues { get; init; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, (string Name, string Email)> InviteeContacts { get; init; } = new Dictionary<Guid, (string, string)>();
    public IReadOnlyDictionary<Guid, (string Name, long Size, string ContentType)> Attachments { get; init; }
        = new Dictionary<Guid, (string, long, string)>();
}

public sealed class FormAnswerException : Exception
{
    public Guid? FieldId { get; }
    public FormAnswerException(string message, Guid? fieldId = null) : base(message) => FieldId = fieldId;
}

public sealed record FormRuntimeSettings(
    bool Enabled,
    int MaxFieldsPerTemplate,
    int MaxArticleRows,
    decimal MaxArticleQuantity,
    int LongTextMaxLength,
    int MaxPerTicket)
{
    /// v0.1.61 — combined size of the files on one form (0 = no cap).
    public long MaxAttachmentBytes { get; init; } = 25L * 1024 * 1024;
}

/// Validates answers submitted for a form against its (frozen) definition.
/// Two passes so the pure part is unit-testable without a database:
/// <see cref="Parse"/> checks shape, types, required, option ids and bounds
/// and collects the referenced ids; <see cref="Build"/> writes the normalized
/// answer document once the service has resolved those ids. Unknown field
/// ids and answers for SectionHeaders are rejected, never ignored.
public static class FormAnswerValidator
{
    public sealed record ParsedAnswers(
        IReadOnlyDictionary<Guid, ParsedValue> Values,
        FormReferenceSet References);

    public abstract record ParsedValue;
    public sealed record TextValue(string Value) : ParsedValue;
    /// A rich-text LongText answer: sanitized HTML (stored as {"html": …}).
    public sealed record HtmlValue(string Html) : ParsedValue;
    /// A ShortText answer with Adsolut orders picked through `::` (stored as
    /// {"text": …, "orders": [{"id", "label"}]}); the note renders each
    /// order's label in the text as a clickable order pill.
    public sealed record TextWithOrdersValue(string Text, IReadOnlyList<FormOrderRef> Orders) : ParsedValue;
    public sealed record NumberValue(decimal Value) : ParsedValue;
    public sealed record BoolValue(bool Value) : ParsedValue;
    public sealed record OptionValue(string OptionId) : ParsedValue;
    public sealed record OptionsValue(IReadOnlyList<string> OptionIds) : ParsedValue;
    public sealed record DateValue(DateOnly Value) : ParsedValue;
    public sealed record DateTimeValue(DateTimeOffset Value) : ParsedValue;
    public sealed record DurationValue(int Minutes) : ParsedValue;
    public sealed record RefValue(Guid Id) : ParsedValue;
    public sealed record ArticleRowsValue(IReadOnlyList<(Guid ArticleId, decimal Quantity)> Rows) : ParsedValue;
    /// v0.1.61 — an Agent field with "multiple": the picked agents in order
    /// and the organizer (one of them, or null).
    public sealed record AgentsValue(IReadOnlyList<Guid> Ids, Guid? OrganizerId) : ParsedValue;
    /// v0.1.61 — invitees: a contact id (resolved to name + e-mail) or a free address.
    public sealed record InviteesValue(IReadOnlyList<(Guid? ContactId, string Email, string Name)> People) : ParsedValue;
    /// v0.1.61 — attachments of this ticket, in the order picked.
    public sealed record AttachmentsValue(IReadOnlyList<Guid> Ids) : ParsedValue;

    /// <paramref name="sanitizeHtml"/> cleans a rich-text LongText answer
    /// (the ticket-note sanitizer in production). Without one, rich-text
    /// answers are rejected rather than stored unsanitized.
    public static ParsedAnswers Parse(
        FormDefinition def, JsonObject? answers, FormRuntimeSettings limits, Func<string, string?>? sanitizeHtml = null)
    {
        answers ??= new JsonObject();
        var byId = def.Fields.ToDictionary(f => f.Id);
        foreach (var (key, _) in answers)
        {
            if (!Guid.TryParse(key, out var fid) || !byId.TryGetValue(fid, out var f) || !FormFieldType.IsInput(f.Type))
                throw new FormAnswerException("The form contains an unknown field. Reload the form and try again.");
        }

        var values = new Dictionary<Guid, ParsedValue>();
        var agents = new HashSet<Guid>();
        var contacts = new HashSet<Guid>();
        var articles = new HashSet<Guid>();
        var queues = new HashSet<Guid>();
        var invitees = new HashSet<Guid>();
        var attachments = new HashSet<Guid>();

        foreach (var f in def.Fields)
        {
            if (!FormFieldType.IsInput(f.Type)) continue;
            // v0.1.61 — a field hidden by its "show when" checkbox is never
            // required and its answer (if the client still sent one) is dropped.
            if (!FormTemplateValidator.IsShown(f, answers)) continue;
            answers.TryGetPropertyValue(f.Id.ToString(), out var node);
            var parsed = node is null ? null : ParseOne(f, node, limits, sanitizeHtml);
            if (parsed is null)
            {
                if (f.Required) throw new FormAnswerException($"'{f.Label}' is required.", f.Id);
                continue;
            }
            switch (parsed)
            {
                case RefValue r when f.Type == FormFieldType.Agent: agents.Add(r.Id); break;
                case RefValue r when f.Type == FormFieldType.Contact: contacts.Add(r.Id); break;
                case RefValue r when f.Type == FormFieldType.Queue: queues.Add(r.Id); break;
                case ArticleRowsValue a: foreach (var row in a.Rows) articles.Add(row.ArticleId); break;
                case AgentsValue av: foreach (var id in av.Ids) agents.Add(id); break;
                case InviteesValue iv: foreach (var p in iv.People) if (p.ContactId is { } cid) invitees.Add(cid); break;
                case AttachmentsValue at: foreach (var id in at.Ids) attachments.Add(id); break;
            }
            values[f.Id] = parsed;
        }

        return new ParsedAnswers(values, new FormReferenceSet(agents, contacts, articles)
        {
            QueueIds = queues, InviteeContactIds = invitees, AttachmentIds = attachments,
        });
    }

    /// Returns null for "no answer" (empty string, empty list, null).
    private static ParsedValue? ParseOne(FormField f, JsonNode node, FormRuntimeSettings limits, Func<string, string?>? sanitizeHtml)
    {
        FormAnswerException Bad(string msg) => new($"'{f.Label}': {msg}", f.Id);

        switch (f.Type)
        {
            case FormFieldType.LongText when node is JsonObject rich:
            {
                // Rich text (v0.1.55): {"html": "…"}. The length limit counts
                // the visible text; the raw HTML gets a generous hard cap so
                // markup can't be used to store unbounded data.
                var raw = rich["html"] is { } h ? ReadString(h) : null;
                if (raw is null || rich.Count != 1) throw Bad("expected text.");
                if (raw.Length > HtmlMaxLength(limits)) throw Bad($"at most {limits.LongTextMaxLength} characters.");
                if (sanitizeHtml is null) throw Bad("rich text is not accepted here.");
                var clean = (sanitizeHtml(raw) ?? string.Empty).Trim();
                var text = KnowledgeBase.KbBodyStripper.HtmlToText(clean);
                if (text.Length == 0) return null;
                if (text.Length > limits.LongTextMaxLength) throw Bad($"at most {limits.LongTextMaxLength} characters.");
                return new HtmlValue(clean);
            }
            case FormFieldType.ShortText when node is JsonObject withOrders:
            {
                // {"text": "…", "orders": [{"id": guid, "label": "…"}]}. An
                // order only counts while its label is still in the text, so
                // deleting the label drops the link; none left = plain text.
                if (withOrders.Count != 2 || withOrders["text"] is not { } tn || ReadString(tn) is not { } raw
                    || withOrders["orders"] is not JsonArray arr)
                    throw Bad("expected text.");
                var s = raw.Replace("\r\n", "\n").Replace('\n', ' ').Trim();
                if (s.Length == 0) return null;
                if (s.Length > FormLimits.ShortTextMax) throw Bad($"at most {FormLimits.ShortTextMax} characters.");
                if (arr.Count > FormLimits.ShortTextOrdersMax) throw Bad("too many linked orders.");
                var orders = new List<FormOrderRef>();
                foreach (var item in arr)
                {
                    if (item is not JsonObject o || o.Count != 2
                        || o["id"] is not { } idn || ReadString(idn) is not { } ids || !Guid.TryParse(ids, out var oid)
                        || o["label"] is not { } ln || ReadString(ln) is not { } label)
                        throw Bad("invalid order reference.");
                    label = label.Trim();
                    if (label.Length == 0 || label.Length > FormLimits.ShortTextMax) throw Bad("invalid order reference.");
                    if (!s.Contains(label, StringComparison.Ordinal) || orders.Any(x => x.Id == oid)) continue;
                    orders.Add(new FormOrderRef(oid, label));
                }
                return orders.Count == 0 ? new TextValue(s) : new TextWithOrdersValue(s, orders);
            }
            case FormFieldType.ShortText:
            case FormFieldType.LongText:
            {
                var s = ReadString(node) ?? throw Bad("expected text.");
                s = s.Replace("\r\n", "\n").Trim();
                if (f.Type == FormFieldType.ShortText) s = s.Replace('\n', ' ');
                if (s.Length == 0) return null;
                var max = f.Type == FormFieldType.ShortText ? FormLimits.ShortTextMax : limits.LongTextMaxLength;
                if (s.Length > max) throw Bad($"at most {max} characters.");
                return new TextValue(s);
            }
            case FormFieldType.Number:
            {
                decimal d;
                if (TryReadDecimal(node, out var dv)) d = dv;
                else if (ReadString(node) is { } str)
                {
                    if (str.Trim().Length == 0) return null;
                    if (!decimal.TryParse(str, System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture, out d)) throw Bad("expected a number.");
                }
                else throw Bad("expected a number.");
                if (Math.Abs(d) > FormLimits.NumberAbsMax) throw Bad("number out of range.");
                if (f.Min is { } min && d < min) throw Bad($"must be at least {min}.");
                if (f.Max is { } max2 && d > max2) throw Bad($"must be at most {max2}.");
                var decimals = f.Decimals ?? 0;
                if (decimal.Round(d, decimals) != d) throw Bad(decimals == 0 ? "must be a whole number." : $"at most {decimals} decimals.");
                return new NumberValue(d);
            }
            case FormFieldType.Checkbox:
            {
                var kind = node is JsonValue v ? v.GetValueKind() : System.Text.Json.JsonValueKind.Undefined;
                if (kind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                    throw Bad("expected yes or no.");
                // An unticked required checkbox counts as "not answered"
                // (e.g. "I confirmed the backup").
                if (kind == System.Text.Json.JsonValueKind.True) return new BoolValue(true);
                return f.Required ? null : new BoolValue(false);
            }
            case FormFieldType.Dropdown:
            {
                var s = ReadString(node) ?? throw Bad("expected an option.");
                if (s.Length == 0) return null;
                if (f.Options!.All(o => o.Id != s)) throw Bad("unknown option.");
                return new OptionValue(s);
            }
            case FormFieldType.MultiSelect:
            case FormFieldType.CheckboxGroup:
            {
                if (node is not JsonArray arr) throw Bad("expected a list of options.");
                var picked = new List<string>();
                foreach (var item in arr)
                {
                    var s = item is null ? null : ReadString(item);
                    if (s is null || f.Options!.All(o => o.Id != s)) throw Bad("unknown option.");
                    if (!picked.Contains(s)) picked.Add(s);
                }
                if (picked.Count == 0) return null;
                // Keep the template's option order, not the click order.
                picked = f.Options!.Where(o => picked.Contains(o.Id)).Select(o => o.Id).ToList();
                return new OptionsValue(picked);
            }
            case FormFieldType.Date:
            {
                var s = ReadString(node) ?? throw Bad("expected a date.");
                if (s.Length == 0) return null;
                if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d)) throw Bad("expected a date (yyyy-MM-dd).");
                return new DateValue(d);
            }
            case FormFieldType.DateTime:
            {
                var s = ReadString(node) ?? throw Bad("expected a date and time.");
                if (s.Length == 0) return null;
                if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)) throw Bad("expected a date and time.");
                return new DateTimeValue(dt.ToUniversalTime());
            }
            case FormFieldType.Duration:
            {
                int minutes;
                if (TryReadDecimal(node, out var md) && md == decimal.Truncate(md) && Math.Abs(md) <= int.MaxValue) minutes = (int)md;
                else if (ReadString(node) is { } str && str.Trim().Length == 0) return null;
                else throw Bad("expected a duration in minutes.");
                if (minutes < 0 || minutes > FormLimits.DurationMaxMinutes) throw Bad("duration out of range.");
                if (minutes == 0) return f.Required ? null : new DurationValue(0);
                return new DurationValue(minutes);
            }
            case FormFieldType.Agent when f.Multiple == true:
            {
                // {"ids": ["…"], "organizerId": "…" | null}
                if (node is not JsonObject ao || ao["ids"] is not JsonArray idArr) throw Bad("expected a list of agents.");
                var ids = new List<Guid>();
                foreach (var item in idArr)
                {
                    var s = item is null ? null : ReadString(item);
                    if (s is null || !Guid.TryParse(s, out var id) || id == Guid.Empty) throw Bad("invalid agent.");
                    if (!ids.Contains(id)) ids.Add(id);
                }
                if (ids.Count == 0) return null;
                if (ids.Count > FormLimits.AgentsMax) throw Bad($"at most {FormLimits.AgentsMax} agents.");
                Guid? organizer = null;
                if (ao["organizerId"] is { } on && ReadString(on) is { Length: > 0 } os)
                {
                    if (!Guid.TryParse(os, out var og) || !ids.Contains(og)) throw Bad("the organizer must be one of the picked agents.");
                    organizer = og;
                }
                return new AgentsValue(ids, organizer);
            }
            case FormFieldType.Address:
            {
                var s = ReadString(node) ?? throw Bad("expected an address.");
                s = s.Replace("\r\n", "\n").Trim();
                if (s.Length == 0) return null;
                if (s.Length > FormLimits.AddressMax) throw Bad($"at most {FormLimits.AddressMax} characters.");
                return new TextValue(s);
            }
            case FormFieldType.Invitees:
            {
                // [{"contactId": "…"} | {"email": "…", "name": "…"}]
                if (node is not JsonArray arr) throw Bad("expected a list of people.");
                var people = new List<(Guid?, string, string)>();
                foreach (var item in arr)
                {
                    if (item is not JsonObject po) throw Bad("invalid person.");
                    if (po["contactId"] is { } cn && ReadString(cn) is { Length: > 0 } cs)
                    {
                        if (!Guid.TryParse(cs, out var cid) || cid == Guid.Empty) throw Bad("invalid contact.");
                        if (people.All(x => x.Item1 != cid)) people.Add((cid, string.Empty, string.Empty));
                        continue;
                    }
                    var email = (po["email"] is { } en ? ReadString(en) : null)?.Trim() ?? string.Empty;
                    var name = (po["name"] is { } nn ? ReadString(nn) : null)?.Trim() ?? string.Empty;
                    if (!IsEmail(email)) throw Bad($"'{(email.Length > 60 ? email[..60] : email)}' is not a valid e-mail address.");
                    if (name.Length > FormLimits.InviteeNameMax) throw Bad("a name is too long.");
                    if (people.All(x => !string.Equals(x.Item2, email, StringComparison.OrdinalIgnoreCase)))
                        people.Add((null, email, name));
                }
                if (people.Count == 0) return null;
                if (people.Count > FormLimits.InviteesMax) throw Bad($"at most {FormLimits.InviteesMax} people.");
                return new InviteesValue(people);
            }
            case FormFieldType.Attachments:
            {
                if (node is not JsonArray arr) throw Bad("expected a list of files.");
                var ids = new List<Guid>();
                foreach (var item in arr)
                {
                    // An id string, or a stored row {"id": …} sent back on edit.
                    var s = item is JsonObject ro && ro["id"] is { } idn ? ReadString(idn) : item is null ? null : ReadString(item);
                    if (s is null || !Guid.TryParse(s, out var id) || id == Guid.Empty) throw Bad("invalid file.");
                    if (!ids.Contains(id)) ids.Add(id);
                }
                if (ids.Count == 0) return null;
                if (ids.Count > FormLimits.AttachmentsMax) throw Bad($"at most {FormLimits.AttachmentsMax} files.");
                return new AttachmentsValue(ids);
            }
            case FormFieldType.Agent:
            case FormFieldType.Contact:
            case FormFieldType.Queue:
            {
                var s = ReadString(node) ?? throw Bad("expected a selection.");
                if (s.Length == 0) return null;
                if (!Guid.TryParse(s, out var id) || id == Guid.Empty) throw Bad("invalid selection.");
                return new RefValue(id);
            }
            case FormFieldType.Article:
            {
                if (node is not JsonArray arr) throw Bad("expected a list of articles.");
                var rows = new List<(Guid, decimal)>();
                foreach (var item in arr)
                {
                    if (item is not JsonObject o) throw Bad("invalid article row.");
                    var idStr = o["articleId"] is { } idNode ? ReadString(idNode) : null;
                    if (idStr is null || !Guid.TryParse(idStr, out var aid) || aid == Guid.Empty) throw Bad("invalid article.");
                    decimal qty;
                    if (o["quantity"] is { } qn && TryReadDecimal(qn, out var q)) qty = q;
                    else throw Bad("every article needs a quantity.");
                    if (qty <= 0) throw Bad("quantities must be larger than 0.");
                    if (qty > limits.MaxArticleQuantity) throw Bad($"quantities must be at most {limits.MaxArticleQuantity}.");
                    if (f.Min is { } qmin && qty < qmin) throw Bad($"quantities must be at least {qmin}.");
                    if (f.Max is { } qmax && qty > qmax) throw Bad($"quantities must be at most {qmax}.");
                    if (decimal.Round(qty, 4) != qty) throw Bad("at most 4 decimals per quantity.");
                    rows.Add((aid, qty));
                }
                if (rows.Count == 0) return null;
                var maxRows = Math.Min(f.MaxRows ?? limits.MaxArticleRows, limits.MaxArticleRows);
                if (rows.Count > maxRows) throw Bad($"at most {maxRows} articles.");
                if (f.MinRows is { } minRows && rows.Count < minRows) throw Bad($"at least {minRows} articles.");
                if (rows.Select(r => r.Item1).Distinct().Count() != rows.Count) throw Bad("an article is listed twice; combine the quantities.");
                return new ArticleRowsValue(rows);
            }
        }
        throw Bad("unsupported field.");
    }

    /// v0.1.58 — the first DateTime field with a trigger key whose answer is
    /// new or changed (vs <paramref name="previous"/>, the stored answers of an
    /// edit) and not after <paramref name="nowUtc"/>; null when none. A
    /// trigger may use such a date as the pending-till, which is never set in
    /// the past — so the agent hears about it while filling in the form.
    public static FormField? TriggerDateInPast(FormDefinition def, ParsedAnswers parsed, JsonObject? previous, DateTime nowUtc)
    {
        foreach (var f in def.Fields)
        {
            if (f.Type != FormFieldType.DateTime || f.TriggerKey is null) continue;
            if (!parsed.Values.TryGetValue(f.Id, out var v) || v is not DateTimeValue dt) continue;
            if (previous?[f.Id.ToString()] is JsonValue pv && pv.GetValueKind() == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(pv.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var before)
                && before.UtcDateTime == dt.Value.UtcDateTime)
                continue;
            if (dt.Value.UtcDateTime <= nowUtc) return f;
        }
        return null;
    }

    /// Hard cap on the raw HTML of a rich-text answer: markup around the
    /// visible text (formatting, links, order pills) needs room, but not
    /// without bound.
    internal static int HtmlMaxLength(FormRuntimeSettings limits) => limits.LongTextMaxLength * 8;

    private static string? ReadString(JsonNode node)
        => node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;

    /// A pragmatic address check (one @, a dot in the domain, no spaces) —
    /// the mail server is the real judge; this keeps typos out of invitations.
    public static bool IsEmail(string s)
    {
        if (s.Length is < 3 or > FormLimits.EmailMax || s.Any(char.IsWhiteSpace)) return false;
        var at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@') || at == s.Length - 1) return false;
        var domain = s[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    /// Reads a JSON number whatever backs the node (a parsed request body is
    /// JsonElement-backed, a node built in code holds a CLR primitive).
    internal static bool TryReadDecimal(JsonNode node, out decimal value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number) return false;
        return decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// Writes the normalized answer document. References must all be present
    /// in <paramref name="refs"/> (the service rejects missing ones first).
    public static JsonObject Build(FormDefinition def, ParsedAnswers parsed, FormResolvedReferences refs)
    {
        var o = new JsonObject();
        foreach (var f in def.Fields)
        {
            if (!parsed.Values.TryGetValue(f.Id, out var v)) continue;
            JsonNode? node = v switch
            {
                TextValue t => JsonValue.Create(t.Value),
                HtmlValue h => new JsonObject { ["html"] = h.Html },
                TextWithOrdersValue t => new JsonObject
                {
                    ["text"] = t.Text,
                    ["orders"] = new JsonArray(t.Orders.Select(o => (JsonNode?)new JsonObject
                    {
                        ["id"] = o.Id.ToString(),
                        ["label"] = o.Label,
                    }).ToArray()),
                },
                NumberValue n => JsonValue.Create(n.Value),
                BoolValue b => JsonValue.Create(b.Value),
                OptionValue ov => JsonValue.Create(ov.OptionId),
                OptionsValue ovs => new JsonArray(ovs.OptionIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                DateValue d => JsonValue.Create(d.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                DateTimeValue dt => JsonValue.Create(dt.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)),
                DurationValue du => JsonValue.Create(du.Minutes),
                AgentsValue av => new JsonArray(av.Ids.Select(id =>
                {
                    var o2 = new JsonObject { ["id"] = id.ToString(), ["name"] = refs.Agents[id] };
                    if (av.OrganizerId == id) o2["organizer"] = true;
                    return (JsonNode?)o2;
                }).ToArray()),
                InviteesValue iv => new JsonArray(iv.People.Select(p => p.ContactId is { } cid
                    ? (JsonNode?)new JsonObject
                    {
                        ["contactId"] = cid.ToString(),
                        ["name"] = refs.InviteeContacts[cid].Name,
                        ["email"] = refs.InviteeContacts[cid].Email,
                    }
                    : new JsonObject { ["name"] = p.Name, ["email"] = p.Email }).ToArray()),
                AttachmentsValue at => new JsonArray(at.Ids.Select(id => (JsonNode?)new JsonObject
                {
                    ["id"] = id.ToString(),
                    ["name"] = refs.Attachments[id].Name,
                    ["size"] = refs.Attachments[id].Size,
                    ["contentType"] = refs.Attachments[id].ContentType,
                }).ToArray()),
                RefValue r when f.Type == FormFieldType.Agent => new JsonObject
                {
                    ["id"] = r.Id.ToString(),
                    ["name"] = refs.Agents[r.Id],
                },
                RefValue r when f.Type == FormFieldType.Queue => new JsonObject
                {
                    ["id"] = r.Id.ToString(),
                    ["name"] = refs.Queues[r.Id],
                },
                RefValue r => new JsonObject
                {
                    ["id"] = r.Id.ToString(),
                    ["name"] = refs.Contacts[r.Id].Name,
                    ["email"] = refs.Contacts[r.Id].Email,
                },
                ArticleRowsValue a => new JsonArray(a.Rows.Select(row => (JsonNode?)new JsonObject
                {
                    ["articleId"] = row.ArticleId.ToString(),
                    ["code"] = refs.Articles[row.ArticleId].Code,
                    ["name"] = refs.Articles[row.ArticleId].Name,
                    ["quantity"] = row.Quantity,
                }).ToArray()),
                _ => null,
            };
            if (node is not null) o[f.Id.ToString()] = node;
        }
        return o;
    }
}

/// Turns a definition + normalized answers into display text: the note HTML
/// (server-rendered, every value HTML-encoded) and the flattened search text.
public static class FormRenderer
{
    /// A stored DateTime answer as UTC; null when empty or unreadable.
    public static DateTime? ReadUtc(JsonNode? v)
        => v is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.String
           && DateTimeOffset.TryParse(jv.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime : null;

    /// v0.1.61 — the end of a Duration field that counts from a DateTime
    /// field: start + duration. Null when it does not count from one, or the
    /// start or a positive duration is missing.
    public static DateTime? DurationEndUtc(FormDefinition def, FormField f, JsonObject? answers)
    {
        if (answers is null || f.Type != FormFieldType.Duration || f.DurationFrom is not { } from) return null;
        if (!def.Fields.Any(x => x.Id == from && x.Type == FormFieldType.DateTime)) return null;
        var start = ReadUtc(answers[from.ToString()]);
        if (start is null || answers[f.Id.ToString()] is not { } node
            || !FormAnswerValidator.TryReadDecimal(node, out var minutes) || minutes <= 0) return null;
        return start.Value.AddMinutes((double)minutes);
    }

    /// A Duration that counts from a start reads "2:00 (until …)" in notes.
    private static string WithDurationEnd(string text, FormDefinition def, FormField f, JsonObject? answers,
        Func<DateTimeOffset, string> formatDateTime)
        => text.Length > 0 && DurationEndUtc(def, f, answers) is { } end
            ? $"{text} (until {formatDateTime(new DateTimeOffset(end, TimeSpan.Zero))})"
            : text;

    public static string DisplayValue(FormField f, JsonNode? v, Func<DateTimeOffset, string> formatDateTime)
    {
        if (v is null) return string.Empty;
        try
        {
            switch (f.Type)
            {
                case FormFieldType.LongText when RichHtml(v) is { } html:
                    return KnowledgeBase.KbBodyStripper.HtmlToText(html);
                case FormFieldType.ShortText when v is JsonObject o:
                    return o["text"]?.GetValue<string>() ?? string.Empty;
                case FormFieldType.ShortText:
                case FormFieldType.LongText:
                    return v.GetValue<string>();
                case FormFieldType.Number:
                    return FormAnswerValidator.TryReadDecimal(v, out var num) ? num.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
                case FormFieldType.Checkbox:
                    return v.GetValue<bool>() ? "Yes" : "No";
                case FormFieldType.Dropdown:
                {
                    var id = v.GetValue<string>();
                    return f.Options?.FirstOrDefault(o => o.Id == id)?.Label ?? id;
                }
                case FormFieldType.MultiSelect:
                case FormFieldType.CheckboxGroup:
                    return string.Join(", ", v.AsArray().Select(n =>
                    {
                        var id = n!.GetValue<string>();
                        return f.Options?.FirstOrDefault(o => o.Id == id)?.Label ?? id;
                    }));
                case FormFieldType.Date:
                {
                    var s = v.GetValue<string>();
                    return DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d)
                        ? d.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)
                        : s;
                }
                case FormFieldType.DateTime:
                {
                    var s = v.GetValue<string>();
                    return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? formatDateTime(dt) : s;
                }
                case FormFieldType.Duration:
                {
                    var m = FormAnswerValidator.TryReadDecimal(v, out var md) ? (int)md : 0;
                    return $"{m / 60}:{m % 60:00}";
                }
                case FormFieldType.Agent when v is JsonArray agents:
                    return string.Join(", ", agents.OfType<JsonObject>().Select(a =>
                        Str(a["name"]) + (a["organizer"] is JsonValue ov && ov.GetValueKind() == System.Text.Json.JsonValueKind.True ? " (organizer)" : "")));
                case FormFieldType.Agent:
                case FormFieldType.Queue:
                    return v["name"]?.GetValue<string>() ?? string.Empty;
                case FormFieldType.Address:
                    return v.GetValue<string>();
                case FormFieldType.Invitees:
                    return string.Join(", ", v.AsArray().OfType<JsonObject>().Select(p =>
                    {
                        var name = Str(p["name"]);
                        var email = Str(p["email"]);
                        return name.Length > 0 && email.Length > 0 ? $"{name} <{email}>" : name + email;
                    }));
                case FormFieldType.Attachments:
                    return string.Join(", ", v.AsArray().OfType<JsonObject>().Select(a => Str(a["name"])));
                case FormFieldType.Contact:
                {
                    var name = v["name"]?.GetValue<string>() ?? string.Empty;
                    var email = v["email"]?.GetValue<string>() ?? string.Empty;
                    return email.Length > 0 && name.Length > 0 ? $"{name} <{email}>" : name + email;
                }
                case FormFieldType.Article:
                    return string.Join("; ", v.AsArray().Select(r =>
                        $"{FormatQty(FormAnswerValidator.TryReadDecimal(r!["quantity"]!, out var q) ? q : 0)} × {r["code"]?.GetValue<string>()} {r["name"]?.GetValue<string>()}".Trim()));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return string.Empty;
        }
        return string.Empty;
    }

    /// The sanitized HTML of a rich-text LongText answer, or null for a plain
    /// (legacy) string answer.
    public static string? RichHtml(JsonNode? v)
        => v is JsonObject o && o["html"] is JsonValue h && h.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? h.GetValue<string>() : null;

    /// The orders linked in a ShortText answer ({"text", "orders"}); empty
    /// for a plain string answer. Malformed entries are skipped.
    public static IReadOnlyList<FormOrderRef> ShortTextOrders(JsonNode? v)
    {
        if (v is not JsonObject o || o["orders"] is not JsonArray arr) return Array.Empty<FormOrderRef>();
        var list = new List<FormOrderRef>();
        foreach (var item in arr)
        {
            if (item is JsonObject r && r["id"] is JsonValue idv && idv.GetValueKind() == System.Text.Json.JsonValueKind.String
                && Guid.TryParse(idv.GetValue<string>(), out var id)
                && r["label"] is JsonValue lv && lv.GetValueKind() == System.Text.Json.JsonValueKind.String
                && lv.GetValue<string>() is { Length: > 0 } label)
                list.Add(new FormOrderRef(id, label));
        }
        return list;
    }

    /// A value as note-cell HTML: rich LongText as its sanitized HTML, a
    /// ShortText with linked orders with each order's first label occurrence
    /// as the same order pill the composer inserts (so it is clickable and
    /// lands in the ticket's linked orders), anything else encoded text.
    internal static string CellHtml(FormField f, JsonNode? v, string display)
    {
        if (display.Length == 0) return "—";
        if (f.Type == FormFieldType.LongText && RichHtml(v) is { } html) return html;
        if (f.Type == FormFieldType.Article && v is JsonArray rows && rows.Count > 0) return ArticleTableHtml(rows);
        if (f.Type != FormFieldType.ShortText) return Lines(display);
        var orders = ShortTextOrders(v);
        if (orders.Count == 0) return E(display);

        // Place each pill at its label's first free occurrence, then emit the
        // text left to right; an overlapping label keeps the earlier pill.
        var spans = new List<(int Start, FormOrderRef Order)>();
        foreach (var o in orders)
        {
            var at = display.IndexOf(o.Label, StringComparison.Ordinal);
            while (at >= 0 && spans.Any(sp => at < sp.Start + sp.Order.Label.Length && sp.Start < at + o.Label.Length))
                at = display.IndexOf(o.Label, at + 1, StringComparison.Ordinal);
            if (at >= 0) spans.Add((at, o));
        }
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var (start, o) in spans.OrderBy(sp => sp.Start))
        {
            sb.Append(E(display[pos..start]))
              .Append("<span data-type=\"orderMention\" class=\"sd-order-mention\" data-order-mention=\"true\" data-order-id=\"")
              .Append(o.Id.ToString()).Append("\" data-label=\"").Append(E(o.Label)).Append("\">")
              .Append(E(o.Label)).Append("</span>");
            pos = start + o.Label.Length;
        }
        sb.Append(E(display[pos..]));
        return sb.ToString();
    }

    /// v0.1.58 — articles as a small table, one row per article (quantity ·
    /// code · name), instead of one long "; "-joined line. Styled by
    /// <c>.sd-form-articles</c>; every value encoded.
    internal static string ArticleTableHtml(JsonArray rows)
    {
        var sb = new StringBuilder("<table class=\"sd-form-articles\"><tbody>");
        foreach (var r in rows)
        {
            if (r is not JsonObject o) continue;
            var qty = o["quantity"] is { } qn && FormAnswerValidator.TryReadDecimal(qn, out var q) ? FormatQty(q) : string.Empty;
            sb.Append("<tr><td class=\"sd-form-articles-qty\">").Append(E(qty)).Append(" ×</td>")
              .Append("<td class=\"sd-form-articles-code\">").Append(E(Str(o["code"]))).Append("</td>")
              .Append("<td>").Append(E(Str(o["name"]))).Append("</td></tr>");
        }
        return sb.Append("</tbody></table>").ToString();
    }

    /// Articles one per line — the readable form of an article answer in an
    /// update note's "old → new" cell.
    private static string ArticleLines(JsonNode? v)
        => v is JsonArray rows && rows.Count > 0
            ? string.Join("\n", rows.OfType<JsonObject>().Select(o =>
                $"{(o["quantity"] is { } qn && FormAnswerValidator.TryReadDecimal(qn, out var q) ? FormatQty(q) : "")} × {Str(o["code"])} {Str(o["name"])}".Trim()))
            : string.Empty;

    private static string Str(JsonNode? n)
        => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : string.Empty;

    public static string FormatQty(decimal q) => q.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
    private static string Lines(string s) => E(s).Replace("\n", "<br>");

    /// The full form as note HTML. <paramref name="changedFieldIds"/> non-null
    /// = update note: only those fields, rendered as "old → new".
    public static string RenderNoteHtml(
        Guid formId, string formName, int version, FormDefinition def,
        JsonObject answers, JsonObject? previous, IReadOnlyCollection<Guid>? changedFieldIds,
        Func<DateTimeOffset, string> formatDateTime)
    {
        var sb = new StringBuilder();
        var title = version == 1 ? $"Form: {formName}" : $"Form updated: {formName} · v{version}";
        sb.Append("<p><strong>").Append(E(title)).Append("</strong> ")
          .Append("<span class=\"sd-form-pill\" data-form-id=\"").Append(formId.ToString()).Append("\">Open form</span></p>");

        var rows = new StringBuilder();
        var pendingSection = (string?)null;
        foreach (var f in def.Fields)
        {
            if (f.Type == FormFieldType.SectionHeader)
            {
                pendingSection = f.Label;
                continue;
            }
            var key = f.Id.ToString();
            if (changedFieldIds is not null && !changedFieldIds.Contains(f.Id)) continue;
            var now = WithDurationEnd(DisplayValue(f, answers[key], formatDateTime), def, f, answers, formatDateTime);
            string cell;
            if (changedFieldIds is not null)
            {
                // Plain text on the old side (a pill there would re-link an
                // order the agent removed); a short text's new side keeps its
                // order pills so an order added on edit is linked too.
                var before = f.Type == FormFieldType.Article ? ArticleLines(previous?[key])
                    : WithDurationEnd(DisplayValue(f, previous?[key], formatDateTime), def, f, previous, formatDateTime);
                var after = f.Type == FormFieldType.ShortText ? CellHtml(f, answers[key], now)
                    : f.Type == FormFieldType.Article ? Lines(ArticleLines(answers[key]) is { Length: > 0 } al ? al : "—")
                    : Lines(now.Length == 0 ? "—" : now);
                cell = $"{Lines(before.Length == 0 ? "—" : before)} → {after}";
            }
            else
            {
                if (now.Length == 0) continue;
                // Rich text goes in as its (already sanitized) HTML; the note
                // as a whole is sanitized again before it is stored.
                cell = CellHtml(f, answers[key], now);
            }
            if (pendingSection is not null)
            {
                rows.Append("<tr><th colspan=\"2\">").Append(E(pendingSection)).Append("</th></tr>");
                pendingSection = null;
            }
            rows.Append("<tr><td><strong>").Append(E(f.Label)).Append("</strong></td><td>").Append(cell).Append("</td></tr>");
        }

        if (rows.Length > 0)
            sb.Append("<table><tbody>").Append(rows).Append("</tbody></table>");
        else if (changedFieldIds is not null)
            sb.Append("<p>No changes.</p>");
        else
            sb.Append("<p>No fields filled in.</p>");
        return sb.ToString();
    }

    /// v0.1.61 — every attachment id in the Attachments fields of an answer set.
    public static IReadOnlyList<Guid> AttachmentIds(FormDefinition def, JsonObject answers)
    {
        var ids = new List<Guid>();
        foreach (var f in def.Fields)
        {
            if (f.Type != FormFieldType.Attachments || answers[f.Id.ToString()] is not JsonArray arr) continue;
            foreach (var row in arr)
            {
                if (row is JsonObject o && o["id"] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(v.GetValue<string>(), out var id) && !ids.Contains(id))
                    ids.Add(id);
            }
        }
        return ids;
    }

    public static string FlattenForSearch(FormDefinition def, JsonObject answers)
    {
        var sb = new StringBuilder();
        foreach (var f in def.Fields)
        {
            if (!FormFieldType.IsInput(f.Type)) continue;
            var v = DisplayValue(f, answers[f.Id.ToString()],
                dt => dt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            if (v.Length == 0) continue;
            sb.Append(f.Label).Append(": ").Append(v).Append('\n');
        }
        var s = sb.ToString();
        return s.Length > 20_000 ? s[..20_000] : s;
    }

    /// Field ids whose normalized value differs between two answer documents.
    public static IReadOnlyList<Guid> ChangedFields(FormDefinition def, JsonObject? before, JsonObject after)
    {
        var changed = new List<Guid>();
        foreach (var f in def.Fields)
        {
            if (!FormFieldType.IsInput(f.Type)) continue;
            var key = f.Id.ToString();
            var a = Canonical(before?[key]);
            var b = Canonical(after[key]);
            if (string.Equals(a, b, StringComparison.Ordinal)) continue;
            // A legacy plain-text LongText re-saved as rich text with the same
            // words is not a change; two rich answers compare by HTML, so a
            // formatting-only edit still counts.
            if (f.Type == FormFieldType.LongText
                && (RichHtml(before?[key]) is null) != (RichHtml(after[key]) is null)
                && string.Equals(
                    DisplayValue(f, before?[key], _ => string.Empty),
                    DisplayValue(f, after[key], _ => string.Empty), StringComparison.Ordinal))
                continue;
            changed.Add(f.Id);
        }
        return changed;
    }

    /// Key-order-independent rendering (jsonb reorders object keys) with
    /// numbers compared by value, so a round-trip through the database never
    /// reads as a change.
    internal static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case null: return string.Empty;
            case JsonObject o:
                return "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key + ":" + Canonical(kv.Value))) + "}";
            case JsonArray a:
                return "[" + string.Join(",", a.Select(Canonical)) + "]";
            case JsonValue v:
                if (FormAnswerValidator.TryReadDecimal(v, out var d)) return "n:" + d.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
                return v.ToJsonString();
        }
        return node.ToJsonString();
    }
}

/// <paramref name="BoPlanTemplateId"/> (v0.1.61): for a back-office
/// template, the form that "Plan" opens (e.g. the planning form).
/// <paramref name="Outlook"/> (v0.1.61): the Outlook appointment mapping.
/// <paramref name="HeaderButtonLabel"/> + <paramref name="HeaderButtonQueueIds"/>
/// (v0.1.61): a button with this label next to the ticket title opens the
/// form on tickets in those queues. <paramref name="InPicker"/> false: not
/// offered through <c>::</c> (<see cref="QueueIds"/> unused).
public sealed record FormTemplateSummary(
    Guid Id, string Name, string Description, bool IsActive, IReadOnlyList<Guid> QueueIds,
    int FieldCount, DateTime CreatedUtc, DateTime UpdatedUtc, bool SendToBackoffice = false,
    Guid? BoPlanTemplateId = null, bool OutlookEnabled = false, string? HeaderButtonLabel = null,
    IReadOnlyList<Guid>? HeaderButtonQueueIds = null, bool InPicker = true)
{
    public bool InPickerFor(Guid queueId) => FormTemplateScope.InPicker(InPicker, QueueIds, queueId);
    public bool ButtonFor(Guid queueId) => FormTemplateScope.Button(HeaderButtonLabel, HeaderButtonQueueIds, queueId);
}

public sealed record FormTemplateDetail(
    Guid Id, string Name, string Description, bool IsActive, IReadOnlyList<Guid> QueueIds,
    FormDefinition Definition, int FieldCount, DateTime CreatedUtc, DateTime UpdatedUtc,
    bool SendToBackoffice = false, Guid? BoPlanTemplateId = null, FormOutlookMapping? Outlook = null,
    string? HeaderButtonLabel = null, IReadOnlyList<Guid>? HeaderButtonQueueIds = null, bool InPicker = true)
{
    /// Fillable on a ticket in this queue through `::` or the header button.
    public bool UsableIn(Guid queueId)
        => IsActive && (FormTemplateScope.InPicker(InPicker, QueueIds, queueId)
                        || FormTemplateScope.Button(HeaderButtonLabel, HeaderButtonQueueIds, queueId));
}

/// v0.1.61 — where a form template is offered. One rule for the usable list,
/// the save check and (mirrored in SQL) <c>ListAvailableForQueueAsync</c>.
public static class FormTemplateScope
{
    /// Offered through `::`: picker on, and no queue scope (= every queue) or this queue.
    public static bool InPicker(bool inPicker, IReadOnlyList<Guid> queueIds, Guid queueId)
        => inPicker && (queueIds.Count == 0 || queueIds.Contains(queueId));

    /// A header button: a label and this queue among the button's queues.
    public static bool Button(string? label, IReadOnlyList<Guid>? queueIds, Guid queueId)
        => !string.IsNullOrEmpty(label) && queueIds is not null && queueIds.Contains(queueId);
}

/// <paramref name="SendToBackoffice"/> (v0.1.54): forms filled from this
/// template land on the Back office forms work list.
public sealed record FormTemplateInput(
    string Name, string Description, bool IsActive, IReadOnlyList<Guid> QueueIds, FormDefinition Definition,
    bool SendToBackoffice = false, Guid? BoPlanTemplateId = null, FormOutlookMapping? Outlook = null,
    string? HeaderButtonLabel = null, IReadOnlyList<Guid>? HeaderButtonQueueIds = null, bool InPicker = true);

/// <paramref name="BoStatus"/> (v0.1.54): Open / Handled for a
/// back-office form, null otherwise; the side panel shows it to the agent.
public sealed record TicketFormSummary(
    Guid Id, Guid TicketId, string Name, int Version,
    Guid? CreatedByUserId, string? CreatedByName, DateTime CreatedUtc,
    Guid? UpdatedByUserId, string? UpdatedByName, DateTime UpdatedUtc,
    string? BoStatus = null,
    string? BoHandledByName = null, DateTime? BoHandledUtc = null);

/// Result of an optimistic form update: the new version and the back-office
/// status the form had before (so the service knows whether it reopened).
public sealed record TicketFormUpdateResult(int Version, string? PreviousBoStatus);

public sealed record TicketFormDetail(
    TicketFormSummary Summary, Guid? TemplateId, FormDefinition Snapshot, JsonObject Answers);

public sealed record TicketFormVersion(
    int Version, JsonObject Answers, long? EventId, Guid? SavedByUserId, string? SavedByName, DateTime SavedUtc);
