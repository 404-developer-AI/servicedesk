using System.Text.Json;
using System.Xml;
using Servicedesk.Infrastructure.Sla;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetPendingTillHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;
    private readonly ISlaRepository _slaRepository;

    public SetPendingTillHandler(SystemFieldMutator mutator, ISlaRepository slaRepository)
    {
        _mutator = mutator;
        _slaRepository = slaRepository;
    }

    public string Kind => "set_pending_till";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        // Four input shapes (plus optional chain pointer on the first three):
        //   { "kind":"set_pending_till", "absolute": "2026-04-30T08:00:00Z" }
        //   { "kind":"set_pending_till", "relative": "PT4H" }                           ISO-8601 duration (calendar)
        //   { "kind":"set_pending_till", "businessDays": 2, "wakeAtLocal": "08:00" }   counts working days, snaps to local time-of-day
        //   { "kind":"set_pending_till", "clear": true }                                clears the column
        //
        // Optional companion property `nextTriggerId` (UUID) on any non-clear
        // shape: when the resulting pending-till elapses, the scheduler fires
        // ONLY this trigger for that ticket's pending-cycle (other reminder
        // triggers skip until the pointer clears). One-shot — the pointer is
        // wiped after the chained trigger runs.
        if (actionJson.TryGetProperty("clear", out var clearEl) && clearEl.ValueKind == JsonValueKind.True)
        {
            var ok = await _mutator.SetPendingTillAsync(ctx.TicketId, null, ctx.TriggerId, nextTriggerId: null, ct);
            return ok
                ? TriggerActionResult.Applied(Kind, new { cleared = true })
                : TriggerActionResult.Failed(Kind, "Ticket vanished mid-update.");
        }

        var (parsed, parseError) = await ResolveTargetUtcAsync(actionJson, ctx, ct);
        if (parseError is not null) return TriggerActionResult.Failed(Kind, parseError);
        var target = parsed!.Value;

        if (target <= ctx.UtcNow)
            return TriggerActionResult.NoOp(Kind, new { reason = "Target time is in the past; not setting pending-till.", targetUtc = target });

        var (nextTriggerId, nextError) = SetPendingTillResolver.ResolveNextTriggerId(actionJson);
        if (nextError is not null) return TriggerActionResult.Failed(Kind, nextError);

        var applied = await _mutator.SetPendingTillAsync(ctx.TicketId, target, ctx.TriggerId, nextTriggerId, ct);
        return applied
            ? TriggerActionResult.Applied(Kind, new { pendingTillUtc = target, nextTriggerId })
            : TriggerActionResult.Failed(Kind, "Ticket vanished mid-update.");
    }

    /// Shared parser used by both the handler and previewer (via
    /// <see cref="SetPendingTillResolver"/>) so dry-run results mirror
    /// real-run failure modes 1:1. Returns either a UTC target or a
    /// human-readable failure reason — never both.
    internal async Task<(DateTime? Target, string? Error)> ResolveTargetUtcAsync(
        JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
        => await SetPendingTillResolver.ResolveAsync(actionJson, ctx, _slaRepository, ct);
}

/// Pure resolver shared between handler + previewer. Static + stateless
/// except for the SLA-repository read; isolates the parsing logic from
/// the DB-mutation layer so both paths agree on what counts as a valid
/// payload.
internal static class SetPendingTillResolver
{
    public static async Task<(DateTime? Target, string? Error)> ResolveAsync(
        JsonElement actionJson, TriggerEvaluationContext ctx, ISlaRepository slaRepository, CancellationToken ct)
    {
        if (actionJson.TryGetProperty("absolute", out var absEl) && absEl.ValueKind == JsonValueKind.String)
        {
            var raw = absEl.GetString();
            if (string.IsNullOrWhiteSpace(raw)
                || !DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return (null, $"Action 'absolute' is not a parseable timestamp: '{raw}'.");
            }
            return (DateTime.SpecifyKind(parsed, DateTimeKind.Utc), null);
        }

        if (actionJson.TryGetProperty("relative", out var relEl) && relEl.ValueKind == JsonValueKind.String)
        {
            var raw = relEl.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return (null, "Action 'relative' is empty.");
            TimeSpan offset;
            try { offset = XmlConvert.ToTimeSpan(raw); }
            catch (FormatException)
            {
                return (null, $"Action 'relative' is not a valid ISO-8601 duration: '{raw}'.");
            }
            return (ctx.UtcNow + offset, null);
        }

        if (actionJson.TryGetProperty("businessDays", out var bdEl) && bdEl.ValueKind == JsonValueKind.Number)
        {
            if (!bdEl.TryGetInt32(out var businessDays) || businessDays < 0)
                return (null, "Action 'businessDays' must be a non-negative integer.");
            if (!actionJson.TryGetProperty("wakeAtLocal", out var wakeEl)
                || wakeEl.ValueKind != JsonValueKind.String
                || !TryParseTimeOfDay(wakeEl.GetString(), out var wakeAt))
            {
                return (null, "Action 'wakeAtLocal' must be a 'HH:mm' time-of-day string when 'businessDays' is set.");
            }

            var schema = await slaRepository.GetDefaultSchemaAsync(ct);
            if (schema is null)
            {
                return (null,
                    "No default business-hours schedule configured. Configure one in Settings → SLA before using businessDays mode.");
            }

            try
            {
                return (Actions.BusinessDayPendingCalculator.Resolve(
                    ctx.UtcNow, businessDays, wakeAt, schema), null);
            }
            catch (InvalidOperationException ex)
            {
                return (null, ex.Message);
            }
        }

        return (null, "Action requires 'absolute', 'relative', 'businessDays' (+ 'wakeAtLocal'), or 'clear:true'.");
    }

    /// Reads the optional `nextTriggerId` companion property. Returns
    /// (null, null) when absent (no chain), (Guid, null) when valid,
    /// (null, error) when the value is malformed.
    public static (Guid? NextTriggerId, string? Error) ResolveNextTriggerId(JsonElement actionJson)
    {
        if (!actionJson.TryGetProperty("nextTriggerId", out var el)) return (null, null);
        if (el.ValueKind == JsonValueKind.Null) return (null, null);
        if (el.ValueKind != JsonValueKind.String)
            return (null, "Action 'nextTriggerId' must be a UUID string or null.");
        var raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        if (!Guid.TryParse(raw, out var parsed))
            return (null, $"Action 'nextTriggerId' is not a valid UUID: '{raw}'.");
        return (parsed, null);
    }

    private static bool TryParseTimeOfDay(string? s, out TimeSpan ts)
    {
        ts = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Accept "H:mm" and "HH:mm" — TimeSpan.TryParseExact with two
        // formats covers both without inviting "1.00:00:00" parse-paths.
        var formats = new[] { @"h\:mm", @"hh\:mm" };
        if (TimeSpan.TryParseExact(s, formats, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
        {
            ts = parsed;
            return true;
        }
        return false;
    }
}
