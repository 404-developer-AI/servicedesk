using System.Text.Json.Nodes;
using Servicedesk.Infrastructure.Forms;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.58 — an Articles answer renders in the form note as a table with one
/// row per article (quantity · code · name), every value encoded; an update
/// note lists the articles one per line on both sides of the arrow.
public sealed class FormArticleNoteTests
{
    private static readonly FormField Articles = new() { Id = Guid.NewGuid(), Type = FormFieldType.Article, Label = "Articles" };
    private static readonly FormDefinition Def = new() { Fields = { Articles } };

    private static JsonArray Rows(params (decimal Qty, string Code, string Name)[] rows) => new(rows.Select(r => (JsonNode?)new JsonObject
    {
        ["articleId"] = Guid.NewGuid().ToString(),
        ["code"] = r.Code,
        ["name"] = r.Name,
        ["quantity"] = r.Qty,
    }).ToArray());

    [Fact]
    public void A_new_form_lists_one_row_per_article()
    {
        var answers = new JsonObject
        {
            [Articles.Id.ToString()] = Rows((20, "DBP001", "Veeam Backup [Microsoft 365]"), (1.5m, "X<1>", "Example <b>")),
        };

        var html = FormRenderer.RenderNoteHtml(Guid.NewGuid(), "Contract", 1, Def, answers, null, null, dt => dt.ToString("u"));

        Assert.Contains("<table class=\"sd-form-articles\">", html);
        Assert.Equal(2, html.Split("<td class=\"sd-form-articles-qty\">").Length - 1);
        Assert.Contains("<td class=\"sd-form-articles-qty\">20 ×</td><td class=\"sd-form-articles-code\">DBP001</td><td>Veeam Backup [Microsoft 365]</td>", html);
        Assert.Contains("X&lt;1&gt;", html);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("; ", html);
    }

    [Fact]
    public void An_update_lists_the_articles_one_per_line()
    {
        var before = new JsonObject { [Articles.Id.ToString()] = Rows((20, "DBP001", "Veeam Backup")) };
        var after = new JsonObject { [Articles.Id.ToString()] = Rows((20, "DBP001", "Veeam Backup"), (1, "SOP001", "Sophos Mail Security")) };

        var html = FormRenderer.RenderNoteHtml(Guid.NewGuid(), "Contract", 2, Def, after, before,
            FormRenderer.ChangedFields(Def, before, after), dt => dt.ToString("u"));

        Assert.Contains("20 × DBP001 Veeam Backup → 20 × DBP001 Veeam Backup<br>1 × SOP001 Sophos Mail Security", global::System.Net.WebUtility.HtmlDecode(html));
    }
}
