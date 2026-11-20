using System.Globalization;
using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// Pure JSON → model mapping for the TD SYNNEX order list. The published
/// OpenAPI spec contradicts its own examples (list under <c>data</c> vs
/// <c>orders</c>; <c>items</c> an object vs an array; a non-ISO
/// <c>shipDateValue</c> like <c>05/12/2025 21:00:00</c>), so every shape the
/// spec allows is accepted. An order without an order number is skipped —
/// it cannot be keyed.
public static class TdSynnexOrderParser
{
    private static readonly string[] ShipDateFormats =
    {
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy H:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
        "d/M/yyyy HH:mm:ss", "d/M/yyyy",
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
        "dd.MM.yyyy", "dd-MM-yyyy",
    };

    public static TdSynnexOrderPage ParsePage(JsonElement root)
    {
        string? listKey = null;
        JsonElement list = default;
        foreach (var key in new[] { "data", "orders" })
        {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(key, out var candidate)
                && candidate.ValueKind == JsonValueKind.Array)
            {
                listKey = key;
                list = candidate;
                break;
            }
        }

        var orders = new List<TdSynnexOrder>();
        if (listKey is not null)
        {
            foreach (var o in list.EnumerateArray())
            {
                var parsed = ParseOrder(o);
                if (parsed is not null) orders.Add(parsed);
            }
        }

        return new TdSynnexOrderPage(
            orders,
            Int(root, "page") ?? 0,
            Int(root, "totalPages") ?? 0,
            Int(root, "totalResults") ?? 0,
            listKey);
    }

    public static TdSynnexOrder? ParseOrder(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        var number = Str(o, "tdsynnexOrderNumber")?.Trim();
        if (string.IsNullOrEmpty(number)) return null;

        var lines = new List<TdSynnexOrderLine>();
        if (o.TryGetProperty("items", out var items))
        {
            // Spec schema: object; spec examples: array. Accept both.
            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                    if (ParseLine(it) is { } line) lines.Add(line);
            }
            else if (ParseLine(items) is { } single)
            {
                lines.Add(single);
            }
        }

        return new TdSynnexOrder
        {
            OrderNumber = Cap(number, 100)!,
            CustomerOrderNumber = Cap(Str(o, "customerOrderNumber"), 200),
            EndUserOrderNumber = Cap(Str(o, "endUserOrderNumber"), 200),
            OrderType = Cap(Str(o, "orderType"), 100),
            Status = Cap(Str(o, "orderStatus")?.Trim().ToUpperInvariant(), 50),
            StatusDetail = Cap(Obj(o, "statusDetails") is { } sd ? Str(sd, "description") : null, 200),
            CreatedBy = Cap(Str(o, "createdBy"), 200),
            CreatedUtc = Utc(Str(o, "creationDate")),
            ModifiedUtc = Utc(Str(o, "modificationDate")),
            TotalNet = Dec(o, "totalOrderNetPrice"),
            Currency = Cap(Str(o, "currency"), 10),
            ShipTo = Obj(o, "shipTo") is { } st ? ParseShipTo(st) : null,
            Payment = Obj(o, "paymentDetails") is { } pd ? ParsePayment(pd) : null,
            Lines = lines,
        };
    }

    private static TdSynnexOrderLine? ParseLine(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object) return null;
        var splits = new List<TdSynnexLineSplit>();
        if (it.TryGetProperty("lineDetails", out var details))
        {
            if (details.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in details.EnumerateArray())
                    if (ParseSplit(d) is { } s) splits.Add(s);
            }
            else if (ParseSplit(details) is { } single)
            {
                splits.Add(single);
            }
        }

        return new TdSynnexOrderLine
        {
            LineId = Cap(Str(it, "lineId"), 50),
            TdPartNumber = Cap(Str(it, "tdsynnexPartNumber"), 100),
            ManufacturerPartNumber = Cap(Str(it, "manufPartNumber"), 100),
            Manufacturer = Cap(Str(it, "manufacturer"), 200),
            Description = Cap(Str(it, "description"), 1000),
            IsEol = it.TryGetProperty("isEOL", out var eol) && eol.ValueKind == JsonValueKind.True,
            UnitNetPrice = Dec(it, "unitNetPrice"),
            Quantity = Dec(it, "lineQuantity"),
            TotalNetPrice = Dec(it, "totalLineNetPrice"),
            Splits = splits,
        };
    }

    private static TdSynnexLineSplit? ParseSplit(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return null;
        var raw = Str(d, "shipDateValue")?.Trim();
        return new TdSynnexLineSplit
        {
            Quantity = Dec(d, "quantity"),
            TotalNetPrice = Dec(d, "totalNetPrice"),
            ShipDate = ParseShipDate(raw),
            ShipDateRaw = Cap(raw, 50),
            ShipDateDetails = Cap(Str(d, "shipDateDetails"), 200),
            Status = Cap(Str(d, "status")?.Trim().ToUpperInvariant(), 50),
            StatusDetail = Cap(Obj(d, "lineStatusDetails") is { } ls ? Str(ls, "description") : null, 200),
            DeliveryNotes = StrList(d, "deliveryNotes"),
            Invoices = StrList(d, "invoices"),
        };
    }

    private static TdSynnexShipTo ParseShipTo(JsonElement st) => new()
    {
        CompanyName = Cap(Str(st, "companyName"), 200),
        ContactName = Cap(Str(st, "contactName"), 200),
        ContactEmail = Cap(Str(st, "contactEmail"), 320),
        Phone = Cap(Str(st, "phoneNumber"), 50),
        Line1 = Cap(JoinNonEmpty(Str(st, "line1"), Str(st, "houseNumber")), 300),
        Line2 = Cap(Str(st, "line2"), 300),
        Line3 = Cap(Str(st, "line3"), 300),
        Zip = Cap(Str(st, "zip") ?? Str(st, "postalCode"), 20),
        City = Cap(Str(st, "city"), 200),
        Country = Cap(Str(st, "country"), 10),
    };

    private static TdSynnexPayment ParsePayment(JsonElement pd) => new()
    {
        Subtotal = Dec(pd, "subtotal") ?? Dec(pd, "netValue"),
        Tax = Dec(pd, "tax"),
        Freight = Dec(pd, "freight"),
        OtherFees = Dec(pd, "otherFees"),
        Total = Dec(pd, "total"),
        Currency = Cap(Str(pd, "currency"), 10),
        PaymentTerms = Cap(Str(pd, "paymentTermText"), 200),
    };

    /// The calendar date of a <c>shipDateValue</c>. TD SYNNEX writes
    /// day-first (<c>05/12/2025 21:00:00</c>); ISO dates are accepted too.
    public static DateOnly? ParseShipDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTime.TryParseExact(raw.Trim(), ShipDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var dt))
        {
            return DateOnly.FromDateTime(dt);
        }
        // An ISO timestamp with an offset or fractional seconds.
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            && raw.Contains('-'))
        {
            return DateOnly.FromDateTime(dto.UtcDateTime);
        }
        return null;
    }

    private static DateTime? Utc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime
            : null;
    }

    // ---- primitive readers --------------------------------------------

    private static JsonElement? Obj(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static int? Int(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static decimal? Dec(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var ds))
            return ds;
        return null;
    }

    private static IReadOnlyList<string> StrList(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var x in v.EnumerateArray())
        {
            var s = x.ValueKind switch
            {
                JsonValueKind.String => x.GetString(),
                JsonValueKind.Number => x.GetRawText(),
                // Tolerate { "number": "…" } / { "id": "…" } objects.
                JsonValueKind.Object => Str(x, "number") ?? Str(x, "id"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(s) && list.Count < 50) list.Add(Cap(s.Trim(), 100)!);
        }
        return list;
    }

    private static string? JoinNonEmpty(string? a, string? b) =>
        string.IsNullOrWhiteSpace(b) ? a : string.IsNullOrWhiteSpace(a) ? b : $"{a} {b}";

    private static string? Cap(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
