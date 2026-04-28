using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Tests for v0.0.24 Blok 6 — TriggerValidator. The validator runs at the
/// admin write boundary so admins get an actionable error instead of a
/// silent never-fires. Hot paths: activator-pair coherence, condition
/// depth cap, action-kind whitelist, and locale/timezone parse-checks.
public class TriggerValidatorTests
{
    [Fact]
    public void Valid_minimal_trigger_passes()
    {
        var r = TriggerValidator.Validate(
            name: "010 - test",
            activatorKind: "action",
            activatorMode: "selective",
            conditionsJson: "{\"op\":\"AND\",\"items\":[]}",
            actionsJson: "[]",
            locale: null,
            timezone: null);
        Assert.True(r.IsValid, r.Error);
    }

    [Fact]
    public void Empty_name_is_rejected()
    {
        var r = TriggerValidator.Validate("  ", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", "[]", null, null);
        Assert.False(r.IsValid);
        Assert.Contains("Name", r.Error!);
    }

    [Theory]
    [InlineData("action", "reminder")]      // action-kind never has time modes
    [InlineData("time", "selective")]       // time-kind never has action modes
    [InlineData("action", "always_off")]    // unknown mode
    [InlineData("foo", "selective")]        // unknown kind
    public void Mismatched_activator_pair_is_rejected(string kind, string mode)
    {
        var r = TriggerValidator.Validate("t", kind, mode, "{\"op\":\"AND\",\"items\":[]}", "[]", null, null);
        Assert.False(r.IsValid);
        Assert.Contains("activator_kind", r.Error!);
    }

    [Fact]
    public void Malformed_conditions_json_is_rejected()
    {
        var r = TriggerValidator.Validate("t", "action", "selective", "not-json", "[]", null, null);
        Assert.False(r.IsValid);
        Assert.Contains("Conditions JSON is malformed", r.Error!);
    }

    [Fact]
    public void Unknown_condition_operator_is_rejected()
    {
        const string conds = """
            {"op":"AND","items":[{"field":"ticket.subject","operator":"is_kinda","value":"x"}]}
            """;
        var r = TriggerValidator.Validate("t", "action", "selective", conds, "[]", null, null);
        Assert.False(r.IsValid);
        Assert.Contains("operator", r.Error!);
    }

    [Fact]
    public void Conditions_depth_over_max_is_rejected()
    {
        // Build a 4-level deep AND nesting (root + 3 children + 1 grandchild
        // makes depth=4 leaf; max is 3).
        const string conds = """
            {"op":"AND","items":[
              {"op":"AND","items":[
                {"op":"AND","items":[
                  {"op":"AND","items":[
                    {"field":"ticket.subject","operator":"is","value":"x"}
                  ]}
                ]}
              ]}
            ]}
            """;
        var r = TriggerValidator.Validate("t", "action", "selective", conds, "[]", null, null);
        Assert.False(r.IsValid);
        Assert.Contains("depth", r.Error!);
    }

    [Fact]
    public void Unknown_action_kind_is_rejected()
    {
        const string actions = """[{"kind":"send_sms","to":"+32"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("send_sms", r.Error!);
    }

    [Fact]
    public void Action_without_kind_is_rejected()
    {
        const string actions = """[{"queue_id":"00000000-0000-0000-0000-000000000000"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("kind", r.Error!);
    }

    [Fact]
    public void Bogus_locale_is_rejected()
    {
        var r = TriggerValidator.Validate(
            "t", "action", "selective",
            "{\"op\":\"AND\",\"items\":[]}", "[]",
            locale: "this-is-not-a-locale-12345",
            timezone: null);
        Assert.False(r.IsValid);
        Assert.Contains("Locale", r.Error!);
    }

    [Fact]
    public void Bogus_timezone_is_rejected()
    {
        var r = TriggerValidator.Validate(
            "t", "action", "selective",
            "{\"op\":\"AND\",\"items\":[]}", "[]",
            locale: null,
            timezone: "Mars/Olympus_Mons");
        Assert.False(r.IsValid);
        Assert.Contains("Timezone", r.Error!);
    }

    [Fact]
    public void Time_escalation_warning_pair_is_accepted()
    {
        // time-activator triggers need at least one condition since
        // v0.0.24 batch 3 — empty trees would fire the candidate scan
        // on every open ticket. Use the simplest narrowing condition
        // that still exercises the activator-pair acceptance path.
        const string conditions =
            "{\"op\":\"AND\",\"items\":[{\"field\":\"ticket.status\",\"operator\":\"is\",\"value\":\"Open\"}]}";
        var r = TriggerValidator.Validate(
            "020 - sla warning",
            activatorKind: "time",
            activatorMode: "escalation_warning",
            conditions, "[]",
            locale: null,
            timezone: null);
        Assert.True(r.IsValid, r.Error);
    }

    [Fact]
    public void Time_activator_rejects_empty_root_conditions()
    {
        var r = TriggerValidator.Validate(
            "020 - sla warning",
            activatorKind: "time",
            activatorMode: "escalation",
            "{\"op\":\"AND\",\"items\":[]}", "[]",
            locale: null,
            timezone: null);
        Assert.False(r.IsValid);
        Assert.Contains("at least one condition", r.Error);
    }

    [Fact]
    public void Send_mail_action_is_accepted()
    {
        const string actions = """
            [{"kind":"send_mail","to":"customer","subject":"#{ticket.subject}","body_html":"<p>hi</p>"}]
            """;
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.True(r.IsValid, r.Error);
    }

    // ---- per-kind payload validation (v0.0.24 fix batch 1) ----------

    [Fact]
    public void Set_status_without_status_id_is_rejected()
    {
        const string actions = """[{"kind":"set_status"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("status_id", r.Error!);
    }

    [Fact]
    public void Set_status_with_non_uuid_is_rejected()
    {
        const string actions = """[{"kind":"set_status","status_id":"not-a-uuid"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("UUID", r.Error!);
    }

    [Fact]
    public void Set_owner_with_explicit_null_is_accepted()
    {
        // user_id: null is the editor's "clear assignee" path — must
        // pass validation so the handler can act on the explicit null.
        const string actions = """[{"kind":"set_owner","user_id":null}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.True(r.IsValid, r.Error);
    }

    [Fact]
    public void Set_owner_without_user_id_property_is_rejected()
    {
        const string actions = """[{"kind":"set_owner"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("user_id", r.Error!);
    }

    [Fact]
    public void Send_mail_without_subject_is_rejected()
    {
        const string actions = """[{"kind":"send_mail","to":"customer","body_html":"<p>hi</p>"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("subject", r.Error!);
    }

    [Fact]
    public void Send_mail_without_body_html_is_rejected()
    {
        const string actions = """[{"kind":"send_mail","to":"customer","subject":"hi"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("body_html", r.Error!);
    }

    [Fact]
    public void Send_mail_with_empty_to_is_rejected()
    {
        const string actions = """[{"kind":"send_mail","to":"","subject":"hi","body_html":"<p>hi</p>"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("'to'", r.Error!);
    }

    [Fact]
    public void Set_pending_till_without_mode_is_rejected()
    {
        const string actions = """[{"kind":"set_pending_till"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("absolute", r.Error!);
    }

    [Fact]
    public void Set_pending_till_relative_with_invalid_duration_is_rejected()
    {
        const string actions = """[{"kind":"set_pending_till","relative":"two days"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("ISO-8601", r.Error!);
    }

    [Fact]
    public void Set_pending_till_clear_is_accepted()
    {
        const string actions = """[{"kind":"set_pending_till","clear":true}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.True(r.IsValid, r.Error);
    }

    [Fact]
    public void Set_pending_till_business_days_without_wake_at_local_is_rejected()
    {
        const string actions = """[{"kind":"set_pending_till","businessDays":2}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("wakeAtLocal", r.Error!);
    }

    [Fact]
    public void Set_pending_till_with_invalid_next_trigger_id_is_rejected()
    {
        const string actions = """[{"kind":"set_pending_till","relative":"PT4H","nextTriggerId":"not-a-uuid"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("nextTriggerId", r.Error!);
    }

    [Fact]
    public void Add_internal_note_without_body_is_rejected()
    {
        const string actions = """[{"kind":"add_internal_note"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("body", r.Error!);
    }

    [Fact]
    public void Add_public_note_with_body_text_is_accepted()
    {
        const string actions = """[{"kind":"add_public_note","body_text":"hi"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.True(r.IsValid, r.Error);
    }

    // ---- chain-target validation (v0.0.24 fix batch 2) -----------

    [Fact]
    public void ExtractChainedTargetIds_returns_unique_guids_from_set_pending_till()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var actions = $$"""
            [
              {"kind":"set_priority","priority_id":"{{Guid.NewGuid()}}"},
              {"kind":"set_pending_till","relative":"PT1H","nextTriggerId":"{{a}}"},
              {"kind":"set_pending_till","relative":"PT2H","nextTriggerId":"{{b}}"},
              {"kind":"set_pending_till","relative":"PT3H","nextTriggerId":"{{a}}"}
            ]
            """;
        var ids = TriggerValidator.ExtractChainedTargetIds(actions);
        Assert.Equal(2, ids.Count);
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
    }

    [Fact]
    public void ExtractChainedTargetIds_skips_null_or_invalid_pointers()
    {
        const string actions = """
            [
              {"kind":"set_pending_till","relative":"PT1H"},
              {"kind":"set_pending_till","relative":"PT1H","nextTriggerId":null},
              {"kind":"set_pending_till","relative":"PT1H","nextTriggerId":"not-a-uuid"},
              {"kind":"set_pending_till","relative":"PT1H","nextTriggerId":42}
            ]
            """;
        var ids = TriggerValidator.ExtractChainedTargetIds(actions);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task ValidateChainTargetsAsync_passes_when_target_is_time_reminder()
    {
        var target = NewRow("time", "reminder");
        var actions = $$"""[{"kind":"set_pending_till","relative":"PT1H","nextTriggerId":"{{target.Id}}"}]""";
        var repo = new ChainFakeRepo(target);

        var r = await TriggerValidator.ValidateChainTargetsAsync(
            actions, "action", "selective", selfId: null, repo, CancellationToken.None);

        Assert.True(r.IsValid, r.Error);
    }

    [Fact]
    public async Task ValidateChainTargetsAsync_rejects_when_target_is_action_kind()
    {
        var target = NewRow("action", "selective");
        var actions = $$"""[{"kind":"set_pending_till","relative":"PT1H","nextTriggerId":"{{target.Id}}"}]""";
        var repo = new ChainFakeRepo(target);

        var r = await TriggerValidator.ValidateChainTargetsAsync(
            actions, "action", "selective", selfId: null, repo, CancellationToken.None);

        Assert.False(r.IsValid);
        Assert.Contains("time:reminder", r.Error!);
    }

    [Fact]
    public async Task ValidateChainTargetsAsync_rejects_when_target_is_missing()
    {
        var ghost = Guid.NewGuid();
        var actions = $$"""[{"kind":"set_pending_till","relative":"PT1H","nextTriggerId":"{{ghost}}"}]""";
        var repo = new ChainFakeRepo();  // no rows

        var r = await TriggerValidator.ValidateChainTargetsAsync(
            actions, "action", "selective", selfId: null, repo, CancellationToken.None);

        Assert.False(r.IsValid);
        Assert.Contains("does not exist", r.Error!);
    }

    [Fact]
    public async Task ValidateChainTargetsAsync_allows_self_chain_when_self_is_time_reminder()
    {
        var self = Guid.NewGuid();
        var actions = $$"""[{"kind":"set_pending_till","relative":"PT1H","nextTriggerId":"{{self}}"}]""";
        var repo = new ChainFakeRepo();  // no row written for self yet

        var r = await TriggerValidator.ValidateChainTargetsAsync(
            actions, "time", "reminder", selfId: self, repo, CancellationToken.None);

        Assert.True(r.IsValid, r.Error);
    }

    [Fact]
    public async Task ValidateChainTargetsAsync_rejects_self_chain_when_self_is_not_time_reminder()
    {
        var self = Guid.NewGuid();
        var actions = $$"""[{"kind":"set_pending_till","relative":"PT1H","nextTriggerId":"{{self}}"}]""";
        var repo = new ChainFakeRepo();

        var r = await TriggerValidator.ValidateChainTargetsAsync(
            actions, "time", "escalation", selfId: self, repo, CancellationToken.None);

        Assert.False(r.IsValid);
        Assert.Contains("chained targets must be time:reminder", r.Error!);
    }

    private static TriggerRow NewRow(string kind, string mode) => new()
    {
        Id = Guid.NewGuid(),
        Name = "x",
        ActivatorKind = kind,
        ActivatorMode = mode,
        ConditionsJson = "{\"op\":\"AND\",\"items\":[]}",
        ActionsJson = "[]",
        IsActive = true,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    /// Tiny fake limited to <see cref="GetByIdAsync"/> — chain-target
    /// validation only consults the repo by id. Other methods throw so
    /// an accidental call surfaces loudly in the test output.
    private sealed class ChainFakeRepo : ITriggerRepository
    {
        private readonly Dictionary<Guid, TriggerRow> _byId;
        public ChainFakeRepo(params TriggerRow[] rows)
            => _byId = rows.ToDictionary(r => r.Id);
        public Task<TriggerRow?> GetByIdAsync(Guid triggerId, CancellationToken ct)
            => Task.FromResult(_byId.TryGetValue(triggerId, out var r) ? r : null);
        public Task<IReadOnlyList<TriggerRow>> LoadActiveAsync(TriggerActivatorKind activatorKind, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerRow>> ListAllAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow> CreateAsync(NewTrigger row, CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow?> UpdateAsync(Guid id, UpdateTrigger row, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetActiveAsync(Guid id, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, TriggerRunSummary>> GetRunSummariesAsync(DateTime sinceUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerRunDetail>> ListRunsAsync(Guid triggerId, int limit, DateTime? cursorUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task RecordRunAsync(TriggerRunRecord record, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListReminderCandidatesAsync(int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationCandidatesAsync(int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationWarningCandidatesAsync(int warningMinutes, int limit, CancellationToken ct) => throw new NotImplementedException();
    }
}
