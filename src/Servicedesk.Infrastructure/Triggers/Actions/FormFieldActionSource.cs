using System.Text.Json;
using Servicedesk.Infrastructure.Forms;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// v0.1.58 — actions that take their value from a form field instead of a
/// fixed one: <c>set_owner</c> (an Agent field), <c>set_queue</c> (a Queue
/// field) and <c>set_pending_till</c> (a DateTime field), each with
/// <c>{"form_field": "&lt;trigger key&gt;"}</c>. Shared by handlers,
/// previewers and the save-time validator so all three agree on what a valid
/// reference is and on how a missing / empty / mistyped field ends.
internal static class FormFieldActionSource
{
    public const string Property = "form_field";

    public static bool HasFormField(JsonElement action)
        => action.ValueKind == JsonValueKind.Object
           && action.TryGetProperty(Property, out var el)
           && el.ValueKind != JsonValueKind.Null;

    /// Save-time shape check (the field's existence depends on the form, so
    /// it is checked at run time).
    public static string? Validate(JsonElement action)
    {
        if (!action.TryGetProperty(Property, out var el) || el.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(el.GetString()))
            return $"'{Property}' must be a form field's trigger key.";
        return FormTemplateValidator.TriggerKeyPattern.IsMatch(el.GetString()!)
            ? null
            : $"'{Property}' is not a valid trigger key (a-z, 0-9 and _).";
    }

    public static bool AnyActionUsesFormField(string actionsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(actionsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                   && doc.RootElement.EnumerateArray().Any(HasFormField);
        }
        catch (JsonException) { return false; }
    }

    /// Exactly one of <see cref="Field"/> (use it), <see cref="Error"/>
    /// (misconfiguration → failed action) or <see cref="EmptyReason"/> (the
    /// agent left the field empty → no-op) is set.
    public readonly record struct Resolution(TriggerFormField? Field, string? Error, string? EmptyReason);

    public static Resolution Resolve(JsonElement action, TriggerEvaluationContext ctx, string expectedType)
    {
        var key = action.TryGetProperty(Property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;
        if (ctx.ChangeSet.FormEvent is not { } form)
            return new(null, "A value from a form field is only available when a form triggers the run.", null);
        if (!form.Fields.TryGetValue(key, out var field))
            return new(null, $"The form '{form.FormName}' has no field with trigger key '{key}'.", null);
        if (!string.Equals(field.Type, expectedType, StringComparison.Ordinal))
            return new(null, $"Form field '{key}' is a {field.Type} field; this action needs a {expectedType} field.", null);
        if (field.IsEmpty)
            return new(null, null, $"Form field '{key}' is empty.");
        return new(field, null, null);
    }
}
