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
        "add_internal_note", "add_public_note", "repost_as_public_reply",
        "send_mail",
        "send_survey",
        // v0.0.39 — only valid on manual triggers; the action carries the
        // full preset (subject/body templates, defaults, requester/company
        // source, optional initial note). Allowed in the whitelist here
        // so the action-json validator passes; activator-pair check below
        // enforces that it actually ships with activator_kind=manual.
        "create_linked_ticket",
        // v0.0.42 — the gate prompt. Carries the full pop-up payload
        // (title, message, optional textarea, button labels) plus the
        // source/target status pair and the note-template applied after
        // a successful confirmation. Only valid on gate:status_change
        // triggers; the activator-pair guard below enforces the pairing.
        "prompt_confirm",
        // v0.0.42 — second gate-only action: hard-block status changes
        // when the ticket's requester contact has no entry in
        // contact_companies. The dialog renders an inline company-picker
        // with a primary/secondary/supplier role selector; submitting
        // creates the link before applying the status change. Only valid
        // on gate:status_change triggers — the activator-pair + gate-
        // action guard below reject every other combination.
        "require_contact_company",
        // Title-review — the first-open gate prompt. Carries the dialog
        // payload (title, message, editable-field label, confirm label).
        // Only valid on gate:first_open triggers; unlike the status-change
        // gate actions it may be combined with regular actions (e.g.
        // add_internal_note) that run after a successful confirmation.
        "title_review",
    };

    public static readonly IReadOnlySet<string> ActivatorPairs = new HashSet<string>(StringComparer.Ordinal)
    {
        "action:selective", "action:always",
        "time:reminder", "time:escalation", "time:escalation_warning",
        // v0.0.39 — the new "manual" activator. Sole mode for now is
        // 'linked_ticket_creator'; future manual-invocation modes (one-off
        // reports, bulk fixes, …) can be added in the same slot.
        "manual:linked_ticket_creator",
        // v0.0.42 — interactive status-change gate. The trigger is NOT
        // evaluated by TriggerService; instead the ticket PATCH endpoint
        // calls into a dedicated matcher before applying the mutation and
        // returns 409 if a gate matches without a confirmation payload.
        "gate:status_change",
        // Interactive first-open gate — the title-review prompt. Surfaced
        // by the ticket detail page's open-gate probe on first open and
        // never by the event evaluator or scheduler.
        "gate:first_open",
    };

    /// Pair the validator hands back when it has work for the caller to
    /// finish — chain-target validation needs DB access (look up each
    /// nextTriggerId) so the static <see cref="Validate"/> can only
    /// extract the candidate ids; the endpoint completes the check via
    /// <see cref="ValidateChainTargetsAsync"/>.
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

        // Gate triggers must own exactly one prompt_confirm action; the
        // matcher API + ticket-PATCH enforcement both assume a single
        // prompt-payload per matching gate. Multiple prompts on one gate
        // would surface multiple consecutive dialogs the admin cannot
        // re-order without rewriting the row.
        if (activatorKind == "gate")
        {
            var promptError = ValidateGateActions(actionsJson, activatorMode);
            if (promptError is not null) return ValidationResult.Fail(promptError);
        }
        else
        {
            // prompt_confirm, require_contact_company and title_review are
            // gate-only. Reject any on a non-gate activator so the editor
            // cannot save a malformed combination (the action-kind
            // whitelist itself accepts them).
            if (ContainsActionKind(actionsJson, "prompt_confirm"))
                return ValidationResult.Fail(
                    "prompt_confirm is only valid on gate:status_change triggers.");
            if (ContainsActionKind(actionsJson, "require_contact_company"))
                return ValidationResult.Fail(
                    "require_contact_company is only valid on gate:status_change triggers.");
            if (ContainsActionKind(actionsJson, "title_review"))
                return ValidationResult.Fail(
                    "title_review is only valid on gate:first_open triggers.");
        }

        // Time-activator triggers scan ALL tickets every tick — an empty
        // root AND matches everything, which turns a single misconfigured
        // escalation rule into a fan-out across the entire ticket table.
        // Force the admin to narrow the scope (queue, status, priority,
        // …) before saving. Reminder/escalation are caught here because
        // both walk a SQL candidate scan; action-activator triggers are
        // exempt — they're scoped to one ticket per mutation already.
        if (activatorKind == "time" && IsEmptyRootGroup(conditionsJson))
        {
            return ValidationResult.Fail(
                "time-activator triggers must include at least one condition " +
                $"(empty root '{activatorKind}:{activatorMode}' would fire on every ticket the candidate scan returns).");
        }

        var actError = ValidateActionsJson(actionsJson, timezone);
        if (actError is not null) return ValidationResult.Fail(actError);

        if (!string.IsNullOrWhiteSpace(locale))
        {
            // Cross-platform locale check. Windows NLS throws on unknown
            // names; .NET on Linux uses ICU which happily fabricates a
            // "custom culture" for any BCP-47-shaped string and never
            // throws — so a typo like "english" or "this-is-not-a-locale"
            // would slip past a try/catch on GetCultureInfo. The
            // CustomCulture LCID sentinel (0x1000 / 4096) is set by
            // both runtimes when the name doesn't resolve to a known
            // culture, which gives us one rejection path that fires on
            // both platforms.
            const int CustomCultureLcid = 0x1000;
            System.Globalization.CultureInfo? culture = null;
            try { culture = System.Globalization.CultureInfo.GetCultureInfo(locale); }
            catch (System.Globalization.CultureNotFoundException) { }
            if (culture is null || culture.LCID == CustomCultureLcid)
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

    /// Walks the actions JSON and returns the GUID values found on
    /// <c>set_pending_till.nextTriggerId</c> entries. Used by the
    /// endpoint to drive a follow-up DB lookup that confirms each
    /// chained target exists and is itself a <c>time:reminder</c>
    /// trigger. Pure-sync because the parsing has no side effects;
    /// the lookup itself happens in <see cref="ValidateChainTargetsAsync"/>.
    public static IReadOnlyList<Guid> ExtractChainedTargetIds(string actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson)) return Array.Empty<Guid>();
        List<Guid>? ids = null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(actionsJson); }
        catch (JsonException) { return Array.Empty<Guid>(); }

        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<Guid>();
            foreach (var action in doc.RootElement.EnumerateArray())
            {
                if (action.ValueKind != JsonValueKind.Object) continue;
                if (!action.TryGetProperty("kind", out var kind)
                    || kind.ValueKind != JsonValueKind.String
                    || kind.GetString() != "set_pending_till") continue;
                if (!action.TryGetProperty("nextTriggerId", out var el)
                    || el.ValueKind != JsonValueKind.String) continue;
                if (Guid.TryParse(el.GetString(), out var g))
                {
                    ids ??= new List<Guid>(2);
                    if (!ids.Contains(g)) ids.Add(g);
                }
            }
            return (IReadOnlyList<Guid>?)ids ?? Array.Empty<Guid>();
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// DB-backed completion of <see cref="Validate"/>. Confirms every
    /// chained <c>nextTriggerId</c> resolves to an existing trigger
    /// whose activator pair is <c>time:reminder</c> — chaining to an
    /// action- or escalation-trigger would fire it blindly outside its
    /// designed activator path. Self-chain (id == <paramref name="selfId"/>)
    /// against a trigger being saved as <c>time:reminder</c> is allowed
    /// without a lookup so the editor can re-arm a reminder against
    /// itself in a single save.
    public static async Task<ValidationResult> ValidateChainTargetsAsync(
        string actionsJson,
        string activatorKind,
        string activatorMode,
        Guid? selfId,
        ITriggerRepository repo,
        CancellationToken ct)
    {
        var ids = ExtractChainedTargetIds(actionsJson);
        if (ids.Count == 0) return ValidationResult.Ok();

        foreach (var id in ids)
        {
            if (selfId.HasValue && id == selfId.Value)
            {
                if (activatorKind != "time" || activatorMode != "reminder")
                    return ValidationResult.Fail(
                        $"nextTriggerId points at this trigger, but its activator is {activatorKind}:{activatorMode}; chained targets must be time:reminder.");
                continue;
            }

            var target = await repo.GetByIdAsync(id, ct);
            if (target is null)
                return ValidationResult.Fail($"nextTriggerId '{id}' does not exist.");
            if (target.ActivatorKind != "time" || target.ActivatorMode != "reminder")
                return ValidationResult.Fail(
                    $"nextTriggerId '{id}' must reference a time:reminder trigger (was {target.ActivatorKind}:{target.ActivatorMode}).");
        }
        return ValidationResult.Ok();
    }

    /// True when the conditions JSON is a group whose <c>items</c> array
    /// is empty — i.e. the matcher would short-circuit to "true" on every
    /// node it visits. Malformed JSON returns false (the structural
    /// validator above is responsible for those errors).
    private static bool IsEmptyRootGroup(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            return root.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array
                && items.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static string? ValidateActionsJson(string json, string? triggerTimezone)
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

                var payloadError = ValidateActionPayload(kindStr, action, triggerTimezone);
                if (payloadError is not null) return $"Action #{idx} ({kindStr}): {payloadError}";

                idx++;
            }
            return null;
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// Per-kind payload check. Mirrors the runtime parsing each handler
    /// does so admins get a save-time error instead of a silent-fail
    /// trigger_run row at evaluation. Keep these in sync with the
    /// matching <see cref="ITriggerActionHandler"/> implementations.
    private static string? ValidateActionPayload(string kind, JsonElement action, string? triggerTimezone)
    {
        switch (kind)
        {
            case "set_queue":
                return RequireGuid(action, "queue_id");
            case "set_priority":
                return RequireGuid(action, "priority_id");
            case "set_status":
                return RequireGuid(action, "status_id");
            case "set_owner":
                // user_id may be explicit null (clear) but the property
                // itself must be present so the handler's clear-vs-malformed
                // discrimination stays meaningful.
                return RequireGuidOrNull(action, "user_id");
            case "set_pending_till":
                return ValidateSetPendingTill(action, triggerTimezone);
            case "add_internal_note":
            case "add_public_note":
                if (HasNonEmptyString(action, "body_html") || HasNonEmptyString(action, "body_text"))
                    return null;
                return "requires non-empty 'body_html' or 'body_text'.";
            case "repost_as_public_reply":
                // Carries no admin-typed payload — the handler reads the
                // triggering article's body verbatim. Surface a clean save
                // even when admins send a stray {} so the editor doesn't
                // need to special-case empty action JSON objects.
                return null;
            case "send_mail":
                if (!HasNonEmptyString(action, "to"))
                    return "requires non-empty 'to' (e.g. customer, owner-agent, queue-agents, address:foo@bar.com).";
                if (!HasNonEmptyString(action, "subject"))
                    return "requires non-empty 'subject'.";
                if (!HasNonEmptyString(action, "body_html"))
                    return "requires non-empty 'body_html'.";
                return null;
            case "prompt_confirm":
                return ValidatePromptConfirm(action);
            case "require_contact_company":
                return ValidateRequireContactCompany(action);
            case "title_review":
                return ValidateTitleReview(action);
            case "send_survey":
                // survey_id is the only required field; subject + body live
                // on the survey row itself. TTL + recipient overrides are
                // optional and type-checked when present.
                {
                    var err = RequireGuid(action, "survey_id");
                    if (err is not null) return err;
                    if (action.TryGetProperty("ttl_days_override", out var ttlEl)
                        && ttlEl.ValueKind != JsonValueKind.Null)
                    {
                        if (ttlEl.ValueKind != JsonValueKind.Number || !ttlEl.TryGetInt32(out var ttl)
                            || ttl < 1 || ttl > 365)
                        {
                            return "'ttl_days_override' must be an integer between 1 and 365.";
                        }
                    }
                    if (action.TryGetProperty("recipient_override", out var recEl)
                        && recEl.ValueKind != JsonValueKind.Null
                        && recEl.ValueKind != JsonValueKind.String)
                    {
                        return "'recipient_override' must be a string or null.";
                    }
                }
                return null;
            default:
                // Unknown kinds are already rejected upstream — kept here
                // as a defensive null so the switch is exhaustive.
                return null;
        }
    }

    /// Action kinds a gate:status_change trigger may carry. Exactly one
    /// entry from this set must be present; nothing else is allowed.
    private static readonly IReadOnlySet<string> GateActionKinds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "prompt_confirm",
            "require_contact_company",
        };

    /// Walks a gate trigger's actions JSON and enforces the shape invariant
    /// the gate-matcher relies on, branching on the gate mode:
    ///   * status_change — exactly one status-change gate action
    ///     (<c>prompt_confirm</c> or <c>require_contact_company</c>) and
    ///     nothing else; the matcher projects a single gate per row.
    ///   * first_open — exactly one <c>title_review</c> action, optionally
    ///     followed by regular (non-gate) actions that run after a
    ///     successful confirmation. status-change gate actions are rejected.
    /// Returns null on success, else an error string the admin endpoint
    /// surfaces verbatim. Per-action payloads are type-checked by the
    /// regular ValidateActionPayload path.
    private static string? ValidateGateActions(string actionsJson, string activatorMode)
    {
        if (activatorMode == "first_open")
            return ValidateFirstOpenGateActions(actionsJson);

        const string allowedList = "prompt_confirm or require_contact_company";
        if (string.IsNullOrWhiteSpace(actionsJson))
            return $"Gate triggers require exactly one gate action ({allowedList}).";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(actionsJson); }
        catch (JsonException) { return null; }
        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return $"Gate triggers require exactly one gate action ({allowedList}).";
            int gateCount = 0;
            int total = 0;
            foreach (var action in doc.RootElement.EnumerateArray())
            {
                total++;
                if (action.ValueKind == JsonValueKind.Object
                    && action.TryGetProperty("kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && GateActionKinds.Contains(kind.GetString() ?? string.Empty))
                {
                    gateCount++;
                }
            }
            if (gateCount == 0)
                return $"Gate triggers require exactly one gate action ({allowedList}).";
            if (gateCount > 1)
                return "Gate triggers cannot carry more than one gate action.";
            if (total != gateCount)
                return $"Gate triggers can only carry gate actions ({allowedList}).";
            return null;
        }
        finally { doc.Dispose(); }
    }

    /// first_open gates need exactly one <c>title_review</c> action and may
    /// not carry the status-change gate actions. Any other action kind
    /// (add_internal_note, set_owner, …) is allowed and runs after the
    /// agent confirms — the per-kind payload is checked elsewhere.
    private static string? ValidateFirstOpenGateActions(string actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson))
            return "First-open gates require exactly one title_review action.";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(actionsJson); }
        catch (JsonException) { return null; }
        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "First-open gates require exactly one title_review action.";
            int titleReviewCount = 0;
            foreach (var action in doc.RootElement.EnumerateArray())
            {
                if (action.ValueKind != JsonValueKind.Object
                    || !action.TryGetProperty("kind", out var kind)
                    || kind.ValueKind != JsonValueKind.String)
                    continue;
                var kindStr = kind.GetString() ?? string.Empty;
                if (kindStr == "title_review") titleReviewCount++;
                else if (GateActionKinds.Contains(kindStr))
                    return $"'{kindStr}' is only valid on gate:status_change triggers.";
            }
            if (titleReviewCount == 0)
                return "First-open gates require exactly one title_review action.";
            if (titleReviewCount > 1)
                return "First-open gates cannot carry more than one title_review action.";
            return null;
        }
        finally { doc.Dispose(); }
    }

    /// Surface for "does this actions JSON include action X anywhere".
    /// Used to reject prompt_confirm on non-gate triggers at the write
    /// boundary so admins cannot smuggle a gate payload into an action-
    /// activator trigger.
    private static bool ContainsActionKind(string actionsJson, string kindName)
    {
        if (string.IsNullOrWhiteSpace(actionsJson)) return false;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(actionsJson); }
        catch (JsonException) { return false; }
        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var action in doc.RootElement.EnumerateArray())
            {
                if (action.ValueKind == JsonValueKind.Object
                    && action.TryGetProperty("kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == kindName)
                {
                    return true;
                }
            }
            return false;
        }
        finally { doc.Dispose(); }
    }

    /// Validates the per-action payload for prompt_confirm. Required:
    /// to_status_id (UUID), title (string), confirm_label (string),
    /// cancel_label (string), note_visibility ("internal"|"public"),
    /// note_template (string — may be empty so no note is appended),
    /// questions (array — may be empty for a button-only confirmation).
    /// Optional: from_status_id (UUID or null), show_message (bool),
    /// message (string — only shown when show_message is true).
    private static string? ValidatePromptConfirm(JsonElement action)
    {
        var toErr = RequireGuid(action, "to_status_id");
        if (toErr is not null) return toErr;
        if (action.TryGetProperty("from_status_id", out var fromEl)
            && fromEl.ValueKind != JsonValueKind.Null)
        {
            if (fromEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(fromEl.GetString(), out _))
                return "'from_status_id' must be a UUID string or null.";
        }
        if (!HasNonEmptyString(action, "title"))
            return "requires non-empty 'title'.";
        if (!HasNonEmptyString(action, "confirm_label"))
            return "requires non-empty 'confirm_label'.";
        if (!HasNonEmptyString(action, "cancel_label"))
            return "requires non-empty 'cancel_label'.";
        if (!action.TryGetProperty("note_visibility", out var visEl)
            || visEl.ValueKind != JsonValueKind.String
            || (visEl.GetString() != "internal" && visEl.GetString() != "public"))
        {
            return "'note_visibility' must be 'internal' or 'public'.";
        }
        if (!action.TryGetProperty("note_template", out var tplEl)
            || tplEl.ValueKind != JsonValueKind.String)
        {
            return "'note_template' must be a string (may be empty).";
        }
        if (action.TryGetProperty("show_message", out var showEl)
            && showEl.ValueKind != JsonValueKind.True
            && showEl.ValueKind != JsonValueKind.False)
        {
            return "'show_message' must be true or false when present.";
        }

        if (!action.TryGetProperty("questions", out var qEl)
            || qEl.ValueKind != JsonValueKind.Array)
        {
            return "'questions' must be an array (use [] for a confirmation-only prompt).";
        }
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        int qIdx = 0;
        foreach (var q in qEl.EnumerateArray())
        {
            var err = ValidatePromptQuestion(q, seenKeys, qIdx);
            if (err is not null) return $"questions[{qIdx}]: {err}";
            qIdx++;
        }
        return null;
    }

    /// One <c>questions[]</c> entry. <c>key</c> drives the
    /// <c>#{prompt.&lt;key&gt;}</c> token in the note template, so it must be
    /// stable / alphanumeric / unique within the action. <c>type</c> is
    /// either <c>text</c> (free-text textarea) or <c>yesno</c> (two button
    /// slots that may be hidden independently). Both button labels can
    /// be null but at least one must remain visible — otherwise the
    /// question has no UI surface.
    private static string? ValidatePromptQuestion(JsonElement q, HashSet<string> seenKeys, int idx)
    {
        if (q.ValueKind != JsonValueKind.Object)
            return "must be an object.";
        if (!q.TryGetProperty("key", out var keyEl)
            || keyEl.ValueKind != JsonValueKind.String)
            return "'key' is required (alphanumeric + underscore).";
        var key = keyEl.GetString() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9_]{0,39}$"))
            return $"'key' '{key}' must match ^[a-z][a-z0-9_]{{0,39}}$.";
        if (!seenKeys.Add(key))
            return $"duplicate key '{key}'; question keys must be unique.";
        if (!HasNonEmptyString(q, "label"))
            return "'label' is required.";
        if (!q.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String)
            return "'type' is required.";
        var type = typeEl.GetString();
        switch (type)
        {
            case "text":
                if (q.TryGetProperty("required", out var reqEl)
                    && reqEl.ValueKind != JsonValueKind.True
                    && reqEl.ValueKind != JsonValueKind.False)
                {
                    return "'required' must be true or false when present.";
                }
                return null;
            case "yesno":
                var yesVisible = HasNonEmptyString(q, "yes_label");
                var noVisible = HasNonEmptyString(q, "no_label");
                if (!yesVisible && !noVisible)
                    return "yesno questions need at least one of 'yes_label' or 'no_label' set.";
                if (q.TryGetProperty("yes_label", out var yEl)
                    && yEl.ValueKind != JsonValueKind.String
                    && yEl.ValueKind != JsonValueKind.Null)
                {
                    return "'yes_label' must be a string or null.";
                }
                if (q.TryGetProperty("no_label", out var nEl)
                    && nEl.ValueKind != JsonValueKind.String
                    && nEl.ValueKind != JsonValueKind.Null)
                {
                    return "'no_label' must be a string or null.";
                }
                return null;
            default:
                return $"unknown type '{type}'. Allowed: text, yesno.";
        }
    }

    /// Validates the per-action payload for require_contact_company.
    /// Required: to_status_id (UUID), title (string), confirm_label
    /// (string), cancel_label (string), note_visibility ("internal"|"public"),
    /// note_template (string — may be empty so no note is appended).
    /// Optional: from_status_id (UUID or null), show_message (bool),
    /// message (string — only shown when show_message is true). The role
    /// the agent picks is captured at confirmation-time; the action
    /// itself doesn't restrict which roles satisfy the gate.
    private static string? ValidateRequireContactCompany(JsonElement action)
    {
        var toErr = RequireGuid(action, "to_status_id");
        if (toErr is not null) return toErr;
        if (action.TryGetProperty("from_status_id", out var fromEl)
            && fromEl.ValueKind != JsonValueKind.Null)
        {
            if (fromEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(fromEl.GetString(), out _))
                return "'from_status_id' must be a UUID string or null.";
        }
        if (!HasNonEmptyString(action, "title"))
            return "requires non-empty 'title'.";
        if (!HasNonEmptyString(action, "confirm_label"))
            return "requires non-empty 'confirm_label'.";
        if (!HasNonEmptyString(action, "cancel_label"))
            return "requires non-empty 'cancel_label'.";
        if (!action.TryGetProperty("note_visibility", out var visEl)
            || visEl.ValueKind != JsonValueKind.String
            || (visEl.GetString() != "internal" && visEl.GetString() != "public"))
        {
            return "'note_visibility' must be 'internal' or 'public'.";
        }
        if (!action.TryGetProperty("note_template", out var tplEl)
            || tplEl.ValueKind != JsonValueKind.String)
        {
            return "'note_template' must be a string (may be empty).";
        }
        if (action.TryGetProperty("show_message", out var showEl)
            && showEl.ValueKind != JsonValueKind.True
            && showEl.ValueKind != JsonValueKind.False)
        {
            return "'show_message' must be true or false when present.";
        }
        return null;
    }

    /// Validates the per-action payload for title_review. Required: title
    /// (string — dialog heading), confirm_label (string — the approve
    /// button). Optional: field_label (string — label above the editable
    /// subject field), show_message (bool), message (string — the question
    /// shown above the field, only rendered when show_message is true).
    /// There is no status pair or note_template: the gate fires on open,
    /// and the approval note (if any) is a separate add_internal_note
    /// action on the same trigger.
    private static string? ValidateTitleReview(JsonElement action)
    {
        if (!HasNonEmptyString(action, "title"))
            return "requires non-empty 'title'.";
        if (!HasNonEmptyString(action, "confirm_label"))
            return "requires non-empty 'confirm_label'.";
        if (action.TryGetProperty("field_label", out var fieldEl)
            && fieldEl.ValueKind != JsonValueKind.String
            && fieldEl.ValueKind != JsonValueKind.Null)
        {
            return "'field_label' must be a string or null.";
        }
        if (action.TryGetProperty("show_message", out var showEl)
            && showEl.ValueKind != JsonValueKind.True
            && showEl.ValueKind != JsonValueKind.False)
        {
            return "'show_message' must be true or false when present.";
        }
        if (action.TryGetProperty("message", out var msgEl)
            && msgEl.ValueKind != JsonValueKind.String
            && msgEl.ValueKind != JsonValueKind.Null)
        {
            return "'message' must be a string or null.";
        }
        return null;
    }

    private static string? ValidateSetPendingTill(JsonElement action, string? triggerTimezone)
    {
        var hasClear = action.TryGetProperty("clear", out var clearEl)
            && clearEl.ValueKind == JsonValueKind.True;
        if (hasClear) return null;

        if (HasNonEmptyString(action, "absolute"))
        {
            var raw = action.GetProperty("absolute").GetString();
            // Delegate to the handler's parser so save-time validation
            // and runtime resolution can never disagree on whether a
            // string is a valid absolute timestamp or how a naive value
            // (no Z / +HH:MM suffix) is interpreted relative to the
            // trigger's timezone — including DST-gap rejection.
            var (_, error) = SetPendingTillResolver.ParseAbsolute(raw, triggerTimezone);
            if (error is not null) return error.Replace("Action 'absolute'", "'absolute'");
            return ValidateNextTriggerId(action);
        }

        if (HasNonEmptyString(action, "relative"))
        {
            var raw = action.GetProperty("relative").GetString();
            try { _ = System.Xml.XmlConvert.ToTimeSpan(raw!); }
            catch (FormatException) { return $"'relative' is not a valid ISO-8601 duration: '{raw}'."; }
            return ValidateNextTriggerId(action);
        }

        if (action.TryGetProperty("businessDays", out var bdEl) && bdEl.ValueKind == JsonValueKind.Number)
        {
            if (!bdEl.TryGetInt32(out var days) || days < 0)
                return "'businessDays' must be a non-negative integer.";
            if (!HasNonEmptyString(action, "wakeAtLocal"))
                return "'businessDays' requires 'wakeAtLocal' as a 'HH:mm' time-of-day string.";
            return ValidateNextTriggerId(action);
        }

        return "requires one of 'absolute', 'relative', 'businessDays' (+ 'wakeAtLocal'), or 'clear: true'.";
    }

    private static string? ValidateNextTriggerId(JsonElement action)
    {
        if (!action.TryGetProperty("nextTriggerId", out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String)
            return "'nextTriggerId' must be a UUID string or null.";
        var raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Guid.TryParse(raw, out _)
            ? null
            : $"'nextTriggerId' is not a valid UUID: '{raw}'.";
    }

    private static string? RequireGuid(JsonElement action, string property)
    {
        if (!action.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
            return $"requires string '{property}'.";
        var raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return $"'{property}' must not be empty.";
        return Guid.TryParse(raw, out _) ? null : $"'{property}' is not a valid UUID: '{raw}'.";
    }

    private static string? RequireGuidOrNull(JsonElement action, string property)
    {
        if (!action.TryGetProperty(property, out var el))
            return $"requires '{property}' (UUID string, or null to clear).";
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String)
            return $"'{property}' must be a UUID string or null.";
        var raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Guid.TryParse(raw, out _) ? null : $"'{property}' is not a valid UUID: '{raw}'.";
    }

    private static bool HasNonEmptyString(JsonElement el, string name)
        => el.TryGetProperty(name, out var prop)
           && prop.ValueKind == JsonValueKind.String
           && !string.IsNullOrEmpty(prop.GetString());
}

public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok() => new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}
