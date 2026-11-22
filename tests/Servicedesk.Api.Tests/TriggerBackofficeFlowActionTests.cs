using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.61 — save-time validation of the two trigger actions behind the
/// back-office intervention flow: <c>set_category</c> (set or clear the
/// phase marker) and <c>handle_backoffice_form</c> (handle the open forms of
/// chosen templates on the ticket).
public class TriggerBackofficeFlowActionTests
{
    private const string Templ = "6a1b2c3d-0000-4000-8000-00000000a001";

    private static ValidationResult Run(string actionsJson) => TriggerValidator.Validate(
        "t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actionsJson, null, null);

    [Theory]
    [InlineData("[{\"kind\":\"set_category\",\"category_id\":\"6a1b2c3d-0000-4000-8000-0000000000c1\"}]")]
    [InlineData("[{\"kind\":\"set_category\",\"category_id\":null}]")]
    public void Set_category_accepts_an_id_or_an_explicit_clear(string json)
        => Assert.True(Run(json).IsValid, Run(json).Error);

    [Theory]
    [InlineData("[{\"kind\":\"set_category\"}]")]
    [InlineData("[{\"kind\":\"set_category\",\"category_id\":\"not-a-guid\"}]")]
    public void Set_category_without_a_valid_target_is_rejected(string json)
        => Assert.False(Run(json).IsValid);

    [Fact]
    public void Handle_backoffice_form_with_templates_and_a_note_passes()
    {
        var r = Run($"[{{\"kind\":\"handle_backoffice_form\",\"template_ids\":[\"{Templ}\"],\"note\":\"Planned on #{{form.event}}\"}}]");
        Assert.True(r.IsValid, r.Error);
    }

    [Theory]
    [InlineData("[{\"kind\":\"handle_backoffice_form\"}]")]
    [InlineData("[{\"kind\":\"handle_backoffice_form\",\"template_ids\":[]}]")]
    [InlineData("[{\"kind\":\"handle_backoffice_form\",\"template_ids\":[\"nope\"]}]")]
    [InlineData("[{\"kind\":\"handle_backoffice_form\",\"template_ids\":\"6a1b2c3d-0000-4000-8000-00000000a001\"}]")]
    public void Handle_backoffice_form_needs_a_template_list(string json)
        => Assert.False(Run(json).IsValid);

    [Fact]
    public void Handle_backoffice_form_note_is_capped()
    {
        var note = new string('x', 2001);
        var r = Run($"[{{\"kind\":\"handle_backoffice_form\",\"template_ids\":[\"{Templ}\"],\"note\":\"{note}\"}}]");
        Assert.False(r.IsValid);
    }
}
