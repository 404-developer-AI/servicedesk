using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Triggers;

namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// Builds the <see cref="TriggerOrderEvent"/> an order pass carries, and the
/// simulated one the trigger test runner uses. No dependency on the trigger
/// service itself (it is one of its dependencies).
public interface ITdSynnexOrderEvents
{
    Task<TriggerOrderEvent> BuildAsync(
        Guid orderId, TdSynnexOrder order, string? previousStatus,
        IReadOnlyList<TdSynnexOrderChange> changes, DateOnly? firstShipDate, DateOnly? etaDate, CancellationToken ct);

    /// The most recently changed order linked to the ticket, replayed as if
    /// its last recorded change just happened (or a status "change" to its
    /// current status when it never changed). Null when nothing is linked.
    Task<TriggerOrderEvent?> SimulateForTicketAsync(Guid ticketId, CancellationToken ct);
}

public sealed class TdSynnexOrderEvents : ITdSynnexOrderEvents
{
    private readonly ITdSynnexOrderRepository _repo;
    private readonly ISettingsService _settings;

    public TdSynnexOrderEvents(ITdSynnexOrderRepository repo, ISettingsService settings)
    {
        _repo = repo;
        _settings = settings;
    }

    /// "Today" on the server's display time zone — the day an open ship date
    /// before which counts as late. Never the client clock.
    public static async Task<DateOnly> TodayAsync(ISettingsService settings, CancellationToken ct)
    {
        var tz = await HealthTimeFormat.ResolveDisplayZoneAsync(settings, ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
    }

    public static bool IsLate(string? status, DateOnly? firstShipDate, DateOnly today) =>
        TdSynnexStatus.IsActive(status) && firstShipDate is { } d && d < today;

    public async Task<TriggerOrderEvent> BuildAsync(
        Guid orderId, TdSynnexOrder order, string? previousStatus,
        IReadOnlyList<TdSynnexOrderChange> changes, DateOnly? firstShipDate, DateOnly? etaDate, CancellationToken ct)
    {
        var today = await TodayAsync(_settings, ct);
        return new TriggerOrderEvent(
            orderId,
            order.OrderNumber,
            order.CustomerOrderNumber,
            order.Status,
            previousStatus,
            changes.Select(c => c.Kind).ToHashSet(StringComparer.OrdinalIgnoreCase),
            changes.Select(c => c.Text).ToList(),
            etaDate,
            IsLate(order.Status, firstShipDate, today),
            order.ShipTo?.CompanyName);
    }

    public async Task<TriggerOrderEvent?> SimulateForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        var linked = await _repo.ListForTicketAsync(ticketId, changeLimit: 1, ct);
        var pick = linked
            .OrderByDescending(d => d.Header.LastChangedUtc ?? DateTime.MinValue)
            .ThenByDescending(d => d.Header.OrderCreatedUtc ?? DateTime.MinValue)
            .FirstOrDefault();
        if (pick is null) return null;

        var h = pick.Header;
        IReadOnlyList<TdSynnexOrderChange> changes = Array.Empty<TdSynnexOrderChange>();
        if (pick.Changes.FirstOrDefault() is { } last)
        {
            try
            {
                changes = JsonSerializer.Deserialize<List<TdSynnexOrderChange>>(last.ChangesJson, TdSynnexOrderRepository.Json)
                          ?? new List<TdSynnexOrderChange>();
            }
            catch (JsonException) { /* fall through to the synthetic change */ }
        }
        if (changes.Count == 0)
            changes = new[] { new TdSynnexOrderChange(TdSynnexChangeKind.Status, $"Status {h.Status ?? "—"}", null, h.Status) };

        var previous = changes.FirstOrDefault(c => c.Kind == TdSynnexChangeKind.Status)?.Before;
        var order = new TdSynnexOrder
        {
            OrderNumber = h.OrderNumber,
            CustomerOrderNumber = h.CustomerOrderNumber,
            Status = h.Status,
            ShipTo = new TdSynnexShipTo { CompanyName = h.ShipToName },
        };
        return await BuildAsync(h.Id, order, previous, changes,
            h.FirstShipDate is { } f ? DateOnly.FromDateTime(f) : null,
            h.EtaDate is { } e ? DateOnly.FromDateTime(e) : null, ct);
    }
}

/// Runs the order pass on every ticket linked to a changed order and pushes
/// <c>TicketUpdated</c> so an open ticket refreshes its order chip. One
/// failing ticket never stops the others.
public interface ITdSynnexOrderTicketFanout
{
    Task RunAsync(TriggerOrderEvent evt, CancellationToken ct);
}

public sealed class TdSynnexOrderTicketFanout : ITdSynnexOrderTicketFanout
{
    private readonly ITdSynnexOrderRepository _repo;
    private readonly ITriggerService _triggers;
    private readonly ITicketListNotifier _notifier;
    private readonly ILogger<TdSynnexOrderTicketFanout> _logger;

    public TdSynnexOrderTicketFanout(
        ITdSynnexOrderRepository repo,
        ITriggerService triggers,
        ITicketListNotifier notifier,
        ILogger<TdSynnexOrderTicketFanout> logger)
    {
        _repo = repo;
        _triggers = triggers;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task RunAsync(TriggerOrderEvent evt, CancellationToken ct)
    {
        var ticketIds = await _repo.GetLinkedTicketIdsAsync(evt.OrderId, ct);
        foreach (var ticketId in ticketIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _triggers.EvaluateAsync(
                    ticketId, ticketEventId: null, TriggerActivatorKind.Action,
                    TriggerChangeSet.ForOrder(evt), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Order trigger pass for order {OrderId} on ticket {TicketId} failed.", evt.OrderId, ticketId);
            }

            try { await _notifier.NotifyUpdatedAsync(ticketId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TicketUpdated push for ticket {TicketId} failed.", ticketId);
            }
        }
    }
}
