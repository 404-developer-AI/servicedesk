using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers;

/// Walks the JSONB condition tree using <see cref="JsonElement"/> directly
/// — no intermediate object-graph deserialisation. Each node is either an
/// operator-block (<c>{ "op": "AND" | "OR" | "NOT", "items": [ ... ] }</c>)
/// or a leaf (<c>{ "field": "...", "operator": "...", "value": ... }</c>).
///
/// Operators implemented in Block 2:
///   <c>is</c>, <c>is_not</c>, <c>contains</c>, <c>does_not_contain</c>,
///   <c>starts_with</c>, <c>ends_with</c>, <c>has_changed</c>,
///   <c>contains_one</c>, <c>contains_all</c>.
/// Block 3+ extends as needed for working-time, datetime, and regex
/// operators — the tree-walker is operator-table-driven.
public sealed class TriggerConditionMatcher : ITriggerConditionMatcher
{
    public bool Matches(JsonElement conditions, TriggerEvaluationContext ctx)
    {
        return Evaluate(conditions, ctx);
    }

    public IReadOnlySet<string> ReferencedFields(JsonElement conditions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(conditions, set);
        return set;
    }

    private static void Collect(JsonElement node, HashSet<string> set)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("op", out _) && node.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                Collect(item, set);
            return;
        }
        if (node.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String)
        {
            var key = field.GetString();
            if (!string.IsNullOrEmpty(key)) set.Add(key);
        }
    }

    private bool Evaluate(JsonElement node, TriggerEvaluationContext ctx)
    {
        if (node.ValueKind != JsonValueKind.Object) return false;

        if (node.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String
            && node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            return op.GetString() switch
            {
                "AND" => items.EnumerateArray().All(item => Evaluate(item, ctx)),
                "OR" => items.EnumerateArray().Any(item => Evaluate(item, ctx)),
                // NOT is treated as "none of the items match" — Zammad's
                // semantics. Empty NOT-block trivially matches.
                "NOT" => items.EnumerateArray().All(item => !Evaluate(item, ctx)),
                _ => false,
            };
        }

        if (node.TryGetProperty("field", out var fieldEl) && fieldEl.ValueKind == JsonValueKind.String
            && node.TryGetProperty("operator", out var opEl) && opEl.ValueKind == JsonValueKind.String)
        {
            var field = fieldEl.GetString()!;
            var oper = opEl.GetString()!;
            node.TryGetProperty("value", out var valueEl);
            return EvaluateLeaf(field, oper, valueEl, ctx);
        }

        return false;
    }

    private static bool EvaluateLeaf(string field, string oper, JsonElement value, TriggerEvaluationContext ctx)
    {
        // has_changed is a meta-operator — only the ChangeSet matters,
        // not the post-mutation value or the leaf 'value'. Returning the
        // truth-value here lets the same operator apply uniformly across
        // any field-key.
        if (string.Equals(oper, "has_changed", StringComparison.OrdinalIgnoreCase))
        {
            return ctx.ChangeSet.ChangedFields.Contains(field);
        }

        var actual = ResolveField(field, ctx);
        return oper.ToLowerInvariant() switch
        {
            "is" => StringEqualsLoose(actual, value),
            "is_not" => !StringEqualsLoose(actual, value),
            "contains" => StringContains(actual, value),
            "does_not_contain" => !StringContains(actual, value),
            "starts_with" => StringStartsWith(actual, value),
            "ends_with" => StringEndsWith(actual, value),
            "contains_one" => CollectionContainsOne(actual, value),
            "contains_all" => CollectionContainsAll(actual, value),
            _ => false,
        };
    }

    private static object? ResolveField(string field, TriggerEvaluationContext ctx)
    {
        return field switch
        {
            TriggerFieldKeys.TicketQueueId => ctx.Ticket.QueueId,
            TriggerFieldKeys.TicketStatusId => ctx.Ticket.StatusId,
            TriggerFieldKeys.TicketPriorityId => ctx.Ticket.PriorityId,
            TriggerFieldKeys.TicketCategoryId => ctx.Ticket.CategoryId,
            TriggerFieldKeys.TicketOwnerId => ctx.Ticket.AssigneeUserId,
            TriggerFieldKeys.TicketRequesterId => ctx.Ticket.RequesterContactId,
            TriggerFieldKeys.TicketCompanyId => ctx.Ticket.CompanyId,
            TriggerFieldKeys.TicketSubject => ctx.Ticket.Subject,
            TriggerFieldKeys.TicketTags => Array.Empty<string>(), // tags entity not introduced yet
            TriggerFieldKeys.ArticleSender => ResolveArticleSender(ctx.TriggeringEvent),
            TriggerFieldKeys.ArticleType => ctx.TriggeringEvent?.EventType,
            TriggerFieldKeys.ArticleBodyText => ctx.TriggeringEvent?.BodyText,
            TriggerFieldKeys.ArticleHasAttachments => false, // attachment-aware lookup deferred to a later block
            _ => null,
        };
    }

    private static string? ResolveArticleSender(Domain.Tickets.TicketEvent? evt)
    {
        if (evt is null) return null;
        if (evt.AuthorUserId.HasValue) return "Agent";
        if (evt.AuthorContactId.HasValue) return "Customer";
        return "System";
    }

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        Guid g => g.ToString(),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string? AsString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => el.GetRawText(),
    };

    private static bool StringEqualsLoose(object? actual, JsonElement expected)
    {
        var a = AsString(actual);
        var b = AsString(expected);
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StringContains(object? actual, JsonElement expected)
    {
        var a = AsString(actual);
        var b = AsString(expected);
        if (a is null || b is null) return false;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StringStartsWith(object? actual, JsonElement expected)
    {
        var a = AsString(actual);
        var b = AsString(expected);
        if (a is null || b is null) return false;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StringEndsWith(object? actual, JsonElement expected)
    {
        var a = AsString(actual);
        var b = AsString(expected);
        if (a is null || b is null) return false;
        return a.EndsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CollectionContainsOne(object? actual, JsonElement expected)
    {
        if (actual is not System.Collections.IEnumerable enumerable || actual is string) return false;
        if (expected.ValueKind != JsonValueKind.Array) return false;

        var actualSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in enumerable)
        {
            var s = AsString(item);
            if (s is not null) actualSet.Add(s);
        }
        foreach (var item in expected.EnumerateArray())
        {
            var s = AsString(item);
            if (s is not null && actualSet.Contains(s)) return true;
        }
        return false;
    }

    private static bool CollectionContainsAll(object? actual, JsonElement expected)
    {
        if (actual is not System.Collections.IEnumerable enumerable || actual is string) return false;
        if (expected.ValueKind != JsonValueKind.Array) return false;

        var actualSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in enumerable)
        {
            var s = AsString(item);
            if (s is not null) actualSet.Add(s);
        }
        foreach (var item in expected.EnumerateArray())
        {
            var s = AsString(item);
            if (s is null || !actualSet.Contains(s)) return false;
        }
        return true;
    }
}
