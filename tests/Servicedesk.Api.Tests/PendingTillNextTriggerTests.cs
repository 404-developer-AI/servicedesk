using System.Text.Json;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.24 (post-feature) — pins the parsing of the optional
/// `nextTriggerId` companion property on `set_pending_till`. The
/// resolver's job here is to hand back a Guid? + an actionable error
/// message; referential validity (does the trigger exist? is it a
/// reminder?) is enforced by the FK + the FE picker, not here.
public sealed class PendingTillNextTriggerTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Absent_property_yields_null_pointer_no_error()
    {
        var json = Parse("""{ "kind":"set_pending_till", "relative":"P1D" }""");
        var (id, err) = SetPendingTillResolver.ResolveNextTriggerId(json);

        Assert.Null(id);
        Assert.Null(err);
    }

    [Fact]
    public void Json_null_yields_null_pointer_no_error()
    {
        var json = Parse("""{ "kind":"set_pending_till", "relative":"P1D", "nextTriggerId": null }""");
        var (id, err) = SetPendingTillResolver.ResolveNextTriggerId(json);

        Assert.Null(id);
        Assert.Null(err);
    }

    [Fact]
    public void Empty_string_yields_null_pointer_no_error()
    {
        var json = Parse("""{ "kind":"set_pending_till", "relative":"P1D", "nextTriggerId": "" }""");
        var (id, err) = SetPendingTillResolver.ResolveNextTriggerId(json);

        Assert.Null(id);
        Assert.Null(err);
    }

    [Fact]
    public void Valid_guid_string_yields_pointer()
    {
        var expected = Guid.NewGuid();
        var json = Parse($$"""{ "kind":"set_pending_till", "relative":"P1D", "nextTriggerId":"{{expected}}" }""");
        var (id, err) = SetPendingTillResolver.ResolveNextTriggerId(json);

        Assert.Equal(expected, id);
        Assert.Null(err);
    }

    [Fact]
    public void Malformed_string_returns_actionable_error()
    {
        var json = Parse("""{ "kind":"set_pending_till", "relative":"P1D", "nextTriggerId":"not-a-uuid" }""");
        var (id, err) = SetPendingTillResolver.ResolveNextTriggerId(json);

        Assert.Null(id);
        Assert.NotNull(err);
        Assert.Contains("not a valid UUID", err);
    }

    [Fact]
    public void Non_string_non_null_returns_type_error()
    {
        var json = Parse("""{ "kind":"set_pending_till", "relative":"P1D", "nextTriggerId": 42 }""");
        var (id, err) = SetPendingTillResolver.ResolveNextTriggerId(json);

        Assert.Null(id);
        Assert.NotNull(err);
        Assert.Contains("UUID string", err);
    }
}
