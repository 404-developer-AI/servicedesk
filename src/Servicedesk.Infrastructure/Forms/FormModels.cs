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

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        SectionHeader, ShortText, LongText, Number, Dropdown, MultiSelect, Checkbox,
        CheckboxGroup, Date, DateTime, Duration, Agent, Contact, Article,
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
    public const int DescriptionMax = 2000;
    public const int LabelMax = 300;
    public const int HelpMax = 1000;
    public const int OptionLabelMax = 200;
    public const int OptionIdMax = 40;
    public const int OptionsMax = 100;
    public const int ShortTextMax = 500;
    public const int DeleteReasonMax = 1000;
    public const int DurationMaxMinutes = 100_000;
    public const decimal NumberAbsMax = 1_000_000_000_000m;
    public const int DecimalsMax = 6;

    public const int HardMaxFields = 200;
    public const int HardMaxArticleRows = 500;
    public const int HardMaxLongText = 20_000;
    public const int HardMaxPerTicket = 200;
    public const decimal HardMaxArticleQuantity = 10_000_000m;
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

            // Defaults only for the types the fill dialog can pre-fill.
            if (f.Type is not (FormFieldType.ShortText or FormFieldType.LongText or FormFieldType.Number
                or FormFieldType.Dropdown or FormFieldType.Checkbox))
            {
                f.DefaultValue = string.Empty;
            }
            if (f.DefaultValue.Length > FormLimits.ShortTextMax) return "Default values must be at most 500 characters.";
            if (f.Type == FormFieldType.Dropdown && f.DefaultValue.Length > 0
                && f.Options!.All(o => o.Id != f.DefaultValue))
            {
                f.DefaultValue = string.Empty;
            }
        }

        if (def.InputFieldCount == 0) return "Add at least one input field.";
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// A reference an answer points to that the service must resolve against the
/// database (and reject when it does not exist / is out of scope).
public sealed record FormReferenceSet(
    IReadOnlySet<Guid> AgentIds,
    IReadOnlySet<Guid> ContactIds,
    IReadOnlySet<Guid> ArticleIds);

/// Resolved display data for references (written into the stored answer so
/// history keeps the name an agent saw, even after a rename).
public sealed record FormResolvedReferences(
    IReadOnlyDictionary<Guid, string> Agents,
    IReadOnlyDictionary<Guid, (string Name, string Email)> Contacts,
    IReadOnlyDictionary<Guid, (string Code, string Name)> Articles);

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
    int MaxPerTicket);

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
    public sealed record NumberValue(decimal Value) : ParsedValue;
    public sealed record BoolValue(bool Value) : ParsedValue;
    public sealed record OptionValue(string OptionId) : ParsedValue;
    public sealed record OptionsValue(IReadOnlyList<string> OptionIds) : ParsedValue;
    public sealed record DateValue(DateOnly Value) : ParsedValue;
    public sealed record DateTimeValue(DateTimeOffset Value) : ParsedValue;
    public sealed record DurationValue(int Minutes) : ParsedValue;
    public sealed record RefValue(Guid Id) : ParsedValue;
    public sealed record ArticleRowsValue(IReadOnlyList<(Guid ArticleId, decimal Quantity)> Rows) : ParsedValue;

    public static ParsedAnswers Parse(FormDefinition def, JsonObject? answers, FormRuntimeSettings limits)
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

        foreach (var f in def.Fields)
        {
            if (!FormFieldType.IsInput(f.Type)) continue;
            answers.TryGetPropertyValue(f.Id.ToString(), out var node);
            var parsed = node is null ? null : ParseOne(f, node, limits);
            if (parsed is null)
            {
                if (f.Required) throw new FormAnswerException($"'{f.Label}' is required.", f.Id);
                continue;
            }
            switch (parsed)
            {
                case RefValue r when f.Type == FormFieldType.Agent: agents.Add(r.Id); break;
                case RefValue r when f.Type == FormFieldType.Contact: contacts.Add(r.Id); break;
                case ArticleRowsValue a: foreach (var row in a.Rows) articles.Add(row.ArticleId); break;
            }
            values[f.Id] = parsed;
        }

        return new ParsedAnswers(values, new FormReferenceSet(agents, contacts, articles));
    }

    /// Returns null for "no answer" (empty string, empty list, null).
    private static ParsedValue? ParseOne(FormField f, JsonNode node, FormRuntimeSettings limits)
    {
        FormAnswerException Bad(string msg) => new($"'{f.Label}': {msg}", f.Id);

        switch (f.Type)
        {
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
            case FormFieldType.Agent:
            case FormFieldType.Contact:
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

    private static string? ReadString(JsonNode node)
        => node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;

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
                NumberValue n => JsonValue.Create(n.Value),
                BoolValue b => JsonValue.Create(b.Value),
                OptionValue ov => JsonValue.Create(ov.OptionId),
                OptionsValue ovs => new JsonArray(ovs.OptionIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                DateValue d => JsonValue.Create(d.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                DateTimeValue dt => JsonValue.Create(dt.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)),
                DurationValue du => JsonValue.Create(du.Minutes),
                RefValue r when f.Type == FormFieldType.Agent => new JsonObject
                {
                    ["id"] = r.Id.ToString(),
                    ["name"] = refs.Agents[r.Id],
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
    public static string DisplayValue(FormField f, JsonNode? v, Func<DateTimeOffset, string> formatDateTime)
    {
        if (v is null) return string.Empty;
        try
        {
            switch (f.Type)
            {
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
                case FormFieldType.Agent:
                    return v["name"]?.GetValue<string>() ?? string.Empty;
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

    public static string FormatQty(decimal q) => q.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private static string E(string s) => System.Net.WebUtility.HtmlEncode(s);

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
            var now = DisplayValue(f, answers[key], formatDateTime);
            string cell;
            if (changedFieldIds is not null)
            {
                var before = DisplayValue(f, previous?[key], formatDateTime);
                cell = $"{E(before.Length == 0 ? "—" : before)} → {E(now.Length == 0 ? "—" : now)}";
            }
            else
            {
                if (now.Length == 0) continue;
                cell = E(now).Replace("\n", "<br>");
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
            if (!string.Equals(a, b, StringComparison.Ordinal)) changed.Add(f.Id);
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

public sealed record FormTemplateSummary(
    Guid Id, string Name, string Description, bool IsActive, IReadOnlyList<Guid> QueueIds,
    int FieldCount, DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed record FormTemplateDetail(
    Guid Id, string Name, string Description, bool IsActive, IReadOnlyList<Guid> QueueIds,
    FormDefinition Definition, int FieldCount, DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed record FormTemplateInput(
    string Name, string Description, bool IsActive, IReadOnlyList<Guid> QueueIds, FormDefinition Definition);

public sealed record TicketFormSummary(
    Guid Id, Guid TicketId, string Name, int Version,
    Guid? CreatedByUserId, string? CreatedByName, DateTime CreatedUtc,
    Guid? UpdatedByUserId, string? UpdatedByName, DateTime UpdatedUtc);

public sealed record TicketFormDetail(
    TicketFormSummary Summary, Guid? TemplateId, FormDefinition Snapshot, JsonObject Answers);

public sealed record TicketFormVersion(
    int Version, JsonObject Answers, long? EventId, Guid? SavedByUserId, string? SavedByName, DateTime SavedUtc);
