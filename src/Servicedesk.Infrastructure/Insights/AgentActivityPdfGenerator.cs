using System.Globalization;
using System.Security;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Servicedesk.Infrastructure.Insights;

/// Which figure the Agents overview chart plots.
public enum AgentChartMetric { Tickets, Time, Calls }

/// One agent's lists in the PDF — the first items of each page's Total:
/// tickets worked on, and tickets opened and closed without action.
public sealed record AgentPdfTicketList(InsightsAgent Agent, AgentTicketPage Page, OpenedNoActionPage? Opened = null);

public sealed record AgentActivityPdfData(
    AgentActivityReport Report,
    AgentChartMetric Metric,
    IReadOnlyList<AgentPdfTicketList> Lists,
    DateTime GeneratedUtc,
    string GeneratedBy);

/// Code-defined QuestPDF document for the Insights Agents overview: the
/// side-by-side totals, the chart for the metric on screen, then each
/// agent's ticket list. Same print palette as the ticket-count export.
public static class AgentActivityPdfGenerator
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

    public static byte[] Generate(AgentActivityPdfData data)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var tz = InsightsCalendar.ResolveTimeZone(data.Report.TimeZoneId);
        var colors = data.Report.Agents.Select(a => TicketCountPdfGenerator.SlotColor(a.Slot)).ToArray();

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
                    col.Item().Element(c => ComposeTotals(c, data.Report, colors));
                    col.Item().Element(c => ComposeChart(c, data, colors));
                    foreach (var list in data.Lists)
                    {
                        col.Item().Element(c => ComposeTicketList(c, list, colors, tz));
                        if (list.Opened is not null)
                            col.Item().Element(c => ComposeOpenedList(c, list.Agent, list.Opened, colors, tz));
                    }
                });
                page.Footer().Element(c => ComposeFooter(c, data.Report));
            });
        }).GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, AgentActivityPdfData data, TimeZoneInfo tz) =>
        container.Background(HeaderBg).PaddingHorizontal(22).PaddingVertical(14).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("INSIGHTS").FontSize(7.5f).Bold().FontColor(AccentPurple).LetterSpacing(0.12f);
                col.Item().Text("Agent activity").Bold().FontSize(18).FontColor(TextPrimary);
                col.Item().Text(text =>
                {
                    text.Span(InsightsCalendar.FormatRange(data.Report.Range)).FontSize(10).FontColor(TextSecondary);
                    text.Span($"  ·  per {data.Report.Granularity.ToString().ToLowerInvariant()}")
                        .FontSize(9).FontColor(TextTertiary);
                    text.Span($"  ·  {data.Report.Agents.Count} agent{(data.Report.Agents.Count == 1 ? "" : "s")}")
                        .FontSize(9).FontColor(TextTertiary);
                });
            });
            row.AutoItem().AlignRight().AlignMiddle().Column(col =>
            {
                col.Item().AlignRight().Text($"Generated {FormatLocal(data.GeneratedUtc, tz)}")
                    .FontSize(7.5f).FontColor(TextSecondary);
                col.Item().AlignRight().Text($"By {data.GeneratedBy}").FontSize(7.5f).FontColor(TextTertiary);
            });
        });

    private static void ComposeTotals(IContainer container, AgentActivityReport report, string[] colors) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(1.6f);
                foreach (var _ in report.Agents) cols.RelativeColumn(1);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("");
                for (var i = 0; i < report.Agents.Count; i++)
                {
                    var idx = i;
                    header.Cell().Element(HeaderCell).AlignRight().Row(r =>
                    {
                        r.Spacing(4);
                        r.RelativeItem().AlignRight().Text(report.Agents[idx].Name)
                            .FontSize(8).Bold().FontColor(TextPrimary).ClampLines(1);
                        r.AutoItem().AlignMiddle().Width(7).Height(7).Background(colors[idx]);
                    });
                }
            });

            void MetricRow(string label, Func<AgentActivityTotals, string> value, string? hint = null)
            {
                table.Cell().Element(BodyCell).Text(text =>
                {
                    text.Span(label).FontSize(8.5f).SemiBold();
                    if (hint is not null) text.Span($"  {hint}").FontSize(7).FontColor(TextTertiary);
                });
                foreach (var t in report.Totals)
                    table.Cell().Element(BodyCell).AlignRight().Text(value(t)).FontSize(10).SemiBold();
            }

            MetricRow("Tickets worked on", t => N(t.Tickets));
            MetricRow("Time on tickets", t => FormatMinutes(t.TicketMinutes));
            MetricRow("Incoming calls", t => N(t.CallsIn));
            MetricRow("Outgoing calls", t => N(t.CallsOut));
            MetricRow("Talk time", t => FormatSeconds(t.CallSeconds), "answered calls");
            MetricRow("Opened without action", t => N(t.OpenedNoAction), "opened and closed, nothing done");
        });

    private static void ComposeChart(IContainer container, AgentActivityPdfData data, string[] colors) =>
        container.Background(CardBg).Border(0.5f).BorderColor(CardBorder).Padding(14).Column(col =>
        {
            col.Spacing(8);
            col.Item().Text($"{MetricTitle(data.Metric)} per {data.Report.Granularity.ToString().ToLowerInvariant()}")
                .FontSize(10).SemiBold();
            col.Item().Svg(BuildChartSvg(data.Report, data.Metric, colors, 760, 200)).FitWidth();
            col.Item().Row(row =>
            {
                row.Spacing(14);
                for (var i = 0; i < data.Report.Agents.Count; i++)
                {
                    var idx = i;
                    row.AutoItem().Row(item =>
                    {
                        item.Spacing(4);
                        item.AutoItem().AlignMiddle().Width(8).Height(8).Background(colors[idx]);
                        item.AutoItem().Text(data.Report.Agents[idx].Name).FontSize(8).FontColor(TextSecondary);
                    });
                }
            });
        });

    private static void ComposeTicketList(IContainer container, AgentPdfTicketList list, string[] colors, TimeZoneInfo tz) =>
        container.Column(col =>
        {
            col.Spacing(6);
            col.Item().PaddingTop(4).Row(row =>
            {
                row.Spacing(6);
                row.AutoItem().AlignMiddle().Width(8).Height(8).Background(colors[list.Agent.Slot]);
                row.RelativeItem().Text(text =>
                {
                    text.Span($"Tickets — {list.Agent.Name}").FontSize(11).SemiBold();
                    text.Span($"  {N(list.Page.Total)} ticket{(list.Page.Total == 1 ? "" : "s")}")
                        .FontSize(8).FontColor(TextTertiary);
                    if (list.Page.Items.Count < list.Page.Total)
                        text.Span($" · first {N(list.Page.Items.Count)} shown").FontSize(8).FontColor(TextTertiary);
                });
            });

            if (list.Page.Items.Count == 0)
            {
                col.Item().Text("No tickets in this period.").FontSize(8).FontColor(TextSecondary);
                return;
            }

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(52);
                    cols.RelativeColumn(3.2f);
                    cols.RelativeColumn(1.6f);
                    cols.RelativeColumn(1.1f);
                    cols.ConstantColumn(78);
                    cols.ConstantColumn(58);
                    cols.ConstantColumn(58);
                    cols.ConstantColumn(58);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("TICKET").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("SUBJECT").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("COMPANY").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("STATUS").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("LAST ACTIVITY").Style(HeaderText());
                    header.Cell().Element(HeaderCell).AlignRight().Text("PERIOD").Style(HeaderText());
                    header.Cell().Element(HeaderCell).AlignRight().Text("AGENT TOTAL").Style(HeaderText());
                    header.Cell().Element(HeaderCell).AlignRight().Text("TICKET TOTAL").Style(HeaderText());
                });

                foreach (var t in list.Page.Items)
                {
                    table.Cell().Element(BodyCell).Text($"#{t.Number}").FontSize(8).SemiBold();
                    table.Cell().Element(BodyCell).Text(t.Subject).FontSize(8).ClampLines(1);
                    table.Cell().Element(BodyCell).Text(t.CompanyName ?? "—").FontSize(8).FontColor(TextSecondary).ClampLines(1);
                    table.Cell().Element(BodyCell).Text(t.StatusName).FontSize(8).FontColor(TextSecondary).ClampLines(1);
                    table.Cell().Element(BodyCell).Text(LastActivity(t, tz)).FontSize(7.5f).FontColor(TextSecondary);
                    table.Cell().Element(BodyCell).AlignRight().Text(MinutesOrDot(t.PeriodMinutes)).FontSize(8).SemiBold();
                    table.Cell().Element(BodyCell).AlignRight().Text(MinutesOrDot(t.AgentMinutes)).FontSize(8);
                    table.Cell().Element(BodyCell).AlignRight().Text(MinutesOrDot(t.TicketMinutes)).FontSize(8).FontColor(TextSecondary);
                }
            });
        });

    private static void ComposeOpenedList(
        IContainer container, InsightsAgent agent, OpenedNoActionPage page, string[] colors, TimeZoneInfo tz) =>
        container.Column(col =>
        {
            col.Spacing(6);
            col.Item().PaddingTop(4).Row(row =>
            {
                row.Spacing(6);
                row.AutoItem().AlignMiddle().Width(8).Height(8).Background(colors[agent.Slot]);
                row.RelativeItem().Text(text =>
                {
                    text.Span($"Opened without action — {agent.Name}").FontSize(11).SemiBold();
                    text.Span($"  {N(page.Total)} opening{(page.Total == 1 ? "" : "s")}")
                        .FontSize(8).FontColor(TextTertiary);
                    if (page.Items.Count < page.Total)
                        text.Span($" · first {N(page.Items.Count)} shown").FontSize(8).FontColor(TextTertiary);
                });
            });

            if (page.Items.Count == 0)
            {
                col.Item().Text("No tickets opened and closed without action in this period.").FontSize(8).FontColor(TextSecondary);
                return;
            }

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(52);
                    cols.RelativeColumn(3.2f);
                    cols.RelativeColumn(1.6f);
                    cols.RelativeColumn(1.1f);
                    cols.ConstantColumn(82);
                    cols.ConstantColumn(82);
                    cols.ConstantColumn(54);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("TICKET").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("SUBJECT").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("COMPANY").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("STATUS NOW").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("OPENED").Style(HeaderText());
                    header.Cell().Element(HeaderCell).Text("CLOSED").Style(HeaderText());
                    header.Cell().Element(HeaderCell).AlignRight().Text("OPEN FOR").Style(HeaderText());
                });

                foreach (var o in page.Items)
                {
                    table.Cell().Element(BodyCell).Text($"#{o.Number}").FontSize(8).SemiBold();
                    table.Cell().Element(BodyCell).Text(o.Subject).FontSize(8).ClampLines(1);
                    table.Cell().Element(BodyCell).Text(o.CompanyName ?? "—").FontSize(8).FontColor(TextSecondary).ClampLines(1);
                    table.Cell().Element(BodyCell).Text(o.StatusName).FontSize(8).FontColor(TextSecondary).ClampLines(1);
                    table.Cell().Element(BodyCell).Text(FormatLocal(o.OpenedUtc, tz)).FontSize(7.5f).FontColor(TextSecondary);
                    table.Cell().Element(BodyCell).Text(text =>
                    {
                        text.Span(FormatLocal(o.ClosedUtc, tz)).FontSize(7.5f).FontColor(TextSecondary);
                        if (o.CloseReason != "removed")
                            text.Span($"  {CloseReasonLabel(o.CloseReason)}").FontSize(6.5f).FontColor(TextTertiary);
                    });
                    table.Cell().Element(BodyCell).AlignRight().Text(FormatDuration(o.DurationSeconds)).FontSize(8);
                }
            });
        });

    /// "removed" is the normal X-click and gets no label.
    internal static string CloseReasonLabel(string reason) => reason switch
    {
        "cleared" => "list cleared",
        "evicted" => "auto (list full)",
        _ => "",
    };

    /// Open duration: seconds below a minute, then minutes, then hours.
    internal static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{Math.Max(0, seconds)}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60:00}s";
        return seconds < 86400
            ? FormatMinutes(seconds / 60)
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 86400}d {seconds % 86400 / 3600}h");
    }

    private static TextStyle HeaderText() =>
        TextStyle.Default.FontSize(7).Bold().FontColor(AccentPurple).LetterSpacing(0.04f);

    private static IContainer HeaderCell(IContainer c) =>
        c.BorderBottom(1).BorderColor(AccentPurple).PaddingVertical(5).PaddingHorizontal(6);

    private static IContainer BodyCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(CardBorder).PaddingVertical(4).PaddingHorizontal(6);

    private static void ComposeFooter(IContainer container, AgentActivityReport report) =>
        container.PaddingHorizontal(22).PaddingVertical(10).Row(row =>
        {
            row.RelativeItem().Text(
                    $"Servicedesk Insights · days in {report.TimeZoneId} · a ticket counts when the agent acted on it or logged time on it in the period · " +
                    "calls are answered calls seen by the Telavox integration · an opening runs from a ticket entering the agent's recent list until it leaves it · deleted tickets excluded")
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

    internal static string MetricTitle(AgentChartMetric metric) => metric switch
    {
        AgentChartMetric.Time => "Hours on tickets",
        AgentChartMetric.Calls => "Calls",
        _ => "Tickets worked on",
    };

    /// Value plotted for one agent in one bucket. Time is plotted in hours.
    internal static double ChartValue(AgentActivityTotals v, AgentChartMetric metric) => metric switch
    {
        AgentChartMetric.Time => v.TicketMinutes / 60.0,
        AgentChartMetric.Calls => v.Calls,
        _ => v.Tickets,
    };

    /// Grouped bar chart as SVG — one bar per agent per bucket. Only numbers,
    /// our own date labels and the fixed palette reach the markup (agent
    /// names live in the QuestPDF legend).
    internal static string BuildChartSvg(
        AgentActivityReport report, AgentChartMetric metric, string[] colors, int width, int height)
    {
        const int left = 38, right = 8, top = 8, bottom = 26;
        var plotW = width - left - right;
        var plotH = height - top - bottom;
        var n = Math.Max(1, report.Buckets.Count);
        var agents = Math.Max(1, report.Agents.Count);

        var max = report.Buckets.Count == 0
            ? 0
            : report.Buckets.SelectMany(b => b.Values).Select(v => ChartValue(v, metric)).DefaultIfEmpty(0).Max();
        var (step, ceiling) = TicketCountPdfGenerator.NiceScale((int)Math.Ceiling(max));

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");

        for (var v = 0; v <= ceiling; v += step)
        {
            var y = top + plotH - (double)v / ceiling * plotH;
            var label = metric == AgentChartMetric.Time ? $"{v:N0}h" : $"{v:N0}";
            sb.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{left}\" y1=\"{y:0.##}\" x2=\"{width - right}\" y2=\"{y:0.##}\" stroke=\"{GridLine}\" stroke-width=\"{(v == 0 ? 1 : 0.6)}\"/>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{left - 6}\" y=\"{y + 3:0.##}\" font-family=\"Helvetica, Arial, sans-serif\" font-size=\"8\" fill=\"{TextTertiary}\" text-anchor=\"end\">{label}</text>");
        }

        var slot = (double)plotW / n;
        var groupW = Math.Min(slot * 0.72, 22.0 * agents);
        var gap = agents > 1 ? Math.Min(1.5, groupW / agents / 4) : 0;
        var barW = Math.Max(1.0, (groupW - gap * (agents - 1)) / agents);
        var labelEvery = (int)Math.Ceiling(n / 14.0);

        for (var i = 0; i < report.Buckets.Count; i++)
        {
            var b = report.Buckets[i];
            var x0 = left + slot * i + (slot - groupW) / 2;
            for (var a = 0; a < b.Values.Count && a < colors.Length; a++)
            {
                var value = ChartValue(b.Values[a], metric);
                if (value <= 0) continue;
                var h = value / ceiling * plotH;
                var x = x0 + a * (barW + gap);
                var y = top + plotH - h;
                var r = Math.Min(2.0, Math.Min(barW / 2, h));
                sb.Append(CultureInfo.InvariantCulture,
                    $"<path d=\"M{x:0.##},{y + h:0.##} V{y + r:0.##} Q{x:0.##},{y:0.##} {x + r:0.##},{y:0.##} H{x + barW - r:0.##} Q{x + barW:0.##},{y:0.##} {x + barW:0.##},{y + r:0.##} V{y + h:0.##} Z\" fill=\"{colors[a]}\"/>");
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

    private static string LastActivity(AgentTicketItem t, TimeZoneInfo tz)
    {
        if (t.LastActionUtc is { } utc)
        {
            var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, tz));
            if (t.LastEntryDate is not { } d || d <= local) return FormatLocal(utc, tz);
        }
        return t.LastEntryDate?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string MinutesOrDot(int minutes) => minutes == 0 ? "·" : FormatMinutes(minutes);

    /// "45m", "3h", "12h 30m".
    internal static string FormatMinutes(int minutes)
    {
        if (minutes <= 0) return "0m";
        var h = minutes / 60;
        var m = minutes % 60;
        if (h == 0) return $"{m}m";
        return m == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h:N0}h")
            : string.Create(CultureInfo.InvariantCulture, $"{h:N0}h {m:00}m");
    }

    /// Talk time: whole minutes above a minute, seconds below.
    internal static string FormatSeconds(long seconds)
    {
        if (seconds <= 0) return "0m";
        if (seconds < 60) return $"{seconds}s";
        return FormatMinutes((int)Math.Min(int.MaxValue, (seconds + 30) / 60));
    }

    private static string FormatLocal(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz)
            .ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}
