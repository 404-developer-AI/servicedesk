using System.Text.Json.Nodes;
using Servicedesk.Infrastructure.Forms;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.61 — the form field extensions for the intervention flow: several
/// agents with an organizer, an address, invitees (contacts + free e-mail),
/// attachments, the conditional "show when" rule and the Outlook appointment
/// mapping. Pure — no database.
public sealed class FormFieldTypesV061Tests
{
    private static readonly FormRuntimeSettings Limits = new(
        Enabled: true, MaxFieldsPerTemplate: 60, MaxArticleRows: 3, MaxArticleQuantity: 1000,
        LongTextMaxLength: 200, MaxPerTicket: 50);

    private static FormField F(string type, string label, bool required = false) => new()
    {
        Id = Guid.NewGuid(), Type = type, Label = label, Required = required,
    };

    private static JsonObject A(params (FormField Field, JsonNode? Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (f, v) in values) o[f.Id.ToString()] = v;
        return o;
    }

    private static FormDefinition Def(params FormField[] fields)
    {
        var def = new FormDefinition { Fields = fields.ToList() };
        Assert.Null(FormTemplateValidator.ValidateAndNormalize(def, 60));
        return def;
    }

    [Fact]
    public void Multiple_agents_keep_order_and_an_organizer_that_must_be_one_of_them()
    {
        var techs = F(FormFieldType.Agent, "Technicians");
        techs.Multiple = true;
        var def = Def(techs);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var parsed = FormAnswerValidator.Parse(def, A((techs, new JsonObject
        {
            ["ids"] = new JsonArray(a.ToString(), b.ToString(), a.ToString()),
            ["organizerId"] = b.ToString(),
        })), Limits);
        var v = Assert.IsType<FormAnswerValidator.AgentsValue>(parsed.Values[techs.Id]);
        Assert.Equal(new[] { a, b }, v.Ids);
        Assert.Equal(b, v.OrganizerId);
        Assert.Equal(new[] { a, b }.ToHashSet(), parsed.References.AgentIds);

        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((techs, new JsonObject
        {
            ["ids"] = new JsonArray(a.ToString()),
            ["organizerId"] = Guid.NewGuid().ToString(),
        })), Limits));

        var built = FormAnswerValidator.Build(def, parsed,
            new FormResolvedReferences(new Dictionary<Guid, string> { [a] = "Sam Example", [b] = "Alex Example" },
                new Dictionary<Guid, (string, string)>(), new Dictionary<Guid, (string, string)>()));
        Assert.Equal("Sam Example, Alex Example (organizer)",
            FormRenderer.DisplayValue(techs, built[techs.Id.ToString()], _ => ""));
    }

    [Fact]
    public void Multiple_is_dropped_on_non_agent_fields()
    {
        var text = F(FormFieldType.ShortText, "Title");
        text.Multiple = true;
        Def(text);
        Assert.Null(text.Multiple);
    }

    [Fact]
    public void Invitees_accept_contacts_and_valid_free_addresses_only()
    {
        var who = F(FormFieldType.Invitees, "Invite");
        var def = Def(who);
        var contact = Guid.NewGuid();
        var parsed = FormAnswerValidator.Parse(def, A((who, new JsonArray(
            new JsonObject { ["contactId"] = contact.ToString() },
            new JsonObject { ["email"] = "it@example.test", ["name"] = "IT desk" },
            new JsonObject { ["email"] = "IT@example.test" }))), Limits);
        var v = Assert.IsType<FormAnswerValidator.InviteesValue>(parsed.Values[who.Id]);
        Assert.Equal(2, v.People.Count); // duplicate address (case-insensitive) dropped
        Assert.Contains(contact, parsed.References.InviteeContactIds);

        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def,
            A((who, new JsonArray(new JsonObject { ["email"] = "not an address" }))), Limits));
    }

    [Fact]
    public void A_field_hidden_by_its_checkbox_is_not_required_and_its_answer_is_dropped()
    {
        var teams = F(FormFieldType.Checkbox, "Teams meeting");
        var invite = F(FormFieldType.Invitees, "Invite", required: true);
        invite.ShowWhen = teams.Id;
        var def = Def(teams, invite);

        var off = FormAnswerValidator.Parse(def, A((teams, false),
            (invite, new JsonArray(new JsonObject { ["email"] = "it@example.test" }))), Limits);
        Assert.False(off.Values.ContainsKey(invite.Id));

        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((teams, true)), Limits));
    }

    [Fact]
    public void A_field_shown_while_unchecked_is_hidden_once_its_checkbox_is_ticked()
    {
        var delivered = F(FormFieldType.Checkbox, "Delivered");
        var reason = F(FormFieldType.ShortText, "Reason not delivered", required: true);
        reason.ShowWhen = delivered.Id;
        reason.ShowWhenUnchecked = true;
        var def = Def(delivered, reason);

        // Ticked: hidden, not required, answer dropped.
        var on = FormAnswerValidator.Parse(def, A((delivered, true), (reason, "Out of stock")), Limits);
        Assert.False(on.Values.ContainsKey(reason.Id));

        // Not ticked, or never answered: shown and required.
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((delivered, false)), Limits));
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, new JsonObject(), Limits));
        var off = FormAnswerValidator.Parse(def, A((delivered, false), (reason, "Out of stock")), Limits);
        Assert.True(off.Values.ContainsKey(reason.Id));
    }

    [Fact]
    public void Show_when_unchecked_without_a_checkbox_is_cleared()
    {
        var text = F(FormFieldType.ShortText, "Note");
        text.ShowWhenUnchecked = true;
        Assert.Null(FormTemplateValidator.ValidateAndNormalize(new FormDefinition { Fields = { text } }, 60));
        Assert.Null(text.ShowWhenUnchecked);
    }

    [Fact]
    public void Show_when_must_point_to_a_checkbox_above_the_field()
    {
        var text = F(FormFieldType.ShortText, "Note");
        var later = F(FormFieldType.Checkbox, "Later");
        text.ShowWhen = later.Id;
        Assert.NotNull(FormTemplateValidator.ValidateAndNormalize(new FormDefinition { Fields = { text, later } }, 60));

        var notCheckbox = F(FormFieldType.ShortText, "Not a checkbox");
        var dependent = F(FormFieldType.ShortText, "Dependent");
        dependent.ShowWhen = notCheckbox.Id;
        Assert.NotNull(FormTemplateValidator.ValidateAndNormalize(new FormDefinition { Fields = { notCheckbox, dependent } }, 60));
    }

    [Fact]
    public void Address_and_attachments_parse_and_collect_references()
    {
        var addr = F(FormFieldType.Address, "Address");
        var files = F(FormFieldType.Attachments, "Files");
        var def = Def(addr, files);
        var file = Guid.NewGuid();
        var parsed = FormAnswerValidator.Parse(def, A(
            (addr, "Main street 1\r\n3500 Hasselt"),
            (files, new JsonArray(file.ToString(), new JsonObject { ["id"] = file.ToString() }))), Limits);
        Assert.Equal("Main street 1\n3500 Hasselt", Assert.IsType<FormAnswerValidator.TextValue>(parsed.Values[addr.Id]).Value);
        Assert.Equal(new[] { file }, Assert.IsType<FormAnswerValidator.AttachmentsValue>(parsed.Values[files.Id]).Ids);
        Assert.Contains(file, parsed.References.AttachmentIds);
    }

    [Fact]
    public void Outlook_mapping_checks_field_types_and_required_parts()
    {
        var title = F(FormFieldType.ShortText, "Title");
        var from = F(FormFieldType.DateTime, "From");
        var to = F(FormFieldType.DateTime, "To");
        var techs = F(FormFieldType.Agent, "Technicians");
        var def = Def(title, from, to, techs);

        var ok = new FormOutlookMapping
        {
            Enabled = true, TitleFieldId = title.Id, StartFieldId = from.Id, EndFieldId = to.Id, TechniciansFieldId = techs.Id,
        };
        Assert.Null(ok.Validate(def));

        Assert.NotNull(new FormOutlookMapping { Enabled = true, TitleFieldId = title.Id, StartFieldId = from.Id, TechniciansFieldId = techs.Id }.Validate(def));
        Assert.NotNull(new FormOutlookMapping { TitleFieldId = from.Id }.Validate(def)); // wrong type even when off
        Assert.NotNull(new FormOutlookMapping
        {
            Enabled = true, TitleFieldId = title.Id, StartFieldId = from.Id, EndFieldId = from.Id, TechniciansFieldId = techs.Id,
        }.Validate(def)); // one field, two properties
    }

    [Fact]
    public void A_duration_that_counts_from_a_start_gives_the_end_and_can_end_an_appointment()
    {
        var title = F(FormFieldType.ShortText, "Title");
        var from = F(FormFieldType.DateTime, "Intervention");
        var duration = F(FormFieldType.Duration, "Duration");
        duration.DurationFrom = from.Id;
        var techs = F(FormFieldType.Agent, "Technicians");
        var def = Def(title, from, duration, techs);
        Assert.Null(FormTemplateValidator.ValidateAndNormalize(def, 60));

        var answers = new JsonObject { [from.Id.ToString()] = "2030-07-02T22:30:00Z", [duration.Id.ToString()] = 150 };
        Assert.Equal(new DateTime(2030, 7, 3, 1, 0, 0, DateTimeKind.Utc), FormRenderer.DurationEndUtc(def, duration, answers));

        var mapping = new FormOutlookMapping
        {
            Enabled = true, TitleFieldId = title.Id, StartFieldId = from.Id, EndFieldId = duration.Id, TechniciansFieldId = techs.Id,
        };
        Assert.Null(mapping.Validate(def));
        var (start, end) = mapping.ReadTimes(def, answers);
        Assert.Equal(new DateTime(2030, 7, 2, 22, 30, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2030, 7, 3, 1, 0, 0, DateTimeKind.Utc), end);

        // No duration yet: no end, so no appointment.
        Assert.Null(mapping.ReadTimes(def, new JsonObject { [from.Id.ToString()] = "2030-07-02T22:30:00Z" }).End);

        // The note shows the end next to the duration.
        var html = FormRenderer.RenderNoteHtml(Guid.NewGuid(), "Plan", 1, def, answers, null, null,
            dt => dt.UtcDateTime.ToString("dd/MM HH:mm", global::System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("2:30 (until 03/07 01:00)", html);
    }

    [Fact]
    public void Counts_from_must_be_a_date_and_time_above_and_the_outlook_end_must_count_from_the_start()
    {
        var duration = F(FormFieldType.Duration, "Duration");
        var later = F(FormFieldType.DateTime, "Later");
        duration.DurationFrom = later.Id;
        Assert.NotNull(FormTemplateValidator.ValidateAndNormalize(new FormDefinition { Fields = { duration, later } }, 60));

        var text = F(FormFieldType.ShortText, "Text");
        text.DurationFrom = Guid.NewGuid();
        Assert.Null(FormTemplateValidator.ValidateAndNormalize(new FormDefinition { Fields = { text } }, 60));
        Assert.Null(text.DurationFrom); // only a Duration keeps it

        var title = F(FormFieldType.ShortText, "Title");
        var start = F(FormFieldType.DateTime, "Start");
        var other = F(FormFieldType.DateTime, "Other");
        var plain = F(FormFieldType.Duration, "Plain");
        var techs = F(FormFieldType.Agent, "Technicians");
        plain.DurationFrom = other.Id;
        Assert.NotNull(new FormOutlookMapping
        {
            Enabled = true, TitleFieldId = title.Id, StartFieldId = start.Id, EndFieldId = plain.Id, TechniciansFieldId = techs.Id,
        }.Validate(Def(title, start, other, plain, techs)));
    }

    // ---- v0.1.61: where a template is offered (picker / header button) ----

    private static FormTemplateDetail Tpl(bool inPicker, Guid[] queues, string? label, Guid[] buttonQueues, bool active = true)
        => new(Guid.NewGuid(), "T", "", active, queues, new FormDefinition(), 0, DateTime.UtcNow, DateTime.UtcNow,
            HeaderButtonLabel: label, HeaderButtonQueueIds: buttonQueues, InPicker: inPicker);

    [Fact]
    public void Picker_scope_empty_means_every_queue()
    {
        var q = Guid.NewGuid();
        Assert.True(Tpl(true, [], null, []).UsableIn(q));
        Assert.False(Tpl(true, [Guid.NewGuid()], null, []).UsableIn(q));
        Assert.False(Tpl(true, [], null, [], active: false).UsableIn(q));
    }

    [Fact]
    public void A_form_off_the_picker_is_usable_only_through_its_button_queues()
    {
        var sd = Guid.NewGuid();
        var other = Guid.NewGuid();
        var t = Tpl(false, [], "Request intervention", [sd]);
        Assert.True(t.UsableIn(sd));
        Assert.False(t.UsableIn(other));
        Assert.False(FormTemplateScope.InPicker(false, [], sd));
    }

    [Fact]
    public void A_button_needs_a_label()
    {
        var q = Guid.NewGuid();
        Assert.False(FormTemplateScope.Button(null, [q], q));
        Assert.False(Tpl(false, [], null, [q]).UsableIn(q));
        Assert.True(FormTemplateScope.Button("Go", [q], q));
    }
}
