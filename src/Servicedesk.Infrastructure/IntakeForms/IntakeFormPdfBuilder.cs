using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Servicedesk.Domain.IntakeForms;

namespace Servicedesk.Infrastructure.IntakeForms;

/// Renders a submitted intake form as a printable A4 PDF.
///
/// <para>The agent-side download endpoint hands the <see cref="IntakeFormAgentView"/>
/// to <see cref="Render"/> and pipes the returned bytes straight into the
/// HTTP response. No temp files, no streams held on disk — the PDF is
/// rebuilt on demand so a template edit or cancelled instance can't leave
/// stale attachments around.</para>
///
/// <para>Layout is intentionally minimal: header with the template name
/// + submit metadata, then Q&amp;A rows with automatic page breaks.
/// No images, no custom fonts. Matches the
/// premium but restrained feel of the rest of the app while staying
/// dependency-light.</para>
public interface IIntakeFormPdfBuilder
{
    byte[] Render(IntakeFormAgentView view, long ticketNumber);
}

public sealed class IntakeFormPdfBuilder : IIntakeFormPdfBuilder
{
    // v0.1.30 — ported from PdfSharpCore to QuestPDF (the engine every other
    // PDF in the app already uses). PdfSharpCore was the only consumer of
    // SixLabors.ImageSharp, whose 2.x line stopped receiving security fixes;
    // dropping it removes both packages. QuestPDF does its own text layout
    // and wrapping, so the manual word-wrap and page-break bookkeeping are
    // gone too.
    private const float MarginPt = 50;

    private const string TextColor = "#1E1E1E";
    private const string MutedColor = "#8C8C8C";
    private const string AccentColor = "#5865F2";
    private const string RuleColor = "#DCDCDC";

    public byte[] Render(IntakeFormAgentView view, long ticketNumber)
    {
        if (view.Instance.Status != IntakeFormStatus.Submitted || view.Answers is null)
        {
            throw new InvalidOperationException(
                "PDF can only be rendered for submitted intake form instances with answers.");
        }

        QuestPDF.Settings.License = LicenseType.Community;
        var answersDict = BuildAnswerLookup(view.Answers);
        var generatedUtc = DateTime.UtcNow;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(MarginPt);
                page.DefaultTextStyle(t => t.FontFamily("Helvetica").FontSize(10).FontColor(TextColor));

                page.Content().Column(col =>
                {
                    col.Item().Text(SanitizeForPdf(view.Template.Name)).FontSize(18).Bold();

                    if (!string.IsNullOrWhiteSpace(view.Template.Description))
                    {
                        col.Item().PaddingTop(4)
                            .Text(SanitizeForPdf(view.Template.Description)).Italic().FontColor(MutedColor);
                    }

                    // Metadata strip.
                    col.Item().PaddingTop(10).LineHorizontal(0.6f).LineColor(RuleColor);
                    col.Item().PaddingVertical(8).Column(meta =>
                    {
                        meta.Spacing(2);
                        MetaRow(meta, "Ticket", $"#{ticketNumber}");
                        if (view.Instance.SentToEmail is { Length: > 0 } email)
                            MetaRow(meta, "Sent to", email);
                        if (view.Instance.SentUtc is DateTime sent)
                            MetaRow(meta, "Sent", sent.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
                        if (view.Instance.SubmittedUtc is DateTime submitted)
                            MetaRow(meta, "Submitted", submitted.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
                    });
                    col.Item().PaddingBottom(14).LineHorizontal(0.6f).LineColor(RuleColor);

                    // Q&A rows.
                    foreach (var q in view.Template.Questions)
                    {
                        if (q.Type == IntakeQuestionType.SectionHeader)
                        {
                            col.Item().PaddingTop(4).PaddingBottom(6)
                                .Text(SanitizeForPdf(q.Label)).FontSize(11).Bold().FontColor(AccentColor);
                            continue;
                        }

                        answersDict.TryGetValue(q.Id.ToString(), out var answerElement);
                        var valueText = SanitizeForPdf(FormatAnswer(q, answerElement));
                        var isMissing = valueText == "—";

                        // Start a row on a new page when less than ~3 lines
                        // fit, so a label is never stranded at the bottom;
                        // a row taller than a page still splits (ShowEntire
                        // would throw on a very long answer).
                        col.Item().EnsureSpace(60).Column(row =>
                        {
                            row.Item().Text(SanitizeForPdf(q.Label)).Bold().FontColor(MutedColor);
                            var value = row.Item().PaddingTop(2).Text(valueText).LineHeight(1.15f);
                            if (isMissing) value.Italic().FontColor(MutedColor);
                            row.Item().PaddingTop(6).PaddingBottom(8).LineHorizontal(0.6f).LineColor(RuleColor);
                        });
                    }
                });

                page.Footer().Text($"Generated {generatedUtc:yyyy-MM-dd HH:mm} UTC — Servicedesk Intake")
                    .FontSize(8).FontColor(MutedColor);
            });
        });

        document = document.WithMetadata(new DocumentMetadata
        {
            Title = $"Intake — {view.Template.Name}",
            Subject = $"Ticket #{ticketNumber}",
            Creator = "Servicedesk",
            Producer = "Servicedesk",
        });

        return document.GeneratePdf();
    }

    private static void MetaRow(ColumnDescriptor meta, string label, string value)
    {
        meta.Item().Row(r =>
        {
            r.ConstantItem(80).Text(label).FontSize(9).Bold().FontColor(MutedColor);
            r.RelativeItem().Text(SanitizeForPdf(value)).FontSize(9);
        });
    }

    /// Normalise free-form text before layout: collapse CRLF/CR to LF, strip
    /// format-category + zero-width chars (common when customers paste from
    /// Word or mobile keyboards) and fold NBSP to a regular space.
    /// Empty/whitespace coerces to "—" so a row always shows something.
    internal static string SanitizeForPdf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "—";
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = normalized.Replace(' ', ' ');
        normalized = Regex.Replace(normalized, @"\p{Cf}", string.Empty); // strip format chars (U+200B etc.)
        // Other C0 control characters (except LF/TAB) have no glyph.
        normalized = Regex.Replace(normalized, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? "—" : normalized;
    }

    private static Dictionary<string, JsonElement> BuildAnswerLookup(JsonDocument answers)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (answers.RootElement.ValueKind != JsonValueKind.Object) return map;
        foreach (var prop in answers.RootElement.EnumerateObject())
        {
            map[prop.Name] = prop.Value.Clone();
        }
        return map;
    }

    private static string FormatAnswer(IntakeQuestion q, JsonElement? raw)
    {
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "—";

        var el = raw.Value;
        switch (q.Type)
        {
            case IntakeQuestionType.YesNo:
                return el.ValueKind == JsonValueKind.True ? "Ja"
                    : el.ValueKind == JsonValueKind.False ? "Nee"
                    : "—";
            case IntakeQuestionType.DropdownSingle:
            {
                if (el.ValueKind != JsonValueKind.String) return "—";
                var value = el.GetString() ?? string.Empty;
                var opt = q.Options.FirstOrDefault(o => o.Value == value);
                return string.IsNullOrEmpty(value) ? "—" : opt?.Label ?? value;
            }
            case IntakeQuestionType.DropdownMulti:
            {
                if (el.ValueKind != JsonValueKind.Array) return "—";
                var sb = new StringBuilder();
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var v = item.GetString() ?? string.Empty;
                    if (v.Length == 0) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    var opt = q.Options.FirstOrDefault(o => o.Value == v);
                    sb.Append(opt?.Label ?? v);
                }
                return sb.Length == 0 ? "—" : sb.ToString();
            }
            case IntakeQuestionType.Date:
            {
                if (el.ValueKind != JsonValueKind.String) return "—";
                var s = el.GetString() ?? string.Empty;
                return s.Length >= 10 ? s[..10] : (s.Length == 0 ? "—" : s);
            }
            case IntakeQuestionType.Number:
                return el.ValueKind == JsonValueKind.Number
                    ? el.GetRawText()
                    : el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "—") : "—";
            case IntakeQuestionType.ShortText:
            case IntakeQuestionType.LongText:
                if (el.ValueKind != JsonValueKind.String) return el.GetRawText();
                var txt = el.GetString();
                return string.IsNullOrWhiteSpace(txt) ? "—" : txt!;
            default:
                return el.GetRawText();
        }
    }
}
