using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
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
/// No images, no custom fonts (embeds the shipped fallback). Matches the
/// premium but restrained feel of the rest of the app while staying
/// dependency-light.</para>
public interface IIntakeFormPdfBuilder
{
    byte[] Render(IntakeFormAgentView view, long ticketNumber);
}

public sealed class IntakeFormPdfBuilder : IIntakeFormPdfBuilder
{
    private const double MarginPt = 50;

    // Liberation Sans ships in the Alpine runtime image via the
    // ttf-liberation apk package (Dockerfile v0.0.20). MIT-licensed and
    // metric-compatible with Arial, so no layout changes vs the earlier
    // "Arial"-on-Windows dev runs. PdfSharpCore's SystemFonts enumeration
    // finds it without any custom resolver setup.
    private const string FontFamily = "Liberation Sans";

    public byte[] Render(IntakeFormAgentView view, long ticketNumber)
    {
        if (view.Instance.Status != IntakeFormStatus.Submitted || view.Answers is null)
        {
            throw new InvalidOperationException(
                "PDF can only be rendered for submitted intake form instances with answers.");
        }

        using var doc = new PdfDocument
        {
            Info =
            {
                Title = $"Intake — {view.Template.Name}",
                Subject = $"Ticket #{ticketNumber}",
                Creator = "Servicedesk",
            },
        };

        var answersDict = BuildAnswerLookup(view.Answers);

        PdfPage page = doc.AddPage();
        page.Size = PdfSharpCore.PageSize.A4;
        XGraphics gfx = XGraphics.FromPdfPage(page);

        // Fonts. Segoe UI is present on Windows; PdfSharpCore falls back via
        // its font resolver on Linux containers. We pick a neutral family
        // name so the resolver can substitute without the call erroring.
        var titleFont = new XFont(FontFamily, 18, XFontStyle.Bold);
        var metaFont = new XFont(FontFamily, 9, XFontStyle.Regular);
        var metaLabelFont = new XFont(FontFamily, 9, XFontStyle.Bold);
        var sectionFont = new XFont(FontFamily, 11, XFontStyle.Bold);
        var labelFont = new XFont(FontFamily, 10, XFontStyle.Bold);
        var valueFont = new XFont(FontFamily, 10, XFontStyle.Regular);
        var valueMutedFont = new XFont(FontFamily, 10, XFontStyle.Italic);
        var footerFont = new XFont(FontFamily, 8, XFontStyle.Regular);

        var pen = new XPen(XColor.FromArgb(220, 220, 220), 0.6);
        var accent = new XSolidBrush(XColor.FromArgb(88, 101, 242));
        var text = new XSolidBrush(XColor.FromArgb(30, 30, 30));
        var muted = new XSolidBrush(XColor.FromArgb(140, 140, 140));

        double y = MarginPt;
        double usableWidth = page.Width - (MarginPt * 2);

        // Title.
        gfx.DrawString(view.Template.Name, titleFont, text,
            new XRect(MarginPt, y, usableWidth, 24), XStringFormats.TopLeft);
        y += 26;

        // Template description (optional). Same wrap path as answers so
        // a long description doesn't overflow the metadata strip.
        if (!string.IsNullOrWhiteSpace(view.Template.Description))
        {
            var descLines = WrapLines(SanitizeForPdf(view.Template.Description), valueMutedFont, usableWidth, gfx);
            var descLineHeight = valueMutedFont.Height * 1.15;
            foreach (var line in descLines)
            {
                gfx.DrawString(line, valueMutedFont, muted,
                    new XRect(MarginPt, y, usableWidth, descLineHeight),
                    XStringFormats.TopLeft);
                y += descLineHeight;
            }
            y += 6;
        }

        // Metadata strip.
        y += 4;
        gfx.DrawLine(pen, MarginPt, y, page.Width - MarginPt, y);
        y += 10;

        y = DrawMetaRow(gfx, metaLabelFont, metaFont, text, muted, MarginPt, y,
            "Ticket", $"#{ticketNumber}");
        if (view.Instance.SentToEmail is { Length: > 0 } email)
        {
            y = DrawMetaRow(gfx, metaLabelFont, metaFont, text, muted, MarginPt, y,
                "Sent to", email);
        }
        if (view.Instance.SentUtc is DateTime sent)
        {
            y = DrawMetaRow(gfx, metaLabelFont, metaFont, text, muted, MarginPt, y,
                "Sent", sent.ToString("yyyy-MM-dd HH:mm 'UTC'"));
        }
        if (view.Instance.SubmittedUtc is DateTime submitted)
        {
            y = DrawMetaRow(gfx, metaLabelFont, metaFont, text, muted, MarginPt, y,
                "Submitted", submitted.ToString("yyyy-MM-dd HH:mm 'UTC'"));
        }

        y += 6;
        gfx.DrawLine(pen, MarginPt, y, page.Width - MarginPt, y);
        y += 16;

        // Q&A rows.
        foreach (var q in view.Template.Questions)
        {
            if (q.Type == IntakeQuestionType.SectionHeader)
            {
                (gfx, page, y) = EnsureSpace(doc, gfx, page, y, 30);
                gfx.DrawString(q.Label, sectionFont, accent,
                    new XRect(MarginPt, y, usableWidth, 16), XStringFormats.TopLeft);
                y += 18;
                continue;
            }

            answersDict.TryGetValue(q.Id.ToString(), out var answerElement);
            var valueText = SanitizeForPdf(FormatAnswer(q, answerElement));
            var isMissing = string.IsNullOrWhiteSpace(valueText) || valueText == "—";

            var valueBrush = isMissing ? muted : text;
            var valueFontUsed = isMissing ? valueMutedFont : valueFont;

            // Split the (possibly multi-line) answer and measure each line
            // so the reserved row height matches what we actually draw.
            // XTextFormatter in PdfSharpCore 1.3.67 has an NRE-path on
            // certain text shapes (control chars, mismatched XRect height,
            // empty blocks), so we emit lines through gfx.DrawString with
            // a manual word-wrap instead — simpler and more robust.
            var wrappedLines = WrapLines(valueText, valueFontUsed, usableWidth, gfx);
            var lineHeight = valueFontUsed.Height * 1.15;
            var valueHeight = wrappedLines.Count * lineHeight;
            var rowHeight = 14 + valueHeight + 12;

            (gfx, page, y) = EnsureSpace(doc, gfx, page, y, rowHeight);

            // Label
            gfx.DrawString(q.Label, labelFont, muted,
                new XRect(MarginPt, y, usableWidth, 14), XStringFormats.TopLeft);
            y += 14;

            // Value
            foreach (var line in wrappedLines)
            {
                gfx.DrawString(line, valueFontUsed, valueBrush,
                    new XRect(MarginPt, y, usableWidth, lineHeight),
                    XStringFormats.TopLeft);
                y += lineHeight;
            }
            y += 6;

            // Separator
            gfx.DrawLine(pen, MarginPt, y, page.Width - MarginPt, y);
            y += 8;
        }

        // Footer on last page.
        var footerText = $"Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — Servicedesk Intake";
        gfx.DrawString(footerText, footerFont, muted,
            new XRect(MarginPt, page.Height - MarginPt + 12, usableWidth, 12),
            XStringFormats.TopLeft);

        gfx.Dispose();

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static double DrawMetaRow(
        XGraphics gfx, XFont labelFont, XFont valueFont, XBrush text, XBrush muted,
        double x, double y, string label, string value)
    {
        const double labelWidth = 80;
        gfx.DrawString(label, labelFont, muted,
            new XRect(x, y, labelWidth, 12), XStringFormats.TopLeft);
        gfx.DrawString(value, valueFont, text,
            new XRect(x + labelWidth, y, 500, 12), XStringFormats.TopLeft);
        return y + 13;
    }

    /// Normalise a free-form answer so PdfSharpCore won't choke on it:
    /// collapse CRLF/CR to LF, strip format-category + zero-width chars
    /// (common when customers paste from Word or mobile keyboards) and
    /// fold NBSP to a regular space. Empty/whitespace coerces to "—" so
    /// the downstream wrap loop always has something to render.
    private static string SanitizeForPdf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "—";
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = normalized.Replace(' ', ' ');
        normalized = Regex.Replace(normalized, @"\p{Cf}", string.Empty); // strip format chars (U+200B etc.)
        return string.IsNullOrWhiteSpace(normalized) ? "—" : normalized;
    }

    /// Manual word-wrap that avoids XTextFormatter. Splits the input on
    /// hard newlines first (so multi-paragraph LongText stays
    /// paragraph-segmented), then greedily packs whitespace-separated
    /// tokens into lines up to <paramref name="maxWidth"/>. Tokens
    /// longer than the available width are hard-broken by character so
    /// a single URL or glued-together string can still render.
    private static List<string> WrapLines(string text, XFont font, double maxWidth, XGraphics gfx)
    {
        var output = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            output.Add(string.Empty);
            return output;
        }

        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                output.Add(string.Empty);
                continue;
            }

            var tokens = paragraph.Split(' ');
            var current = new StringBuilder();
            foreach (var token in tokens)
            {
                var candidate = current.Length == 0 ? token : current + " " + token;
                var width = gfx.MeasureString(candidate, font).Width;
                if (width <= maxWidth)
                {
                    current.Clear();
                    current.Append(candidate);
                    continue;
                }

                if (current.Length > 0)
                {
                    output.Add(current.ToString());
                    current.Clear();
                }

                // Token alone is wider than the line — hard-break by char.
                if (gfx.MeasureString(token, font).Width > maxWidth)
                {
                    var chunk = new StringBuilder();
                    foreach (var ch in token)
                    {
                        chunk.Append(ch);
                        if (gfx.MeasureString(chunk.ToString(), font).Width > maxWidth)
                        {
                            chunk.Length--;
                            output.Add(chunk.ToString());
                            chunk.Clear();
                            chunk.Append(ch);
                        }
                    }
                    if (chunk.Length > 0) current.Append(chunk);
                }
                else
                {
                    current.Append(token);
                }
            }
            output.Add(current.ToString());
        }

        return output;
    }

    private static (XGraphics gfx, PdfPage page, double y) EnsureSpace(
        PdfDocument doc, XGraphics gfx, PdfPage page, double y, double needed)
    {
        if (y + needed <= page.Height - MarginPt) return (gfx, page, y);

        gfx.Dispose();
        var newPage = doc.AddPage();
        newPage.Size = PdfSharpCore.PageSize.A4;
        var newGfx = XGraphics.FromPdfPage(newPage);
        return (newGfx, newPage, MarginPt);
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
