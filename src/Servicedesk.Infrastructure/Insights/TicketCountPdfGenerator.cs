using System.Globalization;
using System.Security;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Servicedesk.Infrastructure.Insights;

public sealed record TicketCountPdfData(
    TicketCountReport Report,
    bool AllQueues,
    DateTime GeneratedUtc,
    string GeneratedBy);

/// Code-defined QuestPDF document for an Insights ticket-count overview
/// (New tickets / Closed tickets) — the same numbers, chart and table the page shows for the chosen filters.
/// The chart is drawn as vector SVG so it stays sharp at any zoom. Mirrors
/// the ticket / M365 report print palette so every export reads as one
/// product.
public static class TicketCountPdfGenerator
{
    private const string PageBg = "#ffffff";
    private const string HeaderBg = "#efeafb";
    private const string CardBg = "#f7f8fc";
    private const string CardBorder = "#e6e7ee";
    private const string GridLine = "#e6e7ee";
    private const string TextPrimary = "#1b1e27";
    private const string TextSecondary = "#4b4f5a";
    private const string TextTertiary = "#8b8e99";
    private const string AccentPurple = "#6d4ad1";

    /// Validated categorical palette (light steps), fixed order — identical
    /// to the SPA's chart palette so the PDF matches the page. A queue's
    /// colour follows its slot; slots past the eighth fold into one neutral
    /// "other" tone instead of inventing indistinguishable hues.
    internal static readonly string[] Palette =
        ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948"];
    private const string OverflowColor = "#898781";

    private const double SegmentGap = 1.5;

    public static byte[] Generate(TicketCountPdfData data)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var tz = InsightsCalendar.ResolveTimeZone(data.Report.TimeZoneId);
        var summary = TicketCountSummary.Compute(data.Report);
        var colors = data.Report.Queues.Select(q => SlotColor(q.Slot)).ToArray();

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(0);
                page.DefaultTextStyle(t => t.FontFamily("Helvetica").FontSize(9).FontColor(TextPrimary));
                page.PageColor(PageBg);

                page.Header().ShowOnce().Element(c => ComposeHeader(c, data, tz));
                page.Content().PaddingHorizontal(22).PaddingVertical(16).Column(col =>
                {
                    col.Spacing(14);
                    col.Item().Element(c => ComposeKpis(c, data.Report, summary));
                    col.Item().Element(c => ComposeChart(c, data.Report, colors));
                    col.Item().Element(c => ComposeTable(c, data.Report, colors));
                });
                page.Footer().Element(c => ComposeFooter(c, data.Report));
            });
        }).GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, TicketCountPdfData data, TimeZoneInfo tz) =>
        container.Background(HeaderBg).PaddingHorizontal(22).PaddingVertical(14).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("INSIGHTS").FontSize(7.5f).Bold().FontColor(AccentPurple).LetterSpacing(0.12f);
                col.Item().Text(Title(data.Report.Filter)).Bold().FontSize(18).FontColor(TextPrimary);
                col.Item().Text(text =>
                {
                    text.Span(InsightsCalendar.FormatRange(data.Report.Range)).FontSize(10).FontColor(TextSecondary);
                    text.Span($"  ·  per {data.Report.Granularity.ToString().ToLowerInvariant()}")
                        .FontSize(9).FontColor(TextTertiary);
                    text.Span(data.AllQueues
                            ? "  ·  all queues"
                            : $"  ·  {data.Report.Queues.Count} selected queue{(data.Report.Queues.Count == 1 ? "" : "s")}")
                        .FontSize(9).FontColor(TextTertiary);
                    if (OutcomeLabel(data.Report.Filter) is { } outcomes)
                        text.Span($"  ·  {outcomes}").FontSize(9).FontColor(TextTertiary);
                });
            });
            row.AutoItem().AlignRight().AlignMiddle().Column(col =>
            {
                col.Item().AlignRight().Text($"Generated {FormatLocal(data.GeneratedUtc, tz)}")
                    .FontSize(7.5f).FontColor(TextSecondary);
                col.Item().AlignRight().Text($"By {data.GeneratedBy}").FontSize(7.5f).FontColor(TextTertiary);
            });
        });

    private static void ComposeKpis(IContainer container, TicketCountReport report, TicketCountSummary s) =>
        container.Row(row =>
        {
            row.Spacing(10);
            row.RelativeItem().Element(c => Kpi(c, $"TOTAL {Title(report.Filter).ToUpperInvariant()}", s.Total.ToString("N0", CultureInfo.InvariantCulture), null));
            row.RelativeItem().Element(c => Kpi(c, "AVERAGE PER DAY",
                s.AveragePerDay.ToString("0.0", CultureInfo.InvariantCulture),
                s.ElapsedDays > 0 ? $"over {s.ElapsedDays} day{(s.ElapsedDays == 1 ? "" : "s")}" : "period not started"));
            row.RelativeItem().Element(c => Kpi(c, $"BUSIEST {report.Granularity.ToString().ToUpperInvariant()}",
                s.PeakBucket is null ? "—" : s.PeakCount.ToString("N0", CultureInfo.InvariantCulture),
                s.PeakBucket is null ? null : InsightsCalendar.FormatBucketLabel(s.PeakBucket.Start, report.Granularity)));
            row.RelativeItem().Element(c => Kpi(c, "LARGEST QUEUE",
                s.TopQueue?.Name ?? "—",
                s.TopQueue is null || s.Total == 0
                    ? null
                    : $"{s.TopQueueCount:N0} tickets · {(double)s.TopQueueCount / s.Total:P0}"));
        });

    private static void Kpi(IContainer container, string label, string value, string? sub) =>
        container.Background(CardBg).Border(0.5f).BorderColor(CardBorder).Padding(12).Column(col =>
        {
            col.Item().Text(label).FontSize(7).FontColor(TextTertiary).LetterSpacing(0.06f);
            // Numbers get the hero size; a (long) queue name steps down so it
            // stays on one line in the card.
            col.Item().PaddingTop(2).Text(value).SemiBold().FontSize(value.Length > 12 ? 12 : 17)
                .FontColor(TextPrimary).ClampLines(1);
            if (sub is not null)
                col.Item().Text(sub).FontSize(7.5f).FontColor(TextSecondary);
        });

    private static void ComposeChart(IContainer container, TicketCountReport report, string[] colors) =>
        container.Background(CardBg).Border(0.5f).BorderColor(CardBorder).Padding(14).Column(col =>
        {
            col.Spacing(10);
            col.Item().Svg(BuildChartSvg(report, colors, 760, 220)).FitWidth();

            // Legend with per-queue totals + share.
            var totals = QueueTotals(report);
            var grand = totals.Sum();
            col.Item().Row(row =>
            {
                row.Spacing(14);
                for (var i = 0; i < report.Queues.Count; i++)
                {
                    var idx = i;
                    row.AutoItem().Row(item =>
                    {
                        item.Spacing(4);
                        item.AutoItem().AlignMiddle().Width(8).Height(8).Background(colors[idx]);
                        item.AutoItem().Text(text =>
                        {
                            text.Span(report.Queues[idx].Name).FontSize(8).FontColor(TextSecondary);
                            text.Span($"  {totals[idx]:N0}").FontSize(8).SemiBold();
                            if (grand > 0)
                                text.Span($" ({(double)totals[idx] / grand:P0})").FontSize(7.5f).FontColor(TextTertiary);
                        });
                    });
                }
            });
        });

    private static void ComposeTable(IContainer container, TicketCountReport report, string[] colors) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(2.2f);
                foreach (var _ in report.Queues) cols.RelativeColumn(1);
                cols.RelativeColumn(1);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text(report.Granularity.ToString().ToUpperInvariant())
                    .FontSize(7).Bold().FontColor(AccentPurple).LetterSpacing(0.04f);
                for (var i = 0; i < report.Queues.Count; i++)
                {
                    var idx = i;
                    header.Cell().Element(HeaderCell).AlignRight().Row(r =>
                    {
                        r.Spacing(3);
                        r.RelativeItem().AlignRight().Text(report.Queues[idx].Name.ToUpperInvariant())
                            .FontSize(7).Bold().FontColor(AccentPurple).LetterSpacing(0.04f);
                        r.AutoItem().AlignMiddle().Width(5).Height(5).Background(colors[idx]);
                    });
                }
                header.Cell().Element(HeaderCell).AlignRight().Text("TOTAL")
                    .FontSize(7).Bold().FontColor(TextPrimary).LetterSpacing(0.04f);
            });

            foreach (var b in report.Buckets)
            {
                table.Cell().Element(BodyCell).Text(text =>
                {
                    text.Span(InsightsCalendar.FormatBucketLabel(b.Start, report.Granularity)).FontSize(8);
                    if (b.From != b.Start || b.To != InsightsCalendar.NextPeriodStart(b.Start, report.Granularity).AddDays(-1))
                        text.Span("  partial").FontSize(6.5f).FontColor(TextTertiary);
                });
                var sum = 0;
                foreach (var n in b.Counts)
                {
                    sum += n;
                    table.Cell().Element(BodyCell).AlignRight().Text(n == 0 ? "·" : n.ToString("N0", CultureInfo.InvariantCulture))
                        .FontSize(8).FontColor(n == 0 ? TextTertiary : TextPrimary);
                }
                table.Cell().Element(BodyCell).AlignRight().Text(sum.ToString("N0", CultureInfo.InvariantCulture))
                    .FontSize(8).SemiBold();
            }

            var totals = QueueTotals(report);
            table.Cell().Element(TotalCell).Text("Total").FontSize(8).Bold();
            foreach (var t in totals)
                table.Cell().Element(TotalCell).AlignRight().Text(t.ToString("N0", CultureInfo.InvariantCulture)).FontSize(8).Bold();
            table.Cell().Element(TotalCell).AlignRight().Text(totals.Sum().ToString("N0", CultureInfo.InvariantCulture))
                .FontSize(8).Bold().FontColor(AccentPurple);
        });

    private static IContainer HeaderCell(IContainer c) =>
        c.BorderBottom(1).BorderColor(AccentPurple).PaddingVertical(5).PaddingHorizontal(6);

    private static IContainer BodyCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(CardBorder).PaddingVertical(4).PaddingHorizontal(6);

    private static IContainer TotalCell(IContainer c) =>
        c.Background(CardBg).BorderTop(1).BorderColor(TextTertiary).PaddingVertical(5).PaddingHorizontal(6);

    private static void ComposeFooter(IContainer container, TicketCountReport report) =>
        container.PaddingHorizontal(22).PaddingVertical(10).Row(row =>
        {
            row.RelativeItem().Text(
                    report.Filter.Metric == InsightsMetric.New
                        ? $"Servicedesk Insights · days in {report.TimeZoneId} · deleted tickets excluded"
                        : $"Servicedesk Insights · days in {report.TimeZoneId} · counted on the close moment of tickets currently resolved or closed · deleted tickets excluded")
                .FontSize(7).FontColor(TextTertiary);
            row.AutoItem().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(7).FontColor(TextTertiary));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });

    internal static string Title(InsightsTicketFilter filter) =>
        filter.Metric == InsightsMetric.New ? "New tickets" : "Closed tickets";

    /// "resolved, merged" when only some outcomes are included; null for
    /// the New metric or when every outcome is in.
    private static string? OutcomeLabel(InsightsTicketFilter filter)
    {
        if (filter.Metric == InsightsMetric.New || filter.Outcomes == ClosedOutcomes.All) return null;
        var parts = new List<string>(3);
        if (filter.Outcomes.HasFlag(ClosedOutcomes.Resolved)) parts.Add("resolved");
        if (filter.Outcomes.HasFlag(ClosedOutcomes.Closed)) parts.Add("closed");
        if (filter.Outcomes.HasFlag(ClosedOutcomes.Merged)) parts.Add("merged");
        return string.Join(", ", parts) + " only";
    }

    private static int[] QueueTotals(TicketCountReport report)
    {
        var totals = new int[report.Queues.Count];
        foreach (var b in report.Buckets)
            for (var i = 0; i < totals.Length; i++) totals[i] += b.Counts[i];
        return totals;
    }

    /// Stacked bar chart as SVG. Only numbers, our own date labels and the
    /// fixed palette are written into the markup (queue names live in the
    /// QuestPDF legend) — nothing user-controlled reaches the SVG.
    internal static string BuildChartSvg(TicketCountReport report, string[] colors, int width, int height)
    {
        const int left = 38, right = 8, top = 8, bottom = 26;
        var plotW = width - left - right;
        var plotH = height - top - bottom;
        var n = Math.Max(1, report.Buckets.Count);

        var max = report.Buckets.Count == 0 ? 0 : report.Buckets.Max(b => b.Counts.Sum());
        var (step, ceiling) = NiceScale(max);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");

        // Gridlines + y labels.
        for (var v = 0; v <= ceiling; v += step)
        {
            var y = top + plotH - (double)v / ceiling * plotH;
            sb.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{left}\" y1=\"{y:0.##}\" x2=\"{width - right}\" y2=\"{y:0.##}\" stroke=\"{GridLine}\" stroke-width=\"{(v == 0 ? 1 : 0.6)}\"/>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{left - 6}\" y=\"{y + 3:0.##}\" font-family=\"Helvetica, Arial, sans-serif\" font-size=\"8\" fill=\"{TextTertiary}\" text-anchor=\"end\">{v:N0}</text>");
        }

        var slot = (double)plotW / n;
        var barW = Math.Max(1.5, Math.Min(slot * 0.62, 46));
        var labelEvery = (int)Math.Ceiling(n / 14.0);

        for (var i = 0; i < report.Buckets.Count; i++)
        {
            var b = report.Buckets[i];
            var x = left + slot * i + (slot - barW) / 2;
            var yCursor = (double)(top + plotH);
            var topmost = -1;
            for (var q = 0; q < b.Counts.Count; q++) if (b.Counts[q] > 0) topmost = q;
            for (var q = 0; q < b.Counts.Count; q++)
            {
                if (b.Counts[q] == 0) continue;
                var h = (double)b.Counts[q] / ceiling * plotH;
                var y = yCursor - h;
                yCursor = y;
                // Surface gap between stacked segments; rounded data-end on top.
                var drawH = q == topmost ? h : Math.Max(0.5, h - SegmentGap);
                var drawY = q == topmost ? y : y + SegmentGap;
                if (q == topmost)
                {
                    var r = Math.Min(2.5, Math.Min(barW / 2, drawH));
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<path d=\"M{x:0.##},{drawY + drawH:0.##} V{drawY + r:0.##} Q{x:0.##},{drawY:0.##} {x + r:0.##},{drawY:0.##} H{x + barW - r:0.##} Q{x + barW:0.##},{drawY:0.##} {x + barW:0.##},{drawY + r:0.##} V{drawY + drawH:0.##} Z\" fill=\"{colors[q]}\"/>");
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<rect x=\"{x:0.##}\" y=\"{drawY:0.##}\" width=\"{barW:0.##}\" height=\"{drawH:0.##}\" fill=\"{colors[q]}\"/>");
                }
            }

            if (i % labelEvery == 0)
            {
                var label = SecurityElement.Escape(ShortLabel(b.Start, report.Granularity));
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{left + slot * i + slot / 2:0.##}\" y=\"{height - 10}\" font-family=\"Helvetica, Arial, sans-serif\" font-size=\"8\" fill=\"{TextSecondary}\" text-anchor=\"middle\">{label}</text>");
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string ShortLabel(DateOnly start, InsightsGranularity g) => g switch
    {
        InsightsGranularity.Day => start.ToString("d MMM", CultureInfo.InvariantCulture),
        InsightsGranularity.Week => $"W{ISOWeek.GetWeekOfYear(start.ToDateTime(TimeOnly.MinValue)):00}",
        InsightsGranularity.Month => start.ToString("MMM yy", CultureInfo.InvariantCulture),
        _ => start.ToString("yyyy", CultureInfo.InvariantCulture),
    };

    /// Round the axis to 1/2/5 × 10^k steps with ~4-5 gridlines.
    internal static (int Step, int Ceiling) NiceScale(int max)
    {
        if (max <= 0) return (1, 4);
        var raw = max / 4.0;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var norm = raw / mag;
        var nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        var step = Math.Max(1, (int)(nice * mag));
        var ceiling = (int)Math.Ceiling((double)max / step) * step;
        return (step, ceiling);
    }

    internal static string SlotColor(int slot) =>
        slot >= 0 && slot < Palette.Length ? Palette[slot] : OverflowColor;

    private static string FormatLocal(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz)
            .ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}
