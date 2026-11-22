using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Servicedesk.Infrastructure.Forms;

/// v0.1.61 — the "Outlook appointment" block of a form template: which field
/// feeds which property of the appointment(s) a saved form creates in the
/// technicians' calendars. One field per property by construction, so two
/// titles can never exist. Stored as JSONB on <c>form_templates.outlook_mapping</c>
/// and copied into the filled form's snapshot is NOT needed: the mapping is
/// read from the live template at sync time (a template edit re-maps future
/// syncs only; field ids are stable).
public sealed class FormOutlookMapping
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// ShortText — the appointment subject (token defaults allowed).
    [JsonPropertyName("titleFieldId")]
    public Guid? TitleFieldId { get; set; }

    /// DateTime — start (required when enabled).
    [JsonPropertyName("startFieldId")]
    public Guid? StartFieldId { get; set; }

    /// DateTime — end (required when enabled; must be after start). Or a
    /// Duration that counts from the start field: end = start + duration.
    [JsonPropertyName("endFieldId")]
    public Guid? EndFieldId { get; set; }

    /// Agent (multiple allowed) — whose calendars get the appointment
    /// (required when enabled).
    [JsonPropertyName("techniciansFieldId")]
    public Guid? TechniciansFieldId { get; set; }

    /// Address — the location; ignored for a Teams meeting.
    [JsonPropertyName("addressFieldId")]
    public Guid? AddressFieldId { get; set; }

    /// Checkbox — ticked = Teams meeting (one event, organizer, invitees).
    [JsonPropertyName("teamsFieldId")]
    public Guid? TeamsFieldId { get; set; }

    /// Invitees — external / extra attendees (Teams meetings only).
    [JsonPropertyName("inviteesFieldId")]
    public Guid? InviteesFieldId { get; set; }

    /// LongText — the message for attendees, used as the body instead of the
    /// internal note whenever there are external attendees.
    [JsonPropertyName("messageFieldId")]
    public Guid? MessageFieldId { get; set; }

    /// Attachments — files added to the appointment.
    [JsonPropertyName("attachmentsFieldId")]
    public Guid? AttachmentsFieldId { get; set; }

    /// Checkbox — ticked = attachments also go to external attendees.
    [JsonPropertyName("shareAttachmentsFieldId")]
    public Guid? ShareAttachmentsFieldId { get; set; }

    public static FormOutlookMapping? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FormOutlookMapping>(json, FormDefinition.JsonOptions); }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, FormDefinition.JsonOptions);

    /// The appointment's start and end from the answers. The end is a DateTime
    /// field, or start + duration for a Duration end field. One rule for the
    /// save check and the calendar sync.
    public (DateTime? Start, DateTime? End) ReadTimes(FormDefinition def, JsonObject answers)
    {
        FormField? Field(Guid? id) => id is { } g ? def.Fields.FirstOrDefault(f => f.Id == g) : null;
        var startField = Field(StartFieldId);
        var start = startField is null ? null : FormRenderer.ReadUtc(answers[startField.Id.ToString()]);
        var endField = Field(EndFieldId);
        DateTime? end = endField switch
        {
            null => null,
            { Type: FormFieldType.Duration } d => d.DurationFrom == StartFieldId ? FormRenderer.DurationEndUtc(def, d, answers) : null,
            _ => FormRenderer.ReadUtc(answers[endField.Id.ToString()]),
        };
        return (start, end);
    }

    /// Every mapped field must exist on the definition with the right type.
    /// A disabled mapping is kept as-is (so switching it off and on again does
    /// not lose the choices) but only checked for type sanity. Returns an
    /// English error or null.
    public string? Validate(FormDefinition def)
    {
        var byId = def.Fields.ToDictionary(f => f.Id);
        string? Check(Guid? id, string what, params string[] types)
        {
            if (id is not { } g) return null;
            if (!byId.TryGetValue(g, out var f)) return $"Outlook appointment: the {what} field no longer exists on this form.";
            if (!types.Contains(f.Type)) return $"Outlook appointment: the {what} must be a {string.Join(" or ", types)} field.";
            return null;
        }

        var err = Check(TitleFieldId, "title", FormFieldType.ShortText)
                  ?? Check(StartFieldId, "start", FormFieldType.DateTime)
                  ?? Check(EndFieldId, "end", FormFieldType.DateTime, FormFieldType.Duration)
                  ?? Check(TechniciansFieldId, "technicians", FormFieldType.Agent)
                  ?? Check(AddressFieldId, "address", FormFieldType.Address)
                  ?? Check(TeamsFieldId, "Teams meeting", FormFieldType.Checkbox)
                  ?? Check(InviteesFieldId, "invitees", FormFieldType.Invitees)
                  ?? Check(MessageFieldId, "message for attendees", FormFieldType.LongText)
                  ?? Check(AttachmentsFieldId, "attachments", FormFieldType.Attachments)
                  ?? Check(ShareAttachmentsFieldId, "share-attachments", FormFieldType.Checkbox);
        if (err is not null) return err;

        var mapped = new[] { TitleFieldId, StartFieldId, EndFieldId, TechniciansFieldId, AddressFieldId,
                             TeamsFieldId, InviteesFieldId, MessageFieldId, AttachmentsFieldId, ShareAttachmentsFieldId }
            .Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (mapped.Count != mapped.Distinct().Count())
            return "Outlook appointment: a field can feed only one appointment property.";

        if (!Enabled) return null;
        if (StartFieldId is null || EndFieldId is null)
            return "Outlook appointment: choose the start and end fields.";
        if (TechniciansFieldId is null)
            return "Outlook appointment: choose the technicians field.";
        if (TitleFieldId is null)
            return "Outlook appointment: choose the title field.";
        if (StartFieldId == EndFieldId)
            return "Outlook appointment: start and end must be two different fields.";
        if (byId[EndFieldId.Value] is { Type: FormFieldType.Duration } dur && dur.DurationFrom != StartFieldId)
            return "Outlook appointment: the duration used as the end must count from the start field.";
        if (InviteesFieldId is not null && TeamsFieldId is null)
            return "Outlook appointment: invitees need a Teams meeting checkbox.";
        return null;
    }
}
