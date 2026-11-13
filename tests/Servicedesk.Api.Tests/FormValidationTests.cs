using System.Text.Json.Nodes;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.52 — ticket forms: template normalization, server-side answer
/// validation against the frozen snapshot, and the note renderer (HTML
/// escaping + diff). All pure — no database.
public sealed class FormValidationTests
{
    private static readonly FormRuntimeSettings Limits = new(
        Enabled: true, MaxFieldsPerTemplate: 60, MaxArticleRows: 3, MaxArticleQuantity: 1000,
        LongTextMaxLength: 50, MaxPerTicket: 50);

    private static FormField F(string type, string label, bool required = false, params string[] options) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Label = label,
        Required = required,
        Options = options.Length == 0 ? null : options.Select((o, i) => new FormFieldOption { Id = $"o{i}", Label = o }).ToList(),
    };

    private static FormDefinition Def(params FormField[] fields)
    {
        var def = new FormDefinition { Fields = fields.ToList() };
        Assert.Null(FormTemplateValidator.ValidateAndNormalize(def, 60));
        return def;
    }

    private static JsonObject A(params (FormField Field, JsonNode? Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (f, v) in values) o[f.Id.ToString()] = v;
        return o;
    }

    // ---- template validation ------------------------------------------------

    [Fact]
    public void Template_rejects_unknown_field_type()
    {
        var def = new FormDefinition { Fields = { new FormField { Type = "Script", Label = "x" } } };
        Assert.NotNull(FormTemplateValidator.ValidateAndNormalize(def, 60));
    }

    [Fact]
    public void Template_needs_an_input_field()
    {
        var def = new FormDefinition { Fields = { new FormField { Type = FormFieldType.SectionHeader, Label = "Intro" } } };
        Assert.Equal("Add at least one input field.", FormTemplateValidator.ValidateAndNormalize(def, 60));
    }

    [Fact]
    public void Template_assigns_ids_and_rejects_duplicates()
    {
        var def = new FormDefinition { Fields = { new FormField { Type = FormFieldType.ShortText, Label = "Serial" } } };
        Assert.Null(FormTemplateValidator.ValidateAndNormalize(def, 60));
        Assert.NotEqual(Guid.Empty, def.Fields[0].Id);

        var id = Guid.NewGuid();
        var dup = new FormDefinition
        {
            Fields =
            {
                new FormField { Id = id, Type = FormFieldType.ShortText, Label = "A" },
                new FormField { Id = id, Type = FormFieldType.ShortText, Label = "B" },
            },
        };
        Assert.NotNull(FormTemplateValidator.ValidateAndNormalize(dup, 60));
    }

    [Fact]
    public void Template_choice_fields_need_unique_options()
    {
        var none = new FormDefinition { Fields = { new FormField { Type = FormFieldType.Dropdown, Label = "Pick" } } };
        Assert.NotNull(FormTemplateValidator.ValidateAndNormalize(none, 60));

        var twice = new FormDefinition
        {
            Fields =
            {
                new FormField
                {
                    Type = FormFieldType.Dropdown, Label = "Pick",
                    Options = new() { new() { Label = "Yes" }, new() { Label = "yes" } },
                },
            },
        };
        Assert.NotNull(FormTemplateValidator.ValidateAndNormalize(twice, 60));
    }

    [Fact]
    public void Template_respects_the_field_cap()
    {
        var def = new FormDefinition();
        for (var i = 0; i < 4; i++) def.Fields.Add(new FormField { Type = FormFieldType.ShortText, Label = $"F{i}" });
        Assert.NotNull(FormTemplateValidator.ValidateAndNormalize(def, 3));
    }

    // ---- answer validation --------------------------------------------------

    [Fact]
    public void Unknown_field_id_is_rejected()
    {
        var def = Def(F(FormFieldType.ShortText, "Serial"));
        var answers = new JsonObject { [Guid.NewGuid().ToString()] = "x" };
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, answers, Limits));
    }

    [Fact]
    public void Answer_for_a_section_header_is_rejected()
    {
        var header = F(FormFieldType.SectionHeader, "Intro");
        var def = Def(header, F(FormFieldType.ShortText, "Serial"));
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((header, "x")), Limits));
    }

    [Fact]
    public void Required_field_must_be_answered()
    {
        var f = F(FormFieldType.ShortText, "Serial", required: true);
        var def = Def(f);
        var ex = Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, "   ")), Limits));
        Assert.Equal(f.Id, ex.FieldId);
    }

    [Fact]
    public void Required_checkbox_must_be_ticked()
    {
        var f = F(FormFieldType.Checkbox, "Backup verified", required: true);
        var def = Def(f);
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, false)), Limits));
        Assert.Single(FormAnswerValidator.Parse(def, A((f, true)), Limits).Values);
    }

    [Fact]
    public void Dropdown_rejects_an_option_that_is_not_on_the_field()
    {
        var f = F(FormFieldType.Dropdown, "Priority", false, "Low", "High");
        var def = Def(f);
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, "o9")), Limits));
        Assert.Single(FormAnswerValidator.Parse(def, A((f, "o1")), Limits).Values);
    }

    [Fact]
    public void Multi_select_keeps_template_order_and_rejects_unknown_options()
    {
        var f = F(FormFieldType.CheckboxGroup, "Done", false, "A", "B", "C");
        var def = Def(f);
        var parsed = FormAnswerValidator.Parse(def, A((f, new JsonArray("o2", "o0", "o2"))), Limits);
        var v = Assert.IsType<FormAnswerValidator.OptionsValue>(parsed.Values[f.Id]);
        Assert.Equal(new[] { "o0", "o2" }, v.OptionIds);

        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, new JsonArray("o0", "evil"))), Limits));
    }

    [Fact]
    public void Number_honours_bounds_and_decimals()
    {
        var f = F(FormFieldType.Number, "Users");
        f.Min = 1; f.Max = 10; f.Decimals = 0;
        var def = Def(f);
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, 0)), Limits));
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, 11)), Limits));
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, 2.5m)), Limits));
        Assert.Single(FormAnswerValidator.Parse(def, A((f, 5)), Limits).Values);
    }

    [Fact]
    public void Long_text_honours_the_tunable_cap()
    {
        var f = F(FormFieldType.LongText, "Notes");
        var def = Def(f);
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, new string('x', 51))), Limits));
    }

    [Fact]
    public void Date_must_be_iso()
    {
        var f = F(FormFieldType.Date, "Delivery");
        var def = Def(f);
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, "13/11/2026")), Limits));
        Assert.Single(FormAnswerValidator.Parse(def, A((f, "2026-11-13")), Limits).Values);
    }

    [Fact]
    public void Agent_and_contact_ids_are_collected_for_server_resolution()
    {
        var agent = F(FormFieldType.Agent, "Technician");
        var contact = F(FormFieldType.Contact, "On site");
        var def = Def(agent, contact);
        var a = Guid.NewGuid();
        var c = Guid.NewGuid();
        var parsed = FormAnswerValidator.Parse(def, A((agent, a.ToString()), (contact, c.ToString())), Limits);
        Assert.Contains(a, parsed.References.AgentIds);
        Assert.Contains(c, parsed.References.ContactIds);
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((agent, "not-a-guid")), Limits));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Article_quantity_out_of_range_is_rejected(int qty)
    {
        var f = F(FormFieldType.Article, "Hardware");
        var def = Def(f);
        var rows = new JsonArray(new JsonObject { ["articleId"] = Guid.NewGuid().ToString(), ["quantity"] = qty });
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, rows)), Limits));
    }

    [Fact]
    public void Article_rows_are_capped_and_deduplicated()
    {
        var f = F(FormFieldType.Article, "Hardware");
        var def = Def(f);
        JsonObject Row(Guid id) => new() { ["articleId"] = id.ToString(), ["quantity"] = 1 };

        var tooMany = new JsonArray(Row(Guid.NewGuid()), Row(Guid.NewGuid()), Row(Guid.NewGuid()), Row(Guid.NewGuid()));
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, tooMany)), Limits));

        var same = Guid.NewGuid();
        Assert.Throws<FormAnswerException>(() => FormAnswerValidator.Parse(def, A((f, new JsonArray(Row(same), Row(same)))), Limits));

        var ok = FormAnswerValidator.Parse(def, A((f, new JsonArray(Row(Guid.NewGuid()), Row(Guid.NewGuid())))), Limits);
        Assert.Equal(2, ok.References.ArticleIds.Count);
    }

    // ---- renderer -----------------------------------------------------------

    private static JsonObject Build(FormDefinition def, JsonObject answers) =>
        FormAnswerValidator.Build(def, FormAnswerValidator.Parse(def, answers, Limits),
            new FormResolvedReferences(
                new Dictionary<Guid, string>(),
                new Dictionary<Guid, (string, string)>(),
                new Dictionary<Guid, (string, string)>()));

    [Fact]
    public void Note_html_escapes_answers_and_survives_the_sanitizer()
    {
        var f = F(FormFieldType.ShortText, "Serial <b>");
        var def = Def(f);
        var answers = Build(def, A((f, "<script>alert(1)</script><img src=x onerror=alert(1)>")));
        var formId = Guid.NewGuid();

        var html = FormRenderer.RenderNoteHtml(formId, "Laptop <intake>", 1, def, answers, null, null, dt => dt.ToString("u"));
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);

        var sanitized = new TicketHtmlSanitizer().Sanitize(html)!;
        Assert.Contains($"data-form-id=\"{formId}\"", sanitized);
        Assert.Contains("&lt;script&gt;", sanitized);
    }

    [Fact]
    public void Update_note_lists_only_changed_fields()
    {
        var a = F(FormFieldType.ShortText, "Serial");
        var b = F(FormFieldType.ShortText, "Model");
        var def = Def(a, b);
        var before = Build(def, A((a, "S1"), (b, "X1")));
        var after = Build(def, A((a, "S2"), (b, "X1")));

        var changed = FormRenderer.ChangedFields(def, before, after);
        Assert.Equal(new[] { a.Id }, changed);

        var html = FormRenderer.RenderNoteHtml(Guid.NewGuid(), "Laptop", 2, def, after, before, changed, dt => dt.ToString("u"));
        Assert.Contains("S1 → S2", html);
        Assert.DoesNotContain("X1", html);
        Assert.Contains("v2", html);
    }

    [Fact]
    public void Changed_fields_ignore_jsonb_key_reordering()
    {
        var f = F(FormFieldType.Article, "Hardware");
        var def = Def(f);
        var id = Guid.NewGuid().ToString();
        var stored = new JsonObject
        {
            [f.Id.ToString()] = new JsonArray(new JsonObject { ["code"] = "A1", ["name"] = "Dock", ["quantity"] = 2.0m, ["articleId"] = id }),
        };
        var fresh = new JsonObject
        {
            [f.Id.ToString()] = new JsonArray(new JsonObject { ["articleId"] = id, ["code"] = "A1", ["name"] = "Dock", ["quantity"] = 2m }),
        };
        Assert.Empty(FormRenderer.ChangedFields(def, stored, fresh));
    }
}
