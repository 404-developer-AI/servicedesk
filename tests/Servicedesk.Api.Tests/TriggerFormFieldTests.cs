using System.Text.Json;
using System.Text.Json.Nodes;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.58 — form saves as trigger passes, keyed form fields as trigger
/// conditions and as the source of set_owner / set_queue / set_pending_till.
public sealed class TriggerFormFieldTests
{
    private static readonly TriggerConditionMatcher Matcher = new();
    private static readonly Guid TemplateId = Guid.Parse("6f1d2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b");
    private static readonly Guid TechnicianId = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly Guid FieldServiceId = Guid.Parse("9e8d7c6b-5a4f-4e3d-8c2b-1a0f9e8d7c6b");

    private static TriggerFormEvent SavePass(string kind = "created", IReadOnlySet<string>? changed = null,
        IReadOnlyDictionary<string, TriggerFormField>? fields = null) =>
        new(string.Empty, Guid.NewGuid(), TemplateId, "Plan intervention", Guid.NewGuid(), "Alex Example",
            Guid.NewGuid(), null, string.Empty, AnchorEventId: 3)
        {
            SaveEvent = kind,
            Fields = fields ?? Fields(),
            ChangedKeys = changed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "technician", "planned_at" },
        };

    private static IReadOnlyDictionary<string, TriggerFormField> Fields(bool technicianEmpty = false) =>
        new Dictionary<string, TriggerFormField>(StringComparer.OrdinalIgnoreCase)
        {
            ["technician"] = technicianEmpty
                ? new TriggerFormField(FormFieldType.Agent, "Technician", string.Empty, null, null)
                : new TriggerFormField(FormFieldType.Agent, "Technician", "Sam Example", TechnicianId, null),
            ["planned_at"] = new TriggerFormField(FormFieldType.DateTime, "Planned at", "01/12/2030 09:00", null,
                new DateTime(2030, 12, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["target_queue"] = new TriggerFormField(FormFieldType.Queue, "Queue", "Field Service", FieldServiceId, null),
        };

    private static IReadOnlySet<string> Refs(string conditions)
    {
        using var doc = JsonDocument.Parse(conditions);
        return Matcher.ReferencedFields(doc.RootElement);
    }

    private static string Cond(params string[] leaves) => $$"""{ "op": "AND", "items": [ {{string.Join(",", leaves)}} ] }""";
    private const string SavedCreated = """{ "field": "form.save_event", "operator": "is", "value": "created" }""";
    private const string TemplateLeaf = """{ "field": "form.template.id", "operator": "is", "value": "6f1d2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b" }""";
    private const string TechnicianSet = """{ "field": "form.field.technician", "operator": "is_set" }""";

    // ---- pass kinds ----------------------------------------------------

    [Fact]
    public void Save_triggers_run_only_on_save_passes_and_back_office_triggers_only_on_back_office_passes()
    {
        var save = TriggerChangeSet.ForForm(SavePass());
        var bo = TriggerChangeSet.ForForm(SavePass() with { SaveEvent = null, Event = "submitted" });
        var saveTrigger = Refs(Cond(SavedCreated, TemplateLeaf));
        var templateOnly = Refs(Cond(TemplateLeaf));

        Assert.True(save.RunsTriggerReferencing(saveTrigger));
        Assert.False(bo.RunsTriggerReferencing(saveTrigger));
        // A trigger that only names a template stays a back-office trigger
        // (v0.1.54 behaviour), so one save never fires it twice.
        Assert.False(save.RunsTriggerReferencing(templateOnly));
        Assert.True(bo.RunsTriggerReferencing(templateOnly));
        // Ticket triggers never run on either.
        Assert.False(save.RunsTriggerReferencing(new HashSet<string>()));
        Assert.False(save.RunsTriggerReferencing(Refs(Cond("""{ "field": "ticket.subject", "operator": "contains", "value": "x" }"""))));
    }

    [Fact]
    public void A_save_pass_marks_the_save_event_template_and_changed_keys_as_changed()
    {
        var changed = TriggerChangeSet.ForForm(SavePass(changed: new HashSet<string> { "planned_at" })).ChangedFields;
        Assert.Contains(TriggerFieldKeys.FormSaveEvent, changed);
        Assert.Contains(TriggerFieldKeys.FormTemplateId, changed);
        Assert.Contains("form.field.planned_at", changed);
        Assert.DoesNotContain("form.field.technician", changed);
        Assert.DoesNotContain(TriggerFieldKeys.FormBackofficeEvent, changed);
    }

    // ---- conditions ----------------------------------------------------

    [Theory]
    [InlineData("""{ "field": "form.save_event", "operator": "is", "value": "created" }""", "created", true)]
    [InlineData("""{ "field": "form.save_event", "operator": "is", "value": "created" }""", "updated", false)]
    [InlineData("""{ "field": "form.field.technician", "operator": "is_set" }""", "created", true)]
    [InlineData("""{ "field": "form.field.technician", "operator": "is_not_set" }""", "created", false)]
    [InlineData("""{ "field": "form.field.unknown_key", "operator": "is_not_set" }""", "created", true)]
    [InlineData("""{ "field": "form.field.technician", "operator": "is", "value": "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d" }""", "created", true)]
    [InlineData("""{ "field": "form.field.target_queue", "operator": "is", "value": "Field Service" }""", "created", false)]
    [InlineData("""{ "field": "form.field.planned_at", "operator": "has_changed" }""", "updated", true)]
    [InlineData("""{ "field": "form.field.target_queue", "operator": "has_changed" }""", "updated", false)]
    public void Form_conditions_resolve_from_the_save_pass(string leaf, string kind, bool expected)
        => Assert.Equal(expected, Eval(Cond(leaf), TriggerChangeSet.ForForm(SavePass(kind))));

    [Fact]
    public void An_empty_field_is_not_set()
    {
        var cs = TriggerChangeSet.ForForm(SavePass(fields: Fields(technicianEmpty: true)));
        Assert.False(Eval(Cond(TechnicianSet), cs));
    }

    [Fact]
    public void Form_field_conditions_never_match_on_a_ticket_pass()
        => Assert.False(Eval(Cond(TechnicianSet), TriggerChangeSet.ArticleOnly()));

    // ---- validator -----------------------------------------------------

    private static ValidationResult Validate(string conditions, string actions)
        => TriggerValidator.Validate("Plan intervention", "action", "selective", conditions, actions, null, null);

    [Fact]
    public void The_intervention_trigger_validates()
    {
        var result = Validate(Cond(SavedCreated, TemplateLeaf, TechnicianSet), """
            [ { "kind": "set_queue", "form_field": "target_queue" },
              { "kind": "set_owner", "form_field": "technician" },
              { "kind": "set_status", "status_id": "3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e6f" },
              { "kind": "set_pending_till", "form_field": "planned_at" } ]
            """);
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void A_form_field_condition_needs_a_pass_condition()
    {
        var result = Validate(Cond(TemplateLeaf, TechnicianSet), "[]");
        Assert.False(result.IsValid);
        Assert.Contains("Form saved", result.Error);
    }

    [Fact]
    public void A_form_field_action_needs_a_pass_condition()
    {
        var result = Validate(Cond("""{ "field": "ticket.subject", "operator": "contains", "value": "x" }"""),
            """[ { "kind": "set_owner", "form_field": "technician" } ]""");
        Assert.False(result.IsValid);
        Assert.Contains("Form saved", result.Error);
    }

    [Fact]
    public void A_back_office_trigger_may_read_form_fields()
        => Assert.True(Validate(Cond("""{ "field": "form.backoffice_event", "operator": "is", "value": "handled" }"""),
            """[ { "kind": "set_owner", "form_field": "technician" } ]""").IsValid);

    [Theory]
    [InlineData("""[ { "kind": "set_owner", "form_field": "Technician!" } ]""")]
    [InlineData("""[ { "kind": "set_queue", "form_field": "" } ]""")]
    [InlineData("""[ { "kind": "set_pending_till", "form_field": 5 } ]""")]
    public void Malformed_form_field_references_are_rejected(string actions)
        => Assert.False(Validate(Cond(SavedCreated), actions).IsValid);

    [Fact]
    public void A_malformed_form_field_condition_key_is_rejected()
        => Assert.False(Validate(Cond(SavedCreated, """{ "field": "form.field.Bad Key", "operator": "is_set" }"""), "[]").IsValid);

    // ---- action source -------------------------------------------------

    private static FormFieldActionSource.Resolution Resolve(string action, TriggerChangeSet cs, string type)
    {
        using var doc = JsonDocument.Parse(action);
        var ctx = new TriggerEvaluationContext(Guid.NewGuid(), TriggerBackofficeFormTests.MakeTicket(), null, cs, DateTime.UtcNow);
        return FormFieldActionSource.Resolve(doc.RootElement, ctx, type);
    }

    [Fact]
    public void Action_source_resolves_the_keyed_field()
    {
        var r = Resolve("""{ "form_field": "technician" }""", TriggerChangeSet.ForForm(SavePass()), FormFieldType.Agent);
        Assert.Null(r.Error);
        Assert.Equal(TechnicianId, r.Field!.RefId);
    }

    [Fact]
    public void Action_source_fails_outside_a_form_pass_on_unknown_keys_and_wrong_types()
    {
        Assert.NotNull(Resolve("""{ "form_field": "technician" }""", TriggerChangeSet.ArticleOnly(), FormFieldType.Agent).Error);
        Assert.NotNull(Resolve("""{ "form_field": "nope" }""", TriggerChangeSet.ForForm(SavePass()), FormFieldType.Agent).Error);
        Assert.NotNull(Resolve("""{ "form_field": "planned_at" }""", TriggerChangeSet.ForForm(SavePass()), FormFieldType.Agent).Error);
    }

    [Fact]
    public void Action_source_reports_an_empty_field_as_empty_not_as_an_error()
    {
        var r = Resolve("""{ "form_field": "technician" }""",
            TriggerChangeSet.ForForm(SavePass(fields: Fields(technicianEmpty: true))), FormFieldType.Agent);
        Assert.Null(r.Error);
        Assert.Null(r.Field);
        Assert.NotNull(r.EmptyReason);
    }

    // ---- form side -----------------------------------------------------

    private static FormDefinition Definition() => new()
    {
        Fields =
        {
            new FormField { Id = Guid.NewGuid(), Type = FormFieldType.Agent, Label = "Technician", TriggerKey = " Technician " },
            new FormField { Id = Guid.NewGuid(), Type = FormFieldType.Agent, Label = "Assistant" },
            new FormField { Id = Guid.NewGuid(), Type = FormFieldType.DateTime, Label = "Planned at", TriggerKey = "planned_at" },
            new FormField { Id = Guid.NewGuid(), Type = FormFieldType.Queue, Label = "Queue", TriggerKey = "target_queue" },
            new FormField { Id = Guid.NewGuid(), Type = FormFieldType.SectionHeader, Label = "Notes", TriggerKey = "notes" },
        },
    };

    [Fact]
    public void Template_validator_normalizes_trigger_keys_and_drops_them_on_headers()
    {
        var def = Definition();
        Assert.Null(FormTemplateValidator.ValidateAndNormalize(def, 60));
        Assert.Equal("technician", def.Fields[0].TriggerKey);
        Assert.Null(def.Fields[1].TriggerKey);
        Assert.Null(def.Fields[4].TriggerKey);
    }

    [Theory]
    [InlineData("planned_at", "used twice")]
    [InlineData("9lives", "trigger key")]
    [InlineData("has-dash", "trigger key")]
    public void Template_validator_rejects_bad_or_duplicate_trigger_keys(string key, string expected)
    {
        var def = Definition();
        def.Fields[1].TriggerKey = key;
        Assert.Contains(expected, FormTemplateValidator.ValidateAndNormalize(def, 60));
    }

    private static readonly FormRuntimeSettings Limits = new(true, 60, 50, 100000, 4000, 50);

    [Fact]
    public void A_keyed_date_in_the_past_is_rejected_only_when_new_or_changed()
    {
        var def = Definition();
        FormTemplateValidator.ValidateAndNormalize(def, 60);
        var planned = def.Fields[2].Id.ToString();
        var now = new DateTime(2030, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var past = new JsonObject { [planned] = "2030-05-01T09:00:00Z" };
        var future = new JsonObject { [planned] = "2030-07-01T09:00:00Z" };

        Assert.Equal(def.Fields[2].Id, FormAnswerValidator.TriggerDateInPast(def, FormAnswerValidator.Parse(def, past, Limits), null, now)?.Id);
        Assert.Null(FormAnswerValidator.TriggerDateInPast(def, FormAnswerValidator.Parse(def, future, Limits), null, now));
        // Editing a form whose intervention already took place: the date did
        // not change, so the form can still be corrected.
        Assert.Null(FormAnswerValidator.TriggerDateInPast(def, FormAnswerValidator.Parse(def, past, Limits), past, now));
        // Moving it to another past date is a change and is refused.
        var otherPast = new JsonObject { [planned] = "2030-05-02T09:00:00Z" };
        Assert.NotNull(FormAnswerValidator.TriggerDateInPast(def, FormAnswerValidator.Parse(def, otherPast, Limits), past, now));
    }

    [Fact]
    public void An_unkeyed_date_in_the_past_is_fine()
    {
        var def = new FormDefinition { Fields = { new FormField { Id = Guid.NewGuid(), Type = FormFieldType.DateTime, Label = "Done at" } } };
        var answers = new JsonObject { [def.Fields[0].Id.ToString()] = "2001-01-01T09:00:00Z" };
        Assert.Null(FormAnswerValidator.TriggerDateInPast(def, FormAnswerValidator.Parse(def, answers, Limits), null, DateTime.UtcNow));
    }

    [Fact]
    public void Queue_answers_are_collected_as_references_and_stored_with_their_name()
    {
        var def = Definition();
        FormTemplateValidator.ValidateAndNormalize(def, 60);
        var queueField = def.Fields[3].Id.ToString();
        var parsed = FormAnswerValidator.Parse(def, new JsonObject { [queueField] = FieldServiceId.ToString() }, Limits);
        Assert.Contains(FieldServiceId, parsed.References.QueueIds);

        var built = FormAnswerValidator.Build(def, parsed, new FormResolvedReferences(
            new Dictionary<Guid, string>(), new Dictionary<Guid, (string, string)>(), new Dictionary<Guid, (string, string)>())
        {
            Queues = new Dictionary<Guid, string> { [FieldServiceId] = "Field Service" },
        });
        Assert.Equal("Field Service", built[queueField]!["name"]!.GetValue<string>());
        Assert.Equal("Field Service", FormRenderer.DisplayValue(def.Fields[3], built[queueField], _ => string.Empty));
    }

    private static Queue MakeQueue(Guid id, bool active = true)
        => new(id, "Field Service", "field-service", string.Empty, "#000000", "wrench", 0, active, false, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task A_queue_the_filler_cannot_access_is_rejected()
    {
        var other = Guid.NewGuid();
        var scope = new QueueAccessScope(false, new HashSet<Guid> { FieldServiceId });
        Task<Queue?> Load(Guid id) => Task.FromResult<Queue?>(MakeQueue(id));

        var ok = await TicketFormService.ResolveQueuesAsync(scope, new[] { FieldServiceId }, Load);
        Assert.Equal("Field Service", ok[FieldServiceId]);

        var ex = await Assert.ThrowsAsync<FormRejectedException>(() => TicketFormService.ResolveQueuesAsync(scope, new[] { other }, Load));
        Assert.Equal(FormRejectCode.Invalid, ex.Code);
    }

    [Fact]
    public async Task An_inactive_queue_is_rejected_even_for_an_admin()
    {
        await Assert.ThrowsAsync<FormRejectedException>(() => TicketFormService.ResolveQueuesAsync(
            QueueAccessScope.AdminScope, new[] { FieldServiceId }, id => Task.FromResult<Queue?>(MakeQueue(id, active: false))));
    }

    [Fact]
    public void Trigger_fields_expose_keyed_fields_only_and_changed_keys_follow_the_answers()
    {
        var def = Definition();
        FormTemplateValidator.ValidateAndNormalize(def, 60);
        var tech = def.Fields[0].Id.ToString();
        var assistant = def.Fields[1].Id.ToString();
        var planned = def.Fields[2].Id.ToString();
        var before = new JsonObject
        {
            [tech] = new JsonObject { ["id"] = TechnicianId.ToString(), ["name"] = "Sam Example" },
            [planned] = "2030-07-01T09:00:00Z",
        };
        var after = new JsonObject
        {
            [tech] = new JsonObject { ["id"] = TechnicianId.ToString(), ["name"] = "Sam Example" },
            [assistant] = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "Robin Example" },
            [planned] = "2030-07-02T09:00:00Z",
        };

        var fields = FormTriggerFields.Build(def, after, _ => "formatted");
        Assert.Equal(new[] { "planned_at", "target_queue", "technician" }, fields.Keys.OrderBy(k => k));
        Assert.Equal(TechnicianId, fields["technician"].RefId);
        Assert.Equal(new DateTime(2030, 7, 2, 9, 0, 0, DateTimeKind.Utc), fields["planned_at"].DateTimeUtc);
        Assert.True(fields["target_queue"].IsEmpty);

        // The unkeyed assistant changed too, but only keyed fields count.
        Assert.Equal(new[] { "planned_at" }, FormTriggerFields.ChangedKeys(def, before, after));
        Assert.Equal(new[] { "planned_at", "technician" }, FormTriggerFields.ChangedKeys(def, null, after).OrderBy(k => k));
    }

    private static bool Eval(string conditions, TriggerChangeSet cs)
    {
        var ctx = new TriggerEvaluationContext(Guid.NewGuid(), TriggerBackofficeFormTests.MakeTicket(), null, cs, DateTime.UtcNow);
        using var doc = JsonDocument.Parse(conditions);
        return Matcher.Matches(doc.RootElement, ctx);
    }
}
