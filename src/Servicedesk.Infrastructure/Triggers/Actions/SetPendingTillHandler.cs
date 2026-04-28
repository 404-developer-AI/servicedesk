using System.Text.Json;
using System.Text.RegularExpressions;
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

        // Read the wall clock here, not from ctx.UtcNow — ctx.UtcNow is
        // captured at trigger-pass start and a slow earlier action in the
        // same trigger (e.g. send_mail with seconds of Graph latency) can
        // make a small relative offset land in the past by the time we
        // get here. The resolver uses the same fresh-now for relative /
        // businessDays so this check stays a defensive guard against
        // absolute timestamps that were already historical at parse time.
        if (target <= DateTime.UtcNow)
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
            return ParseAbsolute(absEl.GetString(), ctx);
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
            // Anchor on a fresh wall-clock read rather than ctx.UtcNow:
            // a slow earlier action in the same trigger can age ctx.UtcNow
            // by seconds, which silently turns a small relative offset
            // (PT1S, PT5S) into a past target → NoOp.
            return (DateTime.UtcNow + offset, null);
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
                // Same rationale as the relative branch — base the
                // calendar walk on a fresh wall-clock read so a slow
                // earlier action doesn't shift the resolved target into
                // yesterday's working day.
                return (Actions.BusinessDayPendingCalculator.Resolve(
                    DateTime.UtcNow, businessDays, wakeAt, schema), null);
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

    /// Distinguishes "2026-04-30T08:00:00" (naive — interpret in the
    /// trigger's timezone) from "2026-04-30T08:00:00Z" /
    /// "2026-04-30T08:00:00+02:00" (offset-aware — already an absolute
    /// moment). Naive without a trigger timezone falls back to UTC so
    /// existing seeded triggers stay backwards-compatible.
    private static readonly Regex HasOffsetSuffix = new(
        @"(?:[Zz]|[+-]\d{2}:?\d{2})\s*$",
        RegexOptions.Compiled);

    internal static (DateTime? Target, string? Error) ParseAbsolute(
        string? raw, TriggerEvaluationContext ctx)
        => ParseAbsolute(raw, ctx.RenderContext?.DefaultTimeZoneId);

    /// Variant used by <see cref="TriggerValidator"/> at write-time, when
    /// only the trigger's persisted timezone string is available (no
    /// render-context yet). Keeping a single implementation guarantees
    /// the runtime handler and the save-time validator agree on which
    /// strings are valid <c>absolute</c> timestamps and how naive ones
    /// are interpreted — without this, an admin can save a value the
    /// validator accepts as UTC and watch the runtime resolve it shifted
    /// by the trigger's timezone offset.
    internal static (DateTime? Target, string? Error) ParseAbsolute(
        string? raw, string? tzId)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "Action 'absolute' is empty.");

        if (HasOffsetSuffix.IsMatch(raw))
        {
            if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dto))
            {
                return (null, $"Action 'absolute' is not a parseable timestamp: '{raw}'.");
            }
            return (dto.UtcDateTime, null);
        }

        // Naive — wall-clock in the trigger's timezone (or UTC fallback
        // when no timezone is set on the trigger).
        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed)
            || parsed.Kind != DateTimeKind.Unspecified)
        {
            return (null, $"Action 'absolute' is not a parseable timestamp: '{raw}'.");
        }

        if (string.IsNullOrWhiteSpace(tzId))
            return (DateTime.SpecifyKind(parsed, DateTimeKind.Utc), null);

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch (TimeZoneNotFoundException)
        {
            return (DateTime.SpecifyKind(parsed, DateTimeKind.Utc), null);
        }
        catch (InvalidTimeZoneException)
        {
            return (DateTime.SpecifyKind(parsed, DateTimeKind.Utc), null);
        }

        if (tz.IsInvalidTime(parsed))
            return (null, $"Action 'absolute' '{raw}' is in the DST gap for {tz.Id}.");

        var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(unspecified, tz), null);
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
