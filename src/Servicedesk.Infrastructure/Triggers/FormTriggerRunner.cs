using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Triggers;

/// Runs the action-trigger passes for forms. Both passes carry the form on
/// their change set, so only triggers with a <c>form.*</c> condition take part
/// (see <see cref="TriggerChangeSet.RunsTriggerReferencing"/>), and what
/// happens on the ticket — a note, a status, queue, owner or pending-till
/// change, a bell notification — is entirely the admin's trigger configuration.
/// <list type="bullet">
/// <item>v0.1.54 — a back-office step (submitted / handled / reopened).</item>
/// <item>v0.1.58 — a form save (created / updated) of any form.</item>
/// </list>
/// Both expose the form's keyed fields (<see cref="FormField.TriggerKey"/>) to
/// conditions, actions and template variables.
public sealed class FormTriggerRunner : IFormTriggerRunner
{
    private readonly ITriggerService _triggers;
    private readonly NpgsqlDataSource _ds;
    private readonly ITicketFormRepository _forms;
    private readonly ISettingsService _settings;

    public FormTriggerRunner(ITriggerService triggers, NpgsqlDataSource ds, ITicketFormRepository forms, ISettingsService settings)
    {
        _triggers = triggers;
        _ds = ds;
        _forms = forms;
        _settings = settings;
    }

    public async Task RunAsync(BackofficeFormEvent evt, CancellationToken ct)
    {
        // The form's latest note on the timeline — what a notify_agent bell
        // links to (the pass itself has no triggering article).
        long? anchor;
        await using (var conn = await _ds.OpenConnectionAsync(ct))
        {
            anchor = await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT event_id FROM ticket_form_versions
                WHERE form_id = @formId AND event_id IS NOT NULL
                ORDER BY version DESC LIMIT 1
                """, new { formId = evt.FormId }, cancellationToken: ct));
        }

        var form = await _forms.GetAsync(evt.FormId, ct);
        var fields = form is null
            ? new Dictionary<string, TriggerFormField>(StringComparer.OrdinalIgnoreCase)
            : FormTriggerFields.Build(form.Snapshot, form.Answers, await FormatterAsync(ct));

        var formEvent = new TriggerFormEvent(
            evt.Kind, evt.FormId, evt.TemplateId, evt.FormName,
            evt.SubmittedByUserId, evt.SubmittedByName, evt.ActorUserId,
            evt.HandledByName, evt.HandledNote, anchor)
        {
            Fields = fields,
        };

        await _triggers.EvaluateAsync(
            evt.TicketId, ticketEventId: null, TriggerActivatorKind.Action,
            TriggerChangeSet.ForForm(formEvent), ct);
    }

    public async Task RunSavedAsync(FormSavedEvent evt, CancellationToken ct)
    {
        var format = await FormatterAsync(ct);
        var form = await _forms.GetAsync(evt.FormId, ct);
        var formEvent = new TriggerFormEvent(
            string.Empty, evt.FormId, evt.TemplateId, evt.FormName,
            form?.Summary.CreatedByUserId, form?.Summary.CreatedByName, evt.ActorUserId,
            HandledByName: null, HandledNote: string.Empty, AnchorEventId: evt.EventId)
        {
            SaveEvent = evt.Kind,
            Fields = FormTriggerFields.Build(evt.Snapshot, evt.Answers, format),
            ChangedKeys = FormTriggerFields.ChangedKeys(evt.Snapshot, evt.Previous, evt.Answers),
        };

        await _triggers.EvaluateAsync(
            evt.TicketId, ticketEventId: null, TriggerActivatorKind.Action,
            TriggerChangeSet.ForForm(formEvent), ct);
    }

    private async Task<Func<DateTimeOffset, string>> FormatterAsync(CancellationToken ct)
    {
        var tz = await HealthTimeFormat.ResolveDisplayZoneAsync(_settings, ct);
        return dt => HealthTimeFormat.Local(dt, tz);
    }
}

/// Pure mapping from a form's snapshot + answers to what a trigger sees.
public static class FormTriggerFields
{
    public static IReadOnlyDictionary<string, TriggerFormField> Build(
        FormDefinition def, JsonObject answers, Func<DateTimeOffset, string> formatDateTime)
    {
        var map = new Dictionary<string, TriggerFormField>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in def.Fields)
        {
            if (f.TriggerKey is null || !FormFieldType.IsInput(f.Type)) continue;
            var v = answers[f.Id.ToString()];
            var display = FormRenderer.DisplayValue(f, v, formatDateTime);
            // An unanswered checkbox (hidden by "show when", or an older
            // answer set) is not ticked: conditions compare "No", never empty.
            if (f.Type == FormFieldType.Checkbox && display.Length == 0) display = "No";
            Guid? refId = null;
            DateTime? utc = null;
            if (f.Type is FormFieldType.Agent or FormFieldType.Contact or FormFieldType.Queue
                && v is JsonObject o && o["id"] is JsonValue idv
                && idv.GetValueKind() == System.Text.Json.JsonValueKind.String
                && Guid.TryParse(idv.GetValue<string>(), out var id))
            {
                refId = id;
            }
            // v0.1.61 — a multi-agent field: the organizer, else the first
            // agent, is the one a "set owner from form field" action takes.
            if (f.Type == FormFieldType.Agent && v is JsonArray agents)
            {
                var pick = agents.OfType<JsonObject>().FirstOrDefault(a =>
                               a["organizer"] is JsonValue ov && ov.GetValueKind() == System.Text.Json.JsonValueKind.True)
                           ?? agents.OfType<JsonObject>().FirstOrDefault();
                if (pick?["id"] is JsonValue pv && pv.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(pv.GetValue<string>(), out var pid))
                    refId = pid;
            }
            if (f.Type == FormFieldType.DateTime && v is JsonValue dv
                && dv.GetValueKind() == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(dv.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
            {
                utc = dto.UtcDateTime;
            }
            map[f.TriggerKey] = new TriggerFormField(f.Type, f.Label, display, refId, utc);
        }
        return map;
    }

    /// Trigger keys whose answer differs between <paramref name="previous"/>
    /// (null on create: every filled key counts) and <paramref name="answers"/>.
    public static IReadOnlySet<string> ChangedKeys(FormDefinition def, JsonObject? previous, JsonObject answers)
    {
        var changedIds = FormRenderer.ChangedFields(def, previous, answers).ToHashSet();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in def.Fields)
        {
            if (f.TriggerKey is not null && changedIds.Contains(f.Id)) keys.Add(f.TriggerKey);
        }
        return keys;
    }
}
