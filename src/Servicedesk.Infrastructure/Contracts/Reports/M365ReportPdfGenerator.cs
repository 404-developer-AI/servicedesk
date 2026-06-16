using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Servicedesk.Infrastructure.Integrations.M365;

namespace Servicedesk.Infrastructure.Contracts.Reports;

public sealed record M365ReportPdfData(
    string CompanyName,
    string? CompanyCode,
    DateTime GeneratedUtc,
    string GeneratedBy,
    string ServerTimezoneId,
    string Scope,
    IReadOnlyList<string> Columns,
    IReadOnlyList<M365EnrichedMailbox> Rows,
    int MailboxCount,
    int? SpamProtected,
    int? SpamTotal,
    int? ExchangeProtected,
    int? ExchangeTotal,
    int? OneDriveProtected,
    int? OneDriveTotal);

/// Code-defined QuestPDF document for the Microsoft 365 report — the PDF copy
/// of the same overview shown in the email body. Mirrors the ticket-PDF print
/// palette so the two exports look like one product.
public static class M365ReportPdfGenerator
{
    private const string PageBg = "#ffffff";
    private const string HeaderBg = "#efeafb";
    private const string CardBg = "#f7f8fc";
    private const string CardBorder = "#e6e7ee";
    private const string TextPrimary = "#1b1e27";
    private const string TextSecondary = "#4b4f5a";
    private const string TextTertiary = "#8b8e99";
    private const string AccentPurple = "#6d4ad1";
    private const string ColProtected = "#059669";
    private const string ColUnprotected = "#dc2626";

    public static byte[] Generate(M365ReportPdfData data)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var tz = ResolveTimezone(data.ServerTimezoneId);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(0);
                page.DefaultTextStyle(t => t.FontFamily("Helvetica").FontSize(9).FontColor(TextPrimary));
                page.PageColor(PageBg);

                page.Header().Element(c => ComposeHeader(c, data, tz));
                page.Content().Padding(18).Column(col =>
                {
                    col.Spacing(12);
                    col.Item().Element(c => ComposeSummary(c, data));
                    col.Item().Element(c => ComposeTable(c, data));
                });
                page.Footer().Element(ComposeFooter);
            });
        }).GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, M365ReportPdfData data, TimeZoneInfo tz) =>
        container.Background(HeaderBg).PaddingHorizontal(22).PaddingVertical(14).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("MICROSOFT 365 REPORT")
                    .Bold().FontSize(16).FontColor(TextPrimary).LetterSpacing(0.04f);
                col.Item().Text(text =>
                {
                    text.Span(data.CompanyName).FontSize(10).FontColor(TextSecondary);
                    if (!string.IsNullOrWhiteSpace(data.CompanyCode))
                        text.Span($"  ·  {data.CompanyCode}").FontSize(9).FontColor(TextTertiary);
                });
            });
            row.AutoItem().AlignRight().Column(col =>
            {
                col.Item().Text($"Generated {FormatLocal(data.GeneratedUtc, tz)}")
                    .FontSize(7.5f).FontColor(TextSecondary);
                col.Item().Text($"By {data.GeneratedBy}").FontSize(7.5f).FontColor(TextTertiary);
            });
        });

    private static void ComposeSummary(IContainer container, M365ReportPdfData data) =>
        container.Background(CardBg).Border(0.5f).BorderColor(CardBorder).Padding(12).Row(row =>
        {
            row.RelativeItem().Element(c => Metric(c, "MAILBOXES", $"{data.MailboxCount}", TextPrimary));
            ChipMetric(row, "SPAM FILTER", data.SpamProtected, data.SpamTotal);
            ChipMetric(row, "ONEDRIVE BACKUP", data.OneDriveProtected, data.OneDriveTotal);
            ChipMetric(row, "EXCHANGE BACKUP", data.ExchangeProtected, data.ExchangeTotal);
        });

    private static void ChipMetric(RowDescriptor row, string label, int? prot, int? total)
    {
        if (total is null)
        {
            row.RelativeItem().Element(c => Metric(c, label, "n/a", TextTertiary));
            return;
        }
        var ok = prot.GetValueOrDefault();
        row.RelativeItem().Element(c => Metric(c, label, $"{ok}/{total}", ok == total ? ColProtected : ColUnprotected));
    }

    private static void Metric(IContainer container, string label, string value, string valueColor) =>
        container.Column(col =>
        {
            col.Item().Text(label).FontSize(7).FontColor(TextTertiary).LetterSpacing(0.06f);
            col.Item().Text(value).SemiBold().FontSize(13).FontColor(valueColor);
        });

    private static void ComposeTable(IContainer container, M365ReportPdfData data) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                foreach (var key in data.Columns)
                    cols.RelativeColumn(Weight(key));
            });

            foreach (var key in data.Columns)
            {
                var label = ReportColumns.All.First(c => c.Key == key).Label;
                table.Cell().Element(HeaderCell).Text(label.ToUpperInvariant())
                    .FontSize(7.5f).Bold().FontColor(AccentPurple).LetterSpacing(0.04f);
            }

            if (data.Rows.Count == 0)
            {
                table.Cell().ColumnSpan((uint)data.Columns.Count).Element(BodyCell)
                    .Text("No mailboxes to show.").FontSize(8.5f).FontColor(TextTertiary).Italic();
                return;
            }

            foreach (var m in data.Rows)
                foreach (var key in data.Columns)
                    RenderCell(table.Cell().Element(BodyCell), key, m);
        });

    private static void RenderCell(IContainer cell, string key, M365EnrichedMailbox m)
    {
        switch (key)
        {
            case ReportColumns.Type: cell.Text(Or(m.MailboxType)).FontSize(8.5f); break;
            case ReportColumns.Name: cell.Text(Or(m.DisplayName)).FontSize(8.5f); break;
            case ReportColumns.Upn: cell.Text(Or(m.Upn)).FontSize(8.5f); break;
            case ReportColumns.Mail: cell.Text(Or(m.Mail)).FontSize(8.5f); break;
            case ReportColumns.Enabled: cell.Text(m.Enabled is null ? "—" : (m.Enabled.Value ? "Yes" : "No")).FontSize(8.5f); break;
            case ReportColumns.Licenses: cell.Text(Or(m.Licenses)).FontSize(8f); break;
            case ReportColumns.Spam: BadgeCell(cell, m.SpamFilterProtected, null, null); break;
            case ReportColumns.OneDrive: BadgeCell(cell, m.OneDriveProtected, m.OneDriveRestorePoints, m.OneDriveLastBackupUtc); break;
            case ReportColumns.Exchange: BadgeCell(cell, m.ExchangeProtected, m.ExchangeRestorePoints, m.ExchangeLastBackupUtc); break;
            default: cell.Text("—").FontSize(8.5f); break;
        }
    }

    private static void BadgeCell(IContainer cell, bool? protectedFlag, int? restorePoints, DateTime? lastBackupUtc)
    {
        if (protectedFlag is null)
        {
            cell.Text("n/a").FontSize(8.5f).FontColor(TextTertiary);
            return;
        }
        var on = protectedFlag.Value;
        cell.Column(col =>
        {
            col.Item().Text(on ? "Protected" : "Unprotected")
                .FontSize(8.5f).SemiBold().FontColor(on ? ColProtected : ColUnprotected);
            if (on && (restorePoints.HasValue || lastBackupUtc.HasValue))
            {
                var parts = new List<string>();
                if (restorePoints.HasValue) parts.Add($"{restorePoints.Value} rp");
                if (lastBackupUtc.HasValue) parts.Add($"last {lastBackupUtc.Value:yyyy-MM-dd}");
                col.Item().Text(string.Join(" · ", parts)).FontSize(7f).FontColor(TextTertiary);
            }
        });
    }

    private static IContainer HeaderCell(IContainer c) =>
        c.Background(HeaderBg).BorderBottom(1).BorderColor(CardBorder).PaddingVertical(5).PaddingHorizontal(6);

    private static IContainer BodyCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(CardBorder).PaddingVertical(4).PaddingHorizontal(6);

    private static void ComposeFooter(IContainer container) =>
        container.BorderTop(0.5f).BorderColor(CardBorder).PaddingHorizontal(22).PaddingVertical(7).Row(row =>
        {
            row.RelativeItem().Text("Servicedesk — Microsoft 365 report").FontSize(7.5f).FontColor(TextTertiary);
            row.AutoItem().Text(text =>
            {
                text.Span("Page ").FontSize(7.5f).FontColor(TextTertiary);
                text.CurrentPageNumber().FontSize(7.5f).FontColor(TextTertiary);
                text.Span(" of ").FontSize(7.5f).FontColor(TextTertiary);
                text.TotalPages().FontSize(7.5f).FontColor(TextTertiary);
            });
        });

    private static float Weight(string key) => key switch
    {
        ReportColumns.Type => 1.2f,
        ReportColumns.Name => 1.8f,
        ReportColumns.Upn => 2.2f,
        ReportColumns.Mail => 2.2f,
        ReportColumns.Enabled => 0.8f,
        ReportColumns.Licenses => 1.8f,
        ReportColumns.Spam => 1.3f,
        ReportColumns.OneDrive => 1.6f,
        ReportColumns.Exchange => 1.6f,
        _ => 1.2f,
    };

    private static string Or(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    private static TimeZoneInfo ResolveTimezone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static string FormatLocal(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local.ToString("yyyy-MM-dd HH:mm");
    }
}
