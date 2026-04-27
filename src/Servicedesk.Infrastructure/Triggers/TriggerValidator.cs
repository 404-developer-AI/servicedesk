using System.Text.Json;
using Servicedesk.Infrastructure.Triggers.Actions;

namespace Servicedesk.Infrastructure.Triggers;

/// Validates an admin-submitted trigger payload before it hits the DB.
/// The evaluator already fails-open on malformed JSON at runtime; the
/// validator tightens that up at the write boundary so admins get an
/// actionable error instead of a silent no-match. Keep this in sync with
/// <see cref="TriggerConditionMatcher"/> (operators + fields) and the
/// registered <see cref="ITriggerActionHandler"/>s — adding either at the
/// matcher / handler layer means adding one entry here.
public static class TriggerValidator
{
    public const int MaxConditionDepth = 3;

    public static readonly IReadOnlySet<string> ConditionOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "is", "is_not",
        "contains", "does_not_contain",
        "starts_with", "ends_with",
        "has_changed",
        "contains_one", "contains_all",
    };

    /// Action kinds the dispatcher currently has a handler for. The
    /// evaluator marks unknown kinds as <c>NoHandler</c> at runtime, but
    /// rejecting them at write-time prevents an admin from saving a
    /// trigger that will never apply (and saves a row in trigger_runs).
    /// add_tags / remove_tags are intentionally absent — the dispatcher
    /// returns NoHandler with a clear message until the tags schema lands.
    public static readonly IReadOnlySet<string> ActionKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "set_queue", "set_priority", "set_status", "set_owner", "set_pending_till",
        "add_internal_note", "add_public_note",
        "send_mail",
    };

    public static readonly IReadOnlySet<string> ActivatorPairs = new HashSet<string>(StringComparer.Ordinal)
    {
        "action:selective", "action:always",
        "time:reminder", "time:escalation", "time:escalation_warning",
    };

    public static ValidationResult Validate(
        string name,
        string activatorKind,
        string activatorMode,
        string conditionsJson,
        string actionsJson,
        string? locale,
        string? timezone)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ValidationResult.Fail("Name is required.");
        if (name.Length > 200)
            return ValidationResult.Fail("Name must be 200 characters or fewer.");

        if (!ActivatorPairs.Contains($"{activatorKind}:{activatorMode}"))
            return ValidationResult.Fail(
                $"activator_kind '{activatorKind}' is not compatible with activator_mode '{activatorMode}'.");

        var condError = ValidateConditionsJson(conditionsJson);
        if (condError is not null) return ValidationResult.Fail(condError);

        var actError = ValidateActionsJson(actionsJson);
        if (actError is not null) return ValidationResult.Fail(actError);

        if (!string.IsNullOrWhiteSpace(locale))
        {
            try { _ = System.Globalization.CultureInfo.GetCultureInfo(locale); }
            catch (System.Globalization.CultureNotFoundException)
            {
                return ValidationResult.Fail($"Locale '{locale}' is not a recognised culture.");
            }
        }

        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(timezone); }
            catch (TimeZoneNotFoundException)
            {
                return ValidationResult.Fail($"Timezone '{timezone}' is not a recognised IANA / Windows id.");
            }
            catch (InvalidTimeZoneException)
            {
                return ValidationResult.Fail($"Timezone '{timezone}' is malformed.");
            }
        }

        return ValidationResult.Ok();
    }

    private static string? ValidateConditionsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "Conditions JSON is required (use {\"op\":\"AND\",\"items\":[]} for no conditions).";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return $"Conditions JSON is malformed: {ex.Message}"; }

        try
        {
            return WalkConditionNode(doc.RootElement, depth: 0);
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static string? WalkConditionNode(JsonElement node, int depth)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return "Condition node must be an object.";

        if (node.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String
            && node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var opStr = op.GetString();
            if (opStr is not ("AND" or "OR" or "NOT"))
                return $"Unknown operator '{opStr}'. Allowed: AND, OR, NOT.";
            if (depth >= MaxConditionDepth)
                return $"Condition tree exceeds max depth of {MaxConditionDepth}.";
            foreach (var item in items.EnumerateArray())
            {
                var err = WalkConditionNode(item, depth + 1);
                if (err is not null) return err;
            }
            return null;
        }

        if (!node.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(field.GetString()))
            return "Leaf condition is missing 'field'.";
        if (!node.TryGetProperty("operator", out var lop) || lop.ValueKind != JsonValueKind.String)
            return "Leaf condition is missing 'operator'.";
        if (!ConditionOperators.Contains(lop.GetString() ?? string.Empty))
            return $"Unknown condition operator '{lop.GetString()}'.";
        return null;
    }

    private static string? ValidateActionsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "Actions JSON is required (use [] for no actions).";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return $"Actions JSON is malformed: {ex.Message}"; }

        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "Actions JSON must be an array.";
            int idx = 0;
            foreach (var action in doc.RootElement.EnumerateArray())
            {
                if (action.ValueKind != JsonValueKind.Object)
                    return $"Action #{idx} must be an object.";
                if (!action.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                    return $"Action #{idx} is missing 'kind'.";
                var kindStr = kind.GetString() ?? string.Empty;
                if (!ActionKinds.Contains(kindStr))
                    return $"Action #{idx}: kind '{kindStr}' is not registered. Allowed: {string.Join(", ", ActionKinds)}.";
                idx++;
            }
            return null;
        }
        finally
        {
            doc.Dispose();
        }
    }
}

public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok() => new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}
