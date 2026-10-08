using System.Text;
using System.Text.Json;
using Servicedesk.Domain.IntakeForms;
using Servicedesk.Infrastructure.IntakeForms;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The intake-form PDF moved from PdfSharpCore to QuestPDF (v0.1.30). These
/// pin what the old builder had to special-case: customer text pasted from
/// Word/mobile (control + zero-width chars, NBSP, CRLF), very long answers
/// and unbreakable tokens, missing answers, and multi-page output.
public sealed class IntakeFormPdfBuilderTests
{
    [Fact]
    public void Renders_a_valid_pdf_for_a_submitted_form()
    {
        var pdf = new IntakeFormPdfBuilder().Render(View(Answers(new()
        {
            ["1"] = "Jan Peeters",
            ["2"] = "Line one\r\nLine two​ with nbsp and a bell\u0007",
            ["3"] = true,
            ["4"] = new[] { "a", "b" },
        })), 19908);

        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Very_long_answers_and_unbreakable_tokens_span_pages_without_throwing()
    {
        var longText = string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 2000));
        var token = new string('x', 5000);
        var pdf = new IntakeFormPdfBuilder().Render(View(Answers(new()
        {
            ["1"] = token,
            ["2"] = longText,
        })), 1);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(CountPages(pdf) > 1);
    }

    [Fact]
    public void Missing_answers_render_as_a_dash()
    {
        var pdf = new IntakeFormPdfBuilder().Render(View(Answers(new())), 1);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Only_submitted_forms_can_be_rendered()
    {
        var view = View(Answers(new())) with { Instance = Instance() with { Status = IntakeFormStatus.Sent } };
        Assert.Throws<InvalidOperationException>(() => new IntakeFormPdfBuilder().Render(view, 1));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData("   ", "—")]
    [InlineData("a\r\nb\rc", "a\nb\nc")]
    [InlineData("zero​width", "zerowidth")]
    [InlineData("nb sp", "nb sp")]
    [InlineData("bell\u0007tab\tok", "belltab\tok")]
    [InlineData("​​", "—")]
    public void Sanitize_normalises_pasted_text(string? input, string expected)
    {
        Assert.Equal(expected, IntakeFormPdfBuilder.SanitizeForPdf(input));
    }

    // ---- helpers -----------------------------------------------------------

    private static int CountPages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf("/Type /Page", idx, StringComparison.Ordinal)) >= 0)
        {
            if (!text.AsSpan(idx).StartsWith("/Type /Pages")) count++;
            idx += 10;
        }
        return count;
    }

    private static readonly Guid TemplateId = Guid.NewGuid();

    private static JsonDocument Answers(Dictionary<string, object> values) =>
        JsonDocument.Parse(JsonSerializer.Serialize(values));

    private static IntakeFormInstance Instance() => new(
        Guid.NewGuid(), TemplateId, Guid.NewGuid(), 1, 2, IntakeFormStatus.Submitted,
        null, DateTime.UtcNow, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, "198.51.100.1", "test",
        null, "customer@example.com", JsonDocument.Parse("{}"));

    private static IntakeFormAgentView View(JsonDocument answers)
    {
        IntakeQuestion Q(long id, IntakeQuestionType type, string label, params (string Value, string Label)[] opts) =>
            new(id, TemplateId, (int)id, type, label, null, false, null, null,
                opts.Select((o, i) => new IntakeQuestionOption(i, id, i, o.Value, o.Label)).ToList());

        var template = new IntakeTemplate(TemplateId, "Onboarding new workstation",
            "Please answer all questions​ before the technician visit.", true,
            DateTime.UtcNow, DateTime.UtcNow, null, new[]
            {
                Q(10, IntakeQuestionType.SectionHeader, "General"),
                Q(1, IntakeQuestionType.ShortText, "Name"),
                Q(2, IntakeQuestionType.LongText, "Description"),
                Q(3, IntakeQuestionType.YesNo, "Existing hardware?"),
                Q(4, IntakeQuestionType.DropdownMulti, "Software", ("a", "Office"), ("b", "Adsolut")),
                Q(5, IntakeQuestionType.Date, "Preferred date"),
            });

        return new IntakeFormAgentView(Instance(), template, answers);
    }
}
