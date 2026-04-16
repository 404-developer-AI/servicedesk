using System.Text.Json;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Servicedesk.Infrastructure.Export;

public sealed record TicketPdfData(
    long Number,
    string Subject,
    string Source,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? DueUtc,
    DateTime? FirstResponseUtc,
    DateTime? ResolvedUtc,
    DateTime? ClosedUtc,
    string QueueName,
    string StatusName,
    string StatusCategory,
    string PriorityName,
    int PriorityLevel,
    string? CategoryName,
    string? AssigneeName,
    string RequesterName,
    string RequesterEmail,
    string? CompanyName,
    string BodyText,
    string? BodyHtml,
    DateTime? FirstResponseDeadlineUtc,
    DateTime? ResolutionDeadlineUtc,
    DateTime? FirstResponseMetUtc,
    DateTime? ResolutionMetUtc,
    bool SlaPaused,
    IReadOnlyList<TicketPdfEvent> Events,
    IReadOnlyList<TicketPdfPin> PinnedEvents,
    DateTime ExportedAtUtc,
    string ExportedBy,
    string ServerTimezoneId);

public sealed record TicketPdfEvent(
    string EventType,
    string? AuthorName,
    string? BodyText,
    string? BodyHtml,
    bool IsInternal,
    DateTime CreatedUtc,
    string? MetadataJson,
    IReadOnlyList<TicketPdfInlineImage> InlineImages);

public sealed record TicketPdfInlineImage(
    string Filename,
    string MimeType,
    byte[] Data);

public sealed record TicketPdfPin(
    long EventId,
    string? PinnedByName,
    string Remark,
    DateTime CreatedUtc);

public static partial class TicketPdfGenerator
{
    // ── Palette ──────────────────────────────────────────────────────────────
    private static readonly string PageBg        = "#0a0a0f";
    private static readonly string CardBg         = "#141418";
    private static readonly string CardBorder      = "#2a2a30";
    private static readonly string TextPrimary     = "#fafafa";
    private static readonly string TextSecondary   = "#9a9aaa";
    private static readonly string TextTertiary    = "#6a6a7a";
    private static readonly string AccentPurple    = "#b8a7e8";
    private static readonly string AccentBlue      = "#5b9bf5";
    private static readonly string HeaderBg        = "#1a1030";
    private static readonly string ColorSuccess    = "#10b981";
    private static readonly string ColorWarning    = "#f59e0b";
    private static readonly string ColorError      = "#ef4444";
    private static readonly string ColorInfo       = "#0ea5e9";
    private static readonly string ColorAmber      = "#f59e0b";
    private static readonly string ColorViolet     = "#8b5cf6";
    private static readonly string ColorCyan       = "#06b6d4";
    private static readonly string ColorLightViolet = "#a78bfa";

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    // ── Timezone conversion ──────────────────────────────────────────────────

    private static TimeZoneInfo ResolveTimezone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static string FormatLocal(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    private static string FormatUtcGray(DateTime utc)
        => $"(UTC {utc:HH:mm})";

    /// Renders a timestamp as: local time in primary color, then (UTC HH:mm) in gray.
    private static void ComposeTimestamp(
        IContainer container, DateTime utc, TimeZoneInfo tz,
        float fontSize = 8f)
    {
        container.Text(text =>
        {
            text.Span(FormatLocal(utc, tz))
                .FontSize(fontSize)
                .FontColor(TextSecondary);
            text.Span($"  {FormatUtcGray(utc)}")
                .FontSize(fontSize - 1f)
                .FontColor(TextTertiary);
        });
    }

    public static byte[] Generate(TicketPdfData data)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var tz = ResolveTimezone(data.ServerTimezoneId);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.DefaultTextStyle(t => t
                    .FontFamily("Helvetica")
                    .FontSize(9)
                    .FontColor(TextPrimary));
                page.PageColor(PageBg);

                page.Header().Element(ComposeHeader(data, tz));
                page.Content().Padding(20).Column(col =>
                {
                    col.Spacing(12);
                    col.Item().Element(ComposeInfoCard(data, tz));

                    if (HasSlaData(data))
                        col.Item().Element(ComposeSlaSection(data, tz));

                    col.Item().Element(ComposeDescriptionCard(data));

                    if (data.PinnedEvents.Count > 0)
                        col.Item().Element(ComposePinnedEvents(data, tz));

                    col.Item().Element(ComposeTimeline(data, tz));
                });
                page.Footer().Element(ComposeFooter(data));
            });
        }).GeneratePdf();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private static Action<IContainer> ComposeHeader(TicketPdfData data, TimeZoneInfo tz) => container =>
        container
            .Background(HeaderBg)
            .PaddingHorizontal(24)
            .PaddingVertical(16)
            .Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item()
                        .Text($"TICKET #{data.Number}")
                        .Bold()
                        .FontSize(20)
                        .FontColor(TextPrimary)
                        .LetterSpacing(0.04f);

                    col.Item()
                        .Text(data.Subject)
                        .FontSize(10)
                        .FontColor(TextSecondary)
                        .Italic();
                });

                row.AutoItem().AlignRight().Column(col =>
                {
                    col.Item().Text(text =>
                    {
                        text.Span($"Exported {FormatLocal(data.ExportedAtUtc, tz)}  ")
                            .FontSize(7.5f).FontColor(TextSecondary);
                        text.Span(FormatUtcGray(data.ExportedAtUtc))
                            .FontSize(6.5f).FontColor(TextTertiary);
                    });

                    col.Item()
                        .Text($"By {data.ExportedBy}")
                        .FontSize(7.5f)
                        .FontColor(TextTertiary);
                });
            });

    // ── Info Card ─────────────────────────────────────────────────────────────

    private static Action<IContainer> ComposeInfoCard(TicketPdfData data, TimeZoneInfo tz) => container =>
        container
            .Background(CardBg)
            .Border(0.5f)
            .BorderColor(CardBorder)
            .Padding(16)
            .Column(col =>
            {
                col.Spacing(10);

                col.Item()
                    .Text(data.Subject)
                    .SemiBold()
                    .FontSize(13)
                    .FontColor(TextPrimary);

                col.Item().Row(row =>
                {
                    // Left column
                    row.RelativeItem().Column(left =>
                    {
                        left.Spacing(6);
                        left.Item().Element(FieldBlock("QUEUE",    data.QueueName));
                        left.Item().Element(StatusBadgeBlock(data));
                        left.Item().Element(PriorityBadgeBlock(data));
                        left.Item().Element(FieldBlock("CATEGORY", data.CategoryName ?? "—"));
                    });

                    row.ConstantItem(24);

                    // Right column
                    row.RelativeItem().Column(right =>
                    {
                        right.Spacing(6);
                        right.Item().Element(FieldBlock("REQUESTER", $"{data.RequesterName} <{data.RequesterEmail}>"));
                        right.Item().Element(FieldBlock("COMPANY",   data.CompanyName ?? "—"));
                        right.Item().Element(FieldBlock("ASSIGNEE",  data.AssigneeName ?? "Unassigned"));
                        right.Item().Element(FieldBlock("SOURCE",    data.Source));
                    });
                });

                col.Item().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Column(dates =>
                    {
                        dates.Spacing(4);
                        dates.Item().Element(DateBlock("CREATED", data.CreatedUtc, tz));
                        dates.Item().Element(DateBlock("UPDATED", data.UpdatedUtc, tz));
                    });
                    row.RelativeItem().Column(dates =>
                    {
                        dates.Spacing(4);
                        if (data.DueUtc.HasValue)
                            dates.Item().Element(DateBlock("DUE", data.DueUtc.Value, tz));
                        if (data.ResolvedUtc.HasValue)
                            dates.Item().Element(DateBlock("RESOLVED", data.ResolvedUtc.Value, tz));
                        if (data.ClosedUtc.HasValue)
                            dates.Item().Element(DateBlock("CLOSED", data.ClosedUtc.Value, tz));
                    });
                });
            });

    // ── SLA Section ───────────────────────────────────────────────────────────

    private static bool HasSlaData(TicketPdfData data) =>
        data.FirstResponseDeadlineUtc.HasValue || data.ResolutionDeadlineUtc.HasValue;

    private static Action<IContainer> ComposeSlaSection(TicketPdfData data, TimeZoneInfo tz) => container =>
        container
            .Background(CardBg)
            .Border(0.5f)
            .BorderColor(CardBorder)
            .Padding(14)
            .Column(col =>
            {
                col.Spacing(8);

                col.Item().Row(row =>
                {
                    row.AutoItem()
                        .Text("SLA")
                        .Bold()
                        .FontSize(8)
                        .FontColor(AccentPurple)
                        .LetterSpacing(0.08f);

                    if (data.SlaPaused)
                    {
                        row.AutoItem().PaddingLeft(8).Background(ColorInfo).PaddingVertical(2).PaddingHorizontal(4).Text("PAUSED")
                            .Bold().FontSize(7).FontColor("#ffffff");
                    }
                });

                col.Item().Row(row =>
                {
                    row.RelativeItem().Element(SlaMetricBlock(
                        "FIRST RESPONSE",
                        data.FirstResponseDeadlineUtc,
                        data.FirstResponseMetUtc,
                        data.ExportedAtUtc, tz));

                    row.ConstantItem(24);

                    row.RelativeItem().Element(SlaMetricBlock(
                        "RESOLUTION",
                        data.ResolutionDeadlineUtc,
                        data.ResolutionMetUtc,
                        data.ExportedAtUtc, tz));
                });
            });

    private static Action<IContainer> SlaMetricBlock(
        string label, DateTime? deadline, DateTime? metAt, DateTime now, TimeZoneInfo tz) => container =>
        container.Column(col =>
        {
            col.Spacing(2);
            col.Item()
                .Text(label)
                .FontSize(7)
                .FontColor(TextTertiary)
                .LetterSpacing(0.06f);

            if (!deadline.HasValue)
            {
                col.Item().Text("No target").FontSize(8).FontColor(TextTertiary);
                return;
            }

            col.Item().Text(text =>
            {
                text.Span($"Target: {FormatLocal(deadline.Value, tz)}  ")
                    .FontSize(8).FontColor(TextSecondary);
                text.Span(FormatUtcGray(deadline.Value))
                    .FontSize(7).FontColor(TextTertiary);
            });

            if (metAt.HasValue)
            {
                col.Item().Text(text =>
                {
                    text.Span($"Met at {FormatLocal(metAt.Value, tz)}  ")
                        .FontSize(8).FontColor(ColorSuccess).Bold();
                    text.Span(FormatUtcGray(metAt.Value))
                        .FontSize(7).FontColor(TextTertiary);
                });
            }
            else if (now > deadline.Value)
            {
                col.Item().Text("BREACHED").FontSize(8).FontColor(ColorError).Bold();
            }
            else
            {
                var remaining = deadline.Value - now;
                col.Item().Text($"Due in {FormatTimespan(remaining)}")
                    .FontSize(8).FontColor(ColorWarning);
            }
        });

    // ── Description Card ──────────────────────────────────────────────────────

    private static Action<IContainer> ComposeDescriptionCard(TicketPdfData data) => container =>
        container
            .Background(CardBg)
            .Border(0.5f)
            .BorderColor(CardBorder)
            .Padding(14)
            .Column(col =>
            {
                col.Spacing(8);
                col.Item()
                    .Text("DESCRIPTION")
                    .Bold()
                    .FontSize(8)
                    .FontColor(AccentPurple)
                    .LetterSpacing(0.08f);

                var bodyText = string.IsNullOrWhiteSpace(data.BodyText)
                    ? StripHtml(data.BodyHtml ?? "")
                    : data.BodyText;

                if (string.IsNullOrWhiteSpace(bodyText))
                    bodyText = "(no description)";

                col.Item()
                    .Text(bodyText.Trim())
                    .FontSize(9)
                    .FontColor(TextPrimary)
                    .LineHeight(1.5f);
            });

    // ── Pinned Events ─────────────────────────────────────────────────────────

    private static Action<IContainer> ComposePinnedEvents(TicketPdfData data, TimeZoneInfo tz) => container =>
        container
            .Background(CardBg)
            .Border(0.5f)
            .BorderColor(CardBorder)
            .Padding(14)
            .Column(col =>
            {
                col.Spacing(8);
                col.Item()
                    .Text("PINNED EVENTS")
                    .Bold()
                    .FontSize(8)
                    .FontColor(ColorAmber)
                    .LetterSpacing(0.08f);

                foreach (var pin in data.PinnedEvents)
                {
                    col.Item()
                        .BorderLeft(2)
                        .BorderColor(ColorAmber)
                        .PaddingLeft(8)
                        .Column(inner =>
                        {
                            inner.Spacing(2);
                            inner.Item().Row(r =>
                            {
                                r.AutoItem().Text($"Event #{pin.EventId}")
                                    .FontSize(8).FontColor(TextSecondary).SemiBold();

                                if (!string.IsNullOrEmpty(pin.PinnedByName))
                                {
                                    r.AutoItem().PaddingLeft(6)
                                        .Text($"pinned by {pin.PinnedByName}")
                                        .FontSize(7.5f).FontColor(TextTertiary);
                                }

                                r.RelativeItem().AlignRight()
                                    .Element(c => ComposeTimestamp(c, pin.CreatedUtc, tz, 7.5f));
                            });

                            if (!string.IsNullOrWhiteSpace(pin.Remark))
                            {
                                inner.Item()
                                    .Text(pin.Remark)
                                    .FontSize(8.5f)
                                    .FontColor(TextPrimary);
                            }
                        });
                }
            });

    // ── Timeline ──────────────────────────────────────────────────────────────

    private static Action<IContainer> ComposeTimeline(TicketPdfData data, TimeZoneInfo tz) => container =>
        container.Column(col =>
        {
            col.Spacing(0);

            // Section divider
            col.Item().PaddingBottom(10).Row(row =>
            {
                row.RelativeItem().AlignMiddle().Background(CardBorder).Height(0.5f);
                row.AutoItem().PaddingHorizontal(10)
                    .Text("ACTIVITY")
                    .Bold()
                    .FontSize(8)
                    .FontColor(AccentPurple)
                    .LetterSpacing(0.08f);
                row.RelativeItem().AlignMiddle().Background(CardBorder).Height(0.5f);
            });

            if (data.Events.Count == 0)
            {
                col.Item().Text("No activity recorded.")
                    .FontSize(8.5f).FontColor(TextTertiary).Italic();
                return;
            }

            foreach (var evt in data.Events)
            {
                col.Item().PaddingBottom(10).Row(row =>
                {
                    // Colored dot column
                    row.ConstantItem(18).AlignTop().PaddingTop(3).Column(dotCol =>
                    {
                        dotCol.Item()
                            .Width(7).Height(7)
                            .Background(EventDotColor(evt.EventType));
                    });

                    row.RelativeItem().Column(content =>
                    {
                        content.Spacing(3);

                        // Header row: type + author + timestamp
                        content.Item().Row(hrow =>
                        {
                            hrow.AutoItem()
                                .Text(FormatEventType(evt.EventType))
                                .SemiBold()
                                .FontSize(8.5f)
                                .FontColor(EventDotColor(evt.EventType));

                            if (!string.IsNullOrEmpty(evt.AuthorName))
                            {
                                hrow.AutoItem().PaddingLeft(5)
                                    .Text($"by {evt.AuthorName}")
                                    .FontSize(8).FontColor(TextSecondary);
                            }

                            if (evt.IsInternal)
                            {
                                hrow.AutoItem().PaddingLeft(5)
                                    .Background("#1e1e30")
                                    .PaddingVertical(1).PaddingHorizontal(4)
                                    .Text("Internal")
                                    .FontSize(7).FontColor(AccentBlue);
                            }

                            hrow.RelativeItem().AlignRight()
                                .Element(c => ComposeTimestamp(c, evt.CreatedUtc, tz, 7.5f));
                        });

                        // Change metadata: "from → to"
                        var changeLine = ParseChangeLine(evt.EventType, evt.MetadataJson);
                        if (!string.IsNullOrEmpty(changeLine))
                        {
                            content.Item()
                                .Text(changeLine)
                                .FontSize(8).FontColor(TextSecondary).Italic();
                        }

                        // Body text — for mail events, prefer BodyHtml (BodyText is only a snippet)
                        var isMailEvent = evt.EventType is "MailReceived" or "Mail";
                        var bodyText = isMailEvent && !string.IsNullOrWhiteSpace(evt.BodyHtml)
                            ? StripHtml(evt.BodyHtml)
                            : evt.BodyText;
                        if (string.IsNullOrWhiteSpace(bodyText) && !string.IsNullOrWhiteSpace(evt.BodyHtml))
                            bodyText = StripHtml(evt.BodyHtml);

                        if (!string.IsNullOrWhiteSpace(bodyText))
                        {
                            content.Item()
                                .Background("#0f0f14")
                                .Padding(8)
                                .Text(bodyText.Trim())
                                .FontSize(8.5f)
                                .FontColor(TextPrimary)
                                .LineHeight(1.5f);
                        }

                        // Inline images from mail events
                        if (evt.InlineImages.Count > 0)
                        {
                            foreach (var img in evt.InlineImages)
                            {
                                try
                                {
                                    content.Item()
                                        .Background("#0f0f14")
                                        .Padding(8)
                                        .Column(imgCol =>
                                        {
                                            imgCol.Spacing(4);
                                            imgCol.Item()
                                                .MaxWidth(450)
                                                .Image(img.Data);
                                            imgCol.Item()
                                                .Text(img.Filename)
                                                .FontSize(7)
                                                .FontColor(TextTertiary)
                                                .Italic();
                                        });
                                }
                                catch
                                {
                                    // Unsupported image format — show filename only
                                    content.Item()
                                        .Text($"[Image: {img.Filename}]")
                                        .FontSize(8).FontColor(TextTertiary).Italic();
                                }
                            }
                        }

                        // File attachments from mail events (injected by MailTimelineEnricher)
                        var attachments = ParseAttachments(evt.MetadataJson);
                        if (attachments.Count > 0)
                        {
                            content.Item()
                                .Background("#0f0f14")
                                .Padding(8)
                                .Column(attCol =>
                                {
                                    attCol.Spacing(3);
                                    attCol.Item()
                                        .Text("ATTACHMENTS")
                                        .FontSize(7)
                                        .FontColor(TextTertiary)
                                        .LetterSpacing(0.06f);

                                    foreach (var att in attachments)
                                    {
                                        attCol.Item().Row(attRow =>
                                        {
                                            attRow.AutoItem()
                                                .Text("📎 ")
                                                .FontSize(8);
                                            attRow.AutoItem()
                                                .Text(att.Name)
                                                .FontSize(8.5f)
                                                .FontColor(AccentBlue);
                                            if (att.Size > 0)
                                            {
                                                attRow.AutoItem().PaddingLeft(6)
                                                    .Text(FormatFileSize(att.Size))
                                                    .FontSize(7.5f)
                                                    .FontColor(TextTertiary);
                                            }
                                        });
                                    }
                                });
                        }
                    });
                });
            }
        });

    // ── Footer ────────────────────────────────────────────────────────────────

    private static Action<IContainer> ComposeFooter(TicketPdfData data) => container =>
        container
            .BorderTop(0.5f)
            .BorderColor(CardBorder)
            .PaddingHorizontal(24)
            .PaddingVertical(8)
            .Row(row =>
            {
                row.RelativeItem()
                    .Text($"Servicedesk — Ticket #{data.Number}")
                    .FontSize(7.5f).FontColor(TextTertiary);

                row.AutoItem()
                    .Text(text =>
                    {
                        text.Span("Page ").FontSize(7.5f).FontColor(TextTertiary);
                        text.CurrentPageNumber().FontSize(7.5f).FontColor(TextTertiary);
                        text.Span(" of ").FontSize(7.5f).FontColor(TextTertiary);
                        text.TotalPages().FontSize(7.5f).FontColor(TextTertiary);
                    });
            });

    // ── Field helpers ─────────────────────────────────────────────────────────

    private static Action<IContainer> FieldBlock(string label, string value) => container =>
        container.Column(col =>
        {
            col.Item()
                .Text(label)
                .FontSize(7)
                .FontColor(TextTertiary)
                .LetterSpacing(0.06f);
            col.Item()
                .Text(value)
                .FontSize(9)
                .FontColor(TextPrimary);
        });

    private static Action<IContainer> DateBlock(string label, DateTime value, TimeZoneInfo tz) => container =>
        container.Column(col =>
        {
            col.Item()
                .Text(label)
                .FontSize(7)
                .FontColor(TextTertiary)
                .LetterSpacing(0.06f);
            col.Item().Text(text =>
            {
                text.Span(FormatLocal(value, tz))
                    .FontSize(8.5f).FontColor(TextSecondary);
                text.Span($"  {FormatUtcGray(value)}")
                    .FontSize(7f).FontColor(TextTertiary);
            });
        });

    private static Action<IContainer> StatusBadgeBlock(TicketPdfData data) => container =>
        container.Column(col =>
        {
            col.Item()
                .Text("STATUS")
                .FontSize(7)
                .FontColor(TextTertiary)
                .LetterSpacing(0.06f);
            col.Item()
                .Background(StatusColor(data.StatusCategory))
                .PaddingHorizontal(6)
                .PaddingVertical(2)
                .Text(data.StatusName)
                .Bold()
                .FontSize(8)
                .FontColor("#ffffff");
        });

    private static Action<IContainer> PriorityBadgeBlock(TicketPdfData data) => container =>
        container.Column(col =>
        {
            col.Item()
                .Text("PRIORITY")
                .FontSize(7)
                .FontColor(TextTertiary)
                .LetterSpacing(0.06f);
            col.Item()
                .Background(PriorityColor(data.PriorityLevel))
                .PaddingHorizontal(6)
                .PaddingVertical(2)
                .Text(data.PriorityName)
                .Bold()
                .FontSize(8)
                .FontColor("#ffffff");
        });

    // ── Color helpers ─────────────────────────────────────────────────────────

    private static string StatusColor(string category) => category switch
    {
        "New"      => "#6d28d9",
        "Open"     => AccentBlue,
        "Pending"  => ColorAmber,
        "Resolved" => ColorSuccess,
        "Closed"   => TextTertiary,
        _          => TextTertiary,
    };

    private static string PriorityColor(int level) => level switch
    {
        1 => ColorError,
        2 => ColorWarning,
        3 => AccentBlue,
        _ => TextTertiary,
    };

    private static string EventDotColor(string eventType) => eventType switch
    {
        "Created"          => AccentPurple,
        "Comment"          => ColorAmber,
        "Mail"             => ColorInfo,
        "MailReceived"     => ColorInfo,
        "Note"             => AccentBlue,
        "StatusChange"     => ColorSuccess,
        "AssignmentChange" => ColorViolet,
        "PriorityChange"   => ColorAmber,
        "QueueChange"      => ColorCyan,
        "CategoryChange"   => ColorLightViolet,
        "SystemNote"       => TextTertiary,
        _                  => TextSecondary,
    };

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static string FormatEventType(string eventType) => eventType switch
    {
        "Created"          => "Created",
        "Comment"          => "Comment",
        "Mail"             => "Outbound Mail",
        "MailReceived"     => "Inbound Mail",
        "Note"             => "Internal Note",
        "StatusChange"     => "Status Changed",
        "AssignmentChange" => "Assignee Changed",
        "PriorityChange"   => "Priority Changed",
        "QueueChange"      => "Queue Changed",
        "CategoryChange"   => "Category Changed",
        "SystemNote"       => "System",
        _                  => eventType,
    };

    private static string? ParseChangeLine(string eventType, string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;

        var isChangeEvent = eventType is
            "StatusChange" or "AssignmentChange" or "PriorityChange"
            or "QueueChange" or "CategoryChange";

        if (!isChangeEvent) return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            var from = TryGetString(root, "fromName") ?? TryGetString(root, "from") ?? "?";
            var to   = TryGetString(root, "toName")   ?? TryGetString(root, "to")   ?? "?";
            return $"{from} → {to}";
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement el, string propertyName)
    {
        if (el.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string StripHtml(string html)
    {
        // Replace block-level tags with newlines to preserve paragraph structure
        var withBreaks = Regex.Replace(html, @"<(br|/p|/div|/li|/tr|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        var stripped = HtmlTagRegex().Replace(withBreaks, "");
        // Decode common HTML entities
        stripped = stripped
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'");
        // Collapse multiple blank lines
        stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n");
        return stripped.Trim();
    }

    private sealed record AttachmentInfo(string Name, long Size);

    private static IReadOnlyList<AttachmentInfo> ParseAttachments(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return [];

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("attachments", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<AttachmentInfo>();
            foreach (var item in arr.EnumerateArray())
            {
                var name = TryGetString(item, "name") ?? "unnamed";
                long size = 0;
                if (item.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number)
                    size = sizeProp.GetInt64();
                list.Add(new AttachmentInfo(name, size));
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };

    private static string FormatTimespan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }
}
