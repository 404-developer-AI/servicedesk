namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// One TD SYNNEX order as parsed from <c>GET /resellers/v1/orders</c>
/// (IncludeDetails=true). Every field is optional except the order number —
/// the parser is deliberately tolerant (see <see cref="TdSynnexOrderParser"/>).
public sealed record TdSynnexOrder
{
    public required string OrderNumber { get; init; }
    public string? CustomerOrderNumber { get; init; }
    public string? EndUserOrderNumber { get; init; }
    public string? OrderType { get; init; }
    public string? Status { get; init; }
    public string? StatusDetail { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public decimal? TotalNet { get; init; }
    public string? Currency { get; init; }
    public TdSynnexShipTo? ShipTo { get; init; }
    public TdSynnexPayment? Payment { get; init; }
    public IReadOnlyList<TdSynnexOrderLine> Lines { get; init; } = Array.Empty<TdSynnexOrderLine>();
}

public sealed record TdSynnexShipTo
{
    public string? CompanyName { get; init; }
    public string? ContactName { get; init; }
    public string? ContactEmail { get; init; }
    public string? Phone { get; init; }
    public string? Line1 { get; init; }
    public string? Line2 { get; init; }
    public string? Line3 { get; init; }
    public string? Zip { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
}

public sealed record TdSynnexPayment
{
    public decimal? Subtotal { get; init; }
    public decimal? Tax { get; init; }
    public decimal? Freight { get; init; }
    public decimal? OtherFees { get; init; }
    public decimal? Total { get; init; }
    public string? Currency { get; init; }
    public string? PaymentTerms { get; init; }
}

public sealed record TdSynnexOrderLine
{
    public string? LineId { get; init; }
    public string? TdPartNumber { get; init; }
    public string? ManufacturerPartNumber { get; init; }
    public string? Manufacturer { get; init; }
    public string? Description { get; init; }
    public bool IsEol { get; init; }
    public decimal? UnitNetPrice { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? TotalNetPrice { get; init; }
    public IReadOnlyList<TdSynnexLineSplit> Splits { get; init; } = Array.Empty<TdSynnexLineSplit>();
}

/// One split quantity of a line (<c>lineDetails[]</c>): how many units ship
/// on which date, with which status, and the delivery notes / invoices that
/// already exist for it.
public sealed record TdSynnexLineSplit
{
    public decimal? Quantity { get; init; }
    public decimal? TotalNetPrice { get; init; }
    /// The ship date as a calendar date (time-of-day dropped). Null when
    /// absent or in a format the parser does not recognise.
    public DateOnly? ShipDate { get; init; }
    /// The raw <c>shipDateValue</c>, kept so an unrecognised format is still
    /// visible to the agent.
    public string? ShipDateRaw { get; init; }
    public string? ShipDateDetails { get; init; }
    public string? Status { get; init; }
    public string? StatusDetail { get; init; }
    public IReadOnlyList<string> DeliveryNotes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Invoices { get; init; } = Array.Empty<string>();

    /// True once this quantity has left TD SYNNEX (or will not ship at all):
    /// a shipped/delivered/invoiced/cancelled status, or a delivery note.
    public bool IsDone =>
        DeliveryNotes.Count > 0
        || TdSynnexStatus.IsDoneSplitStatus(Status);
}

/// Status vocabulary. Order statuses are the documented enum; split statuses
/// are free text in the spec (the example shows <c>SHIPPED</c>), so the
/// "done" set is a tolerant superset.
public static class TdSynnexStatus
{
    public const string Open = "OPEN";
    public const string Investigation = "INVESTIGATION";
    public const string Shipping = "SHIPPING";
    public const string Rejected = "REJECTED";
    public const string Completed = "COMPLETED";

    /// Statuses the active refresh re-reads (and the dashboard counts as open).
    public static readonly IReadOnlyList<string> Active = new[] { Open, Investigation, Shipping };

    public static readonly IReadOnlyList<string> All = new[] { Open, Investigation, Shipping, Rejected, Completed };

    public static bool IsActive(string? status) =>
        status is not null && Active.Contains(status.Trim().ToUpperInvariant());

    private static readonly HashSet<string> DoneSplit = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHIPPED", "DELIVERED", "INVOICED", "COMPLETED", "CLOSED", "CANCELLED", "CANCELED", "REJECTED",
    };

    public static bool IsDoneSplitStatus(string? status) =>
        status is not null && DoneSplit.Contains(status.Trim());
}

/// One page of the order list.
public sealed record TdSynnexOrderPage(
    IReadOnlyList<TdSynnexOrder> Orders, int Page, int TotalPages, int TotalResults, string? ListKey);
