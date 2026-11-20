using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// The kinds of change a sync can detect on an order. These are the values
/// of the trigger condition <c>order.change</c>, so they are a stable API —
/// never rename one.
public static class TdSynnexChangeKind
{
    public const string Status = "status";
    public const string ShipDate = "ship_date";
    public const string Shipment = "shipment";
    public const string Invoice = "invoice";
    public const string Quantity = "quantity";
    public const string Lines = "lines";

    public static readonly IReadOnlyList<string> All = new[] { Status, ShipDate, Shipment, Invoice, Quantity, Lines };
}

/// One detected change, with a human sentence for notes and the popup.
public sealed record TdSynnexOrderChange(string Kind, string Text, string? Before = null, string? After = null);

/// Everything the dashboard derives from an order's lines, computed once on
/// sync and stored in flat columns.
public sealed record TdSynnexOrderDerived(
    int LineCount, decimal QuantityTotal, DateOnly? FirstShipDate, DateOnly? EtaDate, byte[] SnapshotHash, string SearchText);

/// Pure snapshot + diff logic. The tracked state (status, line quantities,
/// open ship dates, split statuses, delivery notes, invoices) is canonicalised
/// so a reorder in the API response never counts as a change; translated
/// descriptions and prices are deliberately not tracked.
public static class TdSynnexOrderDiff
{
    public static TdSynnexOrderDerived Derive(TdSynnexOrder o)
    {
        var openDates = o.Lines
            .SelectMany(l => l.Splits)
            .Where(s => !s.IsDone && s.ShipDate is not null)
            .Select(s => s.ShipDate!.Value)
            .ToList();

        return new TdSynnexOrderDerived(
            o.Lines.Count,
            o.Lines.Sum(l => l.Quantity ?? 0m),
            openDates.Count == 0 ? null : openDates.Min(),
            openDates.Count == 0 ? null : openDates.Max(),
            Hash(o),
            BuildSearchText(o));
    }

    public static byte[] Hash(TdSynnexOrder o)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            s = o.Status,
            l = o.Lines
                .Select(l => new
                {
                    k = LineKey(l),
                    q = l.Quantity,
                    sp = l.Splits
                        .Select(SplitKey)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToArray(),
                })
                .OrderBy(x => x.k, StringComparer.Ordinal)
                .ToArray(),
        });
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    /// Changes between <paramref name="before"/> and <paramref name="after"/>.
    /// Empty when nothing tracked changed.
    public static IReadOnlyList<TdSynnexOrderChange> Compare(TdSynnexOrder before, TdSynnexOrder after)
    {
        var changes = new List<TdSynnexOrderChange>();

        if (!string.Equals(before.Status, after.Status, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(new(TdSynnexChangeKind.Status,
                $"Status {Show(before.Status)} → {Show(after.Status)}", before.Status, after.Status));
        }

        var oldLines = before.Lines.GroupBy(LineKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var newLines = after.Lines.GroupBy(LineKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (key, nl) in newLines)
        {
            var label = LineLabel(nl);
            if (!oldLines.TryGetValue(key, out var ol))
            {
                changes.Add(new(TdSynnexChangeKind.Lines, $"{label}: line added ({Qty(nl.Quantity)} pcs)"));
                continue;
            }

            if (ol.Quantity != nl.Quantity)
            {
                changes.Add(new(TdSynnexChangeKind.Quantity,
                    $"{label}: quantity {Qty(ol.Quantity)} → {Qty(nl.Quantity)}", Qty(ol.Quantity), Qty(nl.Quantity)));
            }

            var oldDates = OpenDates(ol);
            var newDates = OpenDates(nl);
            if (oldDates != newDates && newDates.Length > 0)
            {
                changes.Add(new(TdSynnexChangeKind.ShipDate,
                    $"{label}: ship date {Show(oldDates)} → {newDates}", NullIfEmpty(oldDates), newDates));
            }

            var oldStatus = StatusSummary(ol);
            var newStatus = StatusSummary(nl);
            if (oldStatus != newStatus)
            {
                changes.Add(new(TdSynnexChangeKind.Shipment,
                    $"{label}: {Show(oldStatus)} → {Show(newStatus)}", NullIfEmpty(oldStatus), NullIfEmpty(newStatus)));
            }
        }

        foreach (var (key, ol) in oldLines)
        {
            if (!newLines.ContainsKey(key))
                changes.Add(new(TdSynnexChangeKind.Lines, $"{LineLabel(ol)}: line removed"));
        }

        var newNotes = After(before, after, s => s.DeliveryNotes);
        if (newNotes.Count > 0)
        {
            changes.Add(new(TdSynnexChangeKind.Shipment,
                $"New delivery note{(newNotes.Count == 1 ? "" : "s")}: {string.Join(", ", newNotes)}", null, string.Join(", ", newNotes)));
        }

        var newInvoices = After(before, after, s => s.Invoices);
        if (newInvoices.Count > 0)
        {
            changes.Add(new(TdSynnexChangeKind.Invoice,
                $"New invoice{(newInvoices.Count == 1 ? "" : "s")}: {string.Join(", ", newInvoices)}", null, string.Join(", ", newInvoices)));
        }

        return changes;
    }

    /// One line for a note, the history list and <c>#{order.changes}</c>.
    public static string Summarize(string orderNumber, IReadOnlyList<TdSynnexOrderChange> changes) =>
        changes.Count == 0
            ? $"Order {orderNumber}: no changes"
            : $"Order {orderNumber}: " + string.Join("; ", changes.Select(c => c.Text));

    // ---- helpers ------------------------------------------------------

    private static string LineKey(TdSynnexOrderLine l) =>
        l.LineId ?? l.TdPartNumber ?? l.ManufacturerPartNumber ?? l.Description ?? string.Empty;

    private static string LineLabel(TdSynnexOrderLine l)
    {
        var part = l.ManufacturerPartNumber ?? l.TdPartNumber;
        var id = l.LineId is null ? "Line" : $"Line {l.LineId}";
        return part is null ? id : $"{id} ({part})";
    }

    private static string SplitKey(TdSynnexLineSplit s) => string.Join('|',
        Qty(s.Quantity),
        s.ShipDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? s.ShipDateRaw ?? string.Empty,
        s.Status ?? string.Empty,
        string.Join(',', s.DeliveryNotes.OrderBy(x => x, StringComparer.Ordinal)),
        string.Join(',', s.Invoices.OrderBy(x => x, StringComparer.Ordinal)));

    /// The distinct open (not yet shipped) ship dates of a line, ascending,
    /// as dd/MM/yyyy — the format TD SYNNEX and Belgian agents use.
    private static string OpenDates(TdSynnexOrderLine l) => string.Join(", ",
        l.Splits
            .Where(s => !s.IsDone && s.ShipDate is not null)
            .Select(s => s.ShipDate!.Value)
            .Distinct()
            .Order()
            .Select(d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));

    /// "2× SHIPPED, 1× OPEN" — quantities per split status, stable order.
    private static string StatusSummary(TdSynnexOrderLine l) => string.Join(", ",
        l.Splits
            .GroupBy(s => s.Status ?? "UNKNOWN", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{Qty(g.Sum(s => s.Quantity ?? 0m))}× {g.Key}"));

    private static List<string> After(TdSynnexOrder before, TdSynnexOrder after, Func<TdSynnexLineSplit, IReadOnlyList<string>> pick)
    {
        var old = before.Lines.SelectMany(l => l.Splits).SelectMany(pick).ToHashSet(StringComparer.Ordinal);
        return after.Lines.SelectMany(l => l.Splits).SelectMany(pick)
            .Where(x => !old.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildSearchText(TdSynnexOrder o)
    {
        var parts = new List<string?>
        {
            o.OrderNumber, o.CustomerOrderNumber, o.EndUserOrderNumber,
            o.ShipTo?.CompanyName, o.ShipTo?.ContactName, o.ShipTo?.City,
        };
        foreach (var l in o.Lines)
        {
            parts.Add(l.ManufacturerPartNumber);
            parts.Add(l.TdPartNumber);
            parts.Add(l.Manufacturer);
            parts.Add(l.Description);
            foreach (var s in l.Splits)
            {
                parts.AddRange(s.DeliveryNotes);
                parts.AddRange(s.Invoices);
            }
        }
        var text = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p))).ToLowerInvariant();
        return text.Length <= 8000 ? text : text[..8000];
    }

    private static string Qty(decimal? q) =>
        (q ?? 0m).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Show(string? s) => string.IsNullOrEmpty(s) ? "—" : s;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
