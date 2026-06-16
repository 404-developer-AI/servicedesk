using System.Text;
using System.Text.Encodings.Web;
using Servicedesk.Infrastructure.Integrations.M365;

namespace Servicedesk.Infrastructure.Contracts.Reports;

/// The computed overview for one company's report: the filtered rows (also fed
/// to the PDF), the email-safe HTML block that replaces {{report.table}}, and
/// the summary counts that back the scalar {{report.*}} tokens.
public sealed class ReportRenderResult
{
    public IReadOnlyList<M365EnrichedMailbox> Rows { get; init; } = Array.Empty<M365EnrichedMailbox>();
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    public string TableHtml { get; init; } = string.Empty;

    public int MailboxCount { get; init; }
    public int? SpamProtected { get; init; }
    public int? SpamTotal { get; init; }
    public int? ExchangeProtected { get; init; }
    public int? ExchangeTotal { get; init; }
    public int? OneDriveProtected { get; init; }
    public int? OneDriveTotal { get; init; }
}

/// Builds the Microsoft 365 report overview (summary + table) from the enriched
/// company view. All mailbox-derived text is HTML-encoded — the customer's
/// directory data is untrusted display content embedded into the email HTML.
/// Output uses inline styles only (email clients strip &lt;style&gt; blocks) and a
/// light, branded palette — deliberately not glass, which renders poorly in mail.
public static class M365ReportRenderer
{
    private static readonly HtmlEncoder Enc = HtmlEncoder.Default;

    // Email-safe palette (mirrors the print palette of the ticket PDF).
    private const string ColProtected = "#059669";
    private const string ColUnprotected = "#dc2626";
    private const string ColNa = "#9aa0ab";
    private const string TextPrimary = "#1b1e27";
    private const string TextMuted = "#6b7280";
    private const string Border = "#e6e7ee";
    private const string HeaderBg = "#f3f0fb";
    private const string RowAltBg = "#fafafe";

    public static ReportRenderResult Render(
        M365CompanyMailboxView view, IReadOnlyList<string> columns, string scope)
    {
        var cols = ReportColumns.Normalize(columns);
        var rows = FilterByScope(view, scope);

        var spamTotal = view.SpamFilterAvailable ? rows.Count(r => r.SpamFilterProtected.HasValue) : (int?)null;
        var spamProt = view.SpamFilterAvailable ? rows.Count(r => r.SpamFilterProtected == true) : (int?)null;
        var exTotal = view.VeeamAvailable ? rows.Count(r => r.ExchangeProtected.HasValue) : (int?)null;
        var exProt = view.VeeamAvailable ? rows.Count(r => r.ExchangeProtected == true) : (int?)null;
        var odTotal = view.VeeamAvailable ? rows.Count(r => r.OneDriveProtected.HasValue) : (int?)null;
        var odProt = view.VeeamAvailable ? rows.Count(r => r.OneDriveProtected == true) : (int?)null;

        var html = BuildHtml(view, rows, cols, scope, spamProt, spamTotal, exProt, exTotal, odProt, odTotal);

        return new ReportRenderResult
        {
            Rows = rows,
            Columns = cols,
            TableHtml = html,
            MailboxCount = rows.Count,
            SpamProtected = spamProt,
            SpamTotal = spamTotal,
            ExchangeProtected = exProt,
            ExchangeTotal = exTotal,
            OneDriveProtected = odProt,
            OneDriveTotal = odTotal,
        };
    }

    /// "unprotected" keeps mailboxes unprotected on at least one *available*
    /// protection axis (the action-needed view); "all" keeps everything.
    private static List<M365EnrichedMailbox> FilterByScope(M365CompanyMailboxView view, string scope)
    {
        if (!string.Equals(scope, "unprotected", StringComparison.OrdinalIgnoreCase))
            return view.Mailboxes.ToList();

        return view.Mailboxes.Where(m =>
            (view.SpamFilterAvailable && m.SpamFilterProtected == false) ||
            (view.VeeamAvailable && m.ExchangeProtected == false) ||
            (view.VeeamAvailable && m.OneDriveProtected == false)).ToList();
    }

    private static string BuildHtml(
        M365CompanyMailboxView view, IReadOnlyList<M365EnrichedMailbox> rows, IReadOnlyList<string> cols, string scope,
        int? spamProt, int? spamTotal, int? exProt, int? exTotal, int? odProt, int? odTotal)
    {
        var sb = new StringBuilder();

        // ── Summary line ─────────────────────────────────────────────────
        sb.Append($"<div style=\"font-family:Arial,Helvetica,sans-serif;color:{TextPrimary};\">");
        sb.Append($"<p style=\"margin:0 0 8px 0;font-size:14px;\"><strong>{rows.Count}</strong> mailbox{(rows.Count == 1 ? "" : "es")}");
        if (string.Equals(scope, "unprotected", StringComparison.OrdinalIgnoreCase))
            sb.Append(" <span style=\"color:" + TextMuted + ";\">(unprotected only)</span>");
        sb.Append("</p>");

        sb.Append("<p style=\"margin:0 0 14px 0;font-size:13px;\">");
        AppendSummaryChip(sb, "Spam filter", spamProt, spamTotal);
        AppendSummaryChip(sb, "OneDrive backup", odProt, odTotal);
        AppendSummaryChip(sb, "Exchange backup", exProt, exTotal);
        sb.Append("</p>");

        // ── Table ────────────────────────────────────────────────────────
        sb.Append("<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" " +
                  "style=\"border-collapse:collapse;width:100%;font-family:Arial,Helvetica,sans-serif;font-size:13px;\">");

        // header
        sb.Append($"<tr style=\"background:{HeaderBg};\">");
        foreach (var key in cols)
        {
            var label = ReportColumns.All.First(c => c.Key == key).Label;
            sb.Append($"<th style=\"text-align:left;padding:8px 10px;border-bottom:2px solid {Border};" +
                      $"color:{TextPrimary};font-size:11px;text-transform:uppercase;letter-spacing:0.04em;\">{Enc.Encode(label)}</th>");
        }
        sb.Append("</tr>");

        if (rows.Count == 0)
        {
            sb.Append($"<tr><td colspan=\"{cols.Count}\" style=\"padding:14px 10px;color:{TextMuted};\">No mailboxes to show.</td></tr>");
        }
        else
        {
            var alt = false;
            foreach (var m in rows)
            {
                var bg = alt ? RowAltBg : "#ffffff";
                alt = !alt;
                sb.Append($"<tr style=\"background:{bg};\">");
                foreach (var key in cols)
                    sb.Append(Cell(key, m, view));
                sb.Append("</tr>");
            }
        }

        sb.Append("</table></div>");
        return sb.ToString();
    }

    private static void AppendSummaryChip(StringBuilder sb, string label, int? protectedCount, int? total)
    {
        if (total is null)
            return; // axis not available — omit the chip entirely.
        var ok = protectedCount.GetValueOrDefault();
        var color = ok == total ? ColProtected : ColUnprotected;
        sb.Append($"<span style=\"display:inline-block;margin:0 10px 4px 0;\">" +
                  $"<span style=\"color:{TextMuted};\">{Enc.Encode(label)}:</span> " +
                  $"<strong style=\"color:{color};\">{ok}/{total}</strong></span>");
    }

    private static string Cell(string key, M365EnrichedMailbox m, M365CompanyMailboxView view)
    {
        const string td = "padding:7px 10px;border-bottom:1px solid " + Border + ";vertical-align:top;";
        string Text(string? v) => $"<td style=\"{td}color:{TextPrimary};\">{Enc.Encode(v ?? "—")}</td>";

        return key switch
        {
            ReportColumns.Type => Text(string.IsNullOrWhiteSpace(m.MailboxType) ? "—" : m.MailboxType),
            ReportColumns.Name => Text(string.IsNullOrWhiteSpace(m.DisplayName) ? "—" : m.DisplayName),
            ReportColumns.Upn => Text(string.IsNullOrWhiteSpace(m.Upn) ? "—" : m.Upn),
            ReportColumns.Mail => Text(string.IsNullOrWhiteSpace(m.Mail) ? "—" : m.Mail),
            ReportColumns.Enabled => Text(m.Enabled is null ? "—" : (m.Enabled.Value ? "Yes" : "No")),
            ReportColumns.Licenses => Text(string.IsNullOrWhiteSpace(m.Licenses) ? "—" : m.Licenses),
            ReportColumns.Spam => BadgeCell(td, m.SpamFilterProtected, null, null),
            ReportColumns.OneDrive => BadgeCell(td, m.OneDriveProtected, m.OneDriveRestorePoints, m.OneDriveLastBackupUtc),
            ReportColumns.Exchange => BadgeCell(td, m.ExchangeProtected, m.ExchangeRestorePoints, m.ExchangeLastBackupUtc),
            _ => Text("—"),
        };
    }

    private static string BadgeCell(string td, bool? protectedFlag, int? restorePoints, DateTime? lastBackupUtc)
    {
        if (protectedFlag is null)
            return $"<td style=\"{td}\"><span style=\"color:{ColNa};\">n/a</span></td>";

        var on = protectedFlag.Value;
        var color = on ? ColProtected : ColUnprotected;
        var label = on ? "Protected" : "Unprotected";
        var sb = new StringBuilder();
        sb.Append($"<td style=\"{td}\">");
        sb.Append($"<span style=\"display:inline-block;padding:2px 8px;border-radius:10px;background:{color};" +
                  $"color:#ffffff;font-size:11px;font-weight:bold;\">{label}</span>");
        if (on && (restorePoints.HasValue || lastBackupUtc.HasValue))
        {
            var parts = new List<string>();
            if (restorePoints.HasValue) parts.Add($"{restorePoints.Value} restore point{(restorePoints.Value == 1 ? "" : "s")}");
            if (lastBackupUtc.HasValue) parts.Add($"last {lastBackupUtc.Value:yyyy-MM-dd}");
            sb.Append($"<div style=\"margin-top:3px;color:{TextMuted};font-size:11px;\">{Enc.Encode(string.Join(" · ", parts))}</div>");
        }
        sb.Append("</td>");
        return sb.ToString();
    }

    /// Substitutes the scalar {{tokens}} (everything except {{report.table}},
    /// which the sender injects separately because it is HTML, not a scalar).
    public static string ApplyScalarTokens(string? text, IReadOnlyDictionary<string, string> scalars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = text;
        foreach (var kv in scalars)
            result = result.Replace(kv.Key, kv.Value);
        return result;
    }
}
