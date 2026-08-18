using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Servicedesk.Infrastructure.Checklists;

/// v0.0.103 — the content of a checklist template: ordered sections, each
/// with ordered items. Stored as one JSON document on
/// <c>checklist_templates.definition</c> (the admin editor saves the whole
/// document; attaching to a ticket expands it into normalized rows).
public sealed class ChecklistTemplateDefinition
{
    [JsonPropertyName("sections")]
    public List<ChecklistTemplateSection> Sections { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static ChecklistTemplateDefinition Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ChecklistTemplateDefinition();
        try
        {
            return JsonSerializer.Deserialize<ChecklistTemplateDefinition>(json, JsonOptions)
                   ?? new ChecklistTemplateDefinition();
        }
        catch (JsonException)
        {
            return new ChecklistTemplateDefinition();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public int ItemCount => Sections.Sum(s => s.Items.Count);

    /// Flattened text (name/description are added by the caller) used by the
    /// admin search source — item titles + descriptions + labels.
    public string FlattenForSearch()
    {
        var sb = new StringBuilder();
        foreach (var s in Sections)
        {
            if (!string.IsNullOrWhiteSpace(s.Title)) sb.Append(s.Title).Append('\n');
            foreach (var i in s.Items)
            {
                sb.Append(i.Title).Append('\n');
                if (!string.IsNullOrWhiteSpace(i.Description)) sb.Append(i.Description).Append('\n');
                if (!string.IsNullOrWhiteSpace(i.TeamLabel)) sb.Append(i.TeamLabel).Append(' ');
                if (!string.IsNullOrWhiteSpace(i.TimingLabel)) sb.Append(i.TimingLabel).Append('\n');
            }
        }
        return sb.ToString();
    }
}

public sealed class ChecklistTemplateSection
{
    /// Section title; empty = "ungrouped" items (rendered without a heading).
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<ChecklistTemplateItem> Items { get; set; } = new();
}

public sealed class ChecklistTemplateItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// Free short label, e.g. "Back Office" / "Field Services".
    [JsonPropertyName("teamLabel")]
    public string TeamLabel { get; set; } = string.Empty;

    /// Free short label, e.g. "Week 2".
    [JsonPropertyName("timingLabel")]
    public string TimingLabel { get; set; } = string.Empty;

    [JsonPropertyName("linkUrl")]
    public string LinkUrl { get; set; } = string.Empty;

    [JsonPropertyName("linkLabel")]
    public string LinkLabel { get; set; } = string.Empty;

    /// Required items count towards completion and the close block;
    /// optional ones are informational.
    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; } = true;
}

/// Field limits shared by the template validator and the on-ticket item
/// endpoints (ad-hoc items obey the same caps).
public static class ChecklistLimits
{
    public const int NameMax = 200;
    public const int DescriptionMax = 4000;
    public const int ItemTitleMax = 300;
    public const int ItemDescriptionMax = 4000;
    public const int LabelMax = 60;
    public const int LinkMax = 2000;
    public const int SectionTitleMax = 200;
    public const int SectionsMax = 50;
    public const int NaReasonMax = 2000;
    public const int CommentMax = 4000;
    /// Absolute ceiling for the admin-tunable Checklists.MaxItemsPerChecklist.
    public const int HardMaxItems = 1000;
    /// Absolute ceiling for the admin-tunable Checklists.MaxPerTicket.
    public const int HardMaxPerTicket = 50;
}

/// Validation + normalization for template documents and single items.
/// Returns an English error message (surfaced verbatim by the API) or null.
public static class ChecklistTemplateValidator
{
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (name.Trim().Length > ChecklistLimits.NameMax) return $"Name must be at most {ChecklistLimits.NameMax} characters.";
        return null;
    }

    public static string? ValidateDescription(string? description)
    {
        if (description is not null && description.Length > ChecklistLimits.DescriptionMax)
            return $"Description must be at most {ChecklistLimits.DescriptionMax} characters.";
        return null;
    }

    /// Validates and normalizes (trims, drops empty items) the definition in
    /// place. <paramref name="maxItems"/> is the admin-tunable cap.
    public static string? ValidateAndNormalize(ChecklistTemplateDefinition def, int maxItems)
    {
        def.Sections ??= new List<ChecklistTemplateSection>();
        if (def.Sections.Count > ChecklistLimits.SectionsMax)
            return $"A checklist can have at most {ChecklistLimits.SectionsMax} sections.";

        var total = 0;
        foreach (var section in def.Sections)
        {
            section.Title = (section.Title ?? string.Empty).Trim();
            if (section.Title.Length > ChecklistLimits.SectionTitleMax)
                return $"Section titles must be at most {ChecklistLimits.SectionTitleMax} characters.";
            section.Items ??= new List<ChecklistTemplateItem>();
            // Drop rows the editor left blank (a trailing empty line in the
            // paste box, an added-then-abandoned item).
            section.Items.RemoveAll(i => string.IsNullOrWhiteSpace(i.Title));
            foreach (var item in section.Items)
            {
                var err = ValidateItem(item);
                if (err is not null) return err;
                total++;
            }
        }
        if (total == 0) return "Add at least one item.";
        if (total > maxItems) return $"A checklist can have at most {maxItems} items (Settings → Tickets → Checklists).";
        return null;
    }

    /// Normalizes and validates one item (template or ad-hoc).
    public static string? ValidateItem(ChecklistTemplateItem item)
    {
        item.Title = (item.Title ?? string.Empty).Trim();
        item.Description = (item.Description ?? string.Empty).Trim();
        item.TeamLabel = (item.TeamLabel ?? string.Empty).Trim();
        item.TimingLabel = (item.TimingLabel ?? string.Empty).Trim();
        item.LinkUrl = (item.LinkUrl ?? string.Empty).Trim();
        item.LinkLabel = (item.LinkLabel ?? string.Empty).Trim();

        if (item.Title.Length == 0) return "Item title is required.";
        if (item.Title.Length > ChecklistLimits.ItemTitleMax) return $"Item titles must be at most {ChecklistLimits.ItemTitleMax} characters.";
        if (item.Description.Length > ChecklistLimits.ItemDescriptionMax) return $"Item descriptions must be at most {ChecklistLimits.ItemDescriptionMax} characters.";
        if (item.TeamLabel.Length > ChecklistLimits.LabelMax || item.TimingLabel.Length > ChecklistLimits.LabelMax)
            return $"Labels must be at most {ChecklistLimits.LabelMax} characters.";
        if (item.LinkLabel.Length > ChecklistLimits.LabelMax * 2) return "Link labels must be at most 120 characters.";
        if (item.LinkUrl.Length > 0)
        {
            if (item.LinkUrl.Length > ChecklistLimits.LinkMax) return "Links must be at most 2000 characters.";
            if (!IsSafeHttpUrl(item.LinkUrl)) return $"Link '{Truncate(item.LinkUrl, 40)}' must be an absolute http(s) URL.";
        }
        return null;
    }

    /// Only absolute http/https links are accepted — the UI renders them as
    /// clickable anchors, so javascript:/data: schemes are refused at the
    /// boundary rather than sanitized at render.
    public static bool IsSafeHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public sealed record ChecklistTemplateSummary(
    Guid Id,
    string Name,
    string Description,
    bool IsActive,
    bool BlockClose,
    IReadOnlyList<Guid> QueueIds,
    int ItemCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record ChecklistTemplateDetail(
    Guid Id,
    string Name,
    string Description,
    bool IsActive,
    bool BlockClose,
    IReadOnlyList<Guid> QueueIds,
    ChecklistTemplateDefinition Definition,
    int ItemCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record ChecklistTemplateInput(
    string Name,
    string Description,
    bool IsActive,
    bool BlockClose,
    IReadOnlyList<Guid> QueueIds,
    ChecklistTemplateDefinition Definition);
