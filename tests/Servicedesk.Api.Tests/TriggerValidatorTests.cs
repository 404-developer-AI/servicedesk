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
        var r = TriggerValidator.Validate(
            "020 - sla warning",
            activatorKind: "time",
            activatorMode: "escalation_warning",
            "{\"op\":\"AND\",\"items\":[]}", "[]",
            locale: null,
            timezone: null);
        Assert.True(r.IsValid, r.Error);
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
}
