using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.TdSynnex;
using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.59 — Order Management (TD SYNNEX). Pure parser / diff / URL-policy /
/// trigger-pass tests; no DB, no network. Order data below is fictitious.
public sealed class TdSynnexOrderTests
{
    // The spec's own example shape: list under "orders", items as an array,
    // day-first shipDateValue.
    private const string ExampleOrders = """
        {
          "totalResults": 1, "page": 1, "totalPages": 1, "pageSize": 20,
          "orders": [{
            "tdsynnexOrderNumber": "I00999001",
            "customerOrderNumber": "PO-TEST-1",
            "creationDate": "2026-11-02T09:15:00Z",
            "modificationDate": "2026-11-10T07:28:00Z",
            "orderStatus": "open",
            "statusDetails": { "id": "1;Open;Header", "description": "Open" },
            "totalOrderNetPrice": 525.14,
            "currency": "EUR",
            "shipTo": { "companyName": "Example Ltd", "city": "Exampletown", "zip": "1000", "country": "BE" },
            "items": [{
              "lineId": "10",
              "tdsynnexPartNumber": "1234567",
              "manufPartNumber": "ABC-123",
              "description": "Notebook",
              "lineQuantity": 3,
              "lineDetails": [
                { "quantity": 1, "shipDateValue": "05/12/2026 21:00:00", "status": "SHIPPED", "deliveryNotes": ["DN-1"], "invoices": [] },
                { "quantity": 2, "shipDateValue": "27/11/2026 00:00:00", "status": "OPEN" }
              ]
            }]
          }]
        }
        """;

    private static TdSynnexOrderPage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TdSynnexOrderParser.ParsePage(doc.RootElement);
    }

    [Fact]
    public void Parses_the_example_shape()
    {
        var page = Parse(ExampleOrders);

        Assert.Equal("orders", page.ListKey);
        var o = Assert.Single(page.Orders);
        Assert.Equal("I00999001", o.OrderNumber);
        Assert.Equal("OPEN", o.Status); // normalised to upper case
        Assert.Equal(new DateTime(2026, 11, 2, 9, 15, 0, DateTimeKind.Utc), o.CreatedUtc);
        var line = Assert.Single(o.Lines);
        Assert.Equal(2, line.Splits.Count);
        Assert.Equal(new DateOnly(2026, 12, 5), line.Splits[0].ShipDate); // day-first, not 12 May
        Assert.True(line.Splits[0].IsDone);
        Assert.False(line.Splits[1].IsDone);
    }

    [Fact]
    public void Parses_the_schema_shape_data_key_and_items_object()
    {
        var page = Parse("""
            { "totalPages": 1, "data": [{
                "tdsynnexOrderNumber": "I00999002",
                "orderStatus": "SHIPPING",
                "items": { "lineId": "20", "lineQuantity": 1,
                           "lineDetails": [{ "quantity": 1, "shipDateValue": "2026-11-30", "status": "OPEN" }] }
            }] }
            """);

        Assert.Equal("data", page.ListKey);
        var line = Assert.Single(Assert.Single(page.Orders).Lines);
        Assert.Equal(new DateOnly(2026, 11, 30), Assert.Single(line.Splits).ShipDate);
    }

    [Fact]
    public void Skips_orders_without_a_number_and_tolerates_junk()
    {
        var page = Parse("""{ "orders": [ { "orderStatus": "OPEN" }, 42, { "tdsynnexOrderNumber": "I1", "items": "x" } ] }""");
        var o = Assert.Single(page.Orders);
        Assert.Equal("I1", o.OrderNumber);
        Assert.Empty(o.Lines);
    }

    [Theory]
    [InlineData("05/12/2026 21:00:00", 2026, 12, 5)]
    [InlineData("05/12/2026", 2026, 12, 5)]
    [InlineData("2026-12-05", 2026, 12, 5)]
    [InlineData("2026-12-05T08:00:00Z", 2026, 12, 5)]
    public void Ship_dates_parse_day_first_and_iso(string raw, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), TdSynnexOrderParser.ParseShipDate(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData(null)]
    public void Unknown_ship_dates_are_null(string? raw) => Assert.Null(TdSynnexOrderParser.ParseShipDate(raw));

    [Fact]
    public void Derive_uses_open_splits_only_for_the_eta()
    {
        var o = Assert.Single(Parse(ExampleOrders).Orders);
        var d = TdSynnexOrderDiff.Derive(o);
        Assert.Equal(new DateOnly(2026, 11, 27), d.FirstShipDate); // the shipped split on 05/12 does not count
        Assert.Equal(new DateOnly(2026, 11, 27), d.EtaDate);
        Assert.Contains("abc-123", d.SearchText);
        Assert.Contains("dn-1", d.SearchText);
    }

    [Fact]
    public void No_change_when_only_the_response_order_differs()
    {
        var a = Assert.Single(Parse(ExampleOrders).Orders);
        var reversed = a with
        {
            Lines = a.Lines.Select(l => l with { Splits = l.Splits.Reverse().ToList(), Description = "Renamed" }).ToList(),
        };

        Assert.Equal(TdSynnexOrderDiff.Hash(a), TdSynnexOrderDiff.Hash(reversed));
        Assert.Empty(TdSynnexOrderDiff.Compare(a, reversed));
    }

    [Fact]
    public void Detects_status_ship_date_and_delivery_note_changes()
    {
        var before = Assert.Single(Parse(ExampleOrders).Orders);
        var line = before.Lines[0];
        var after = before with
        {
            Status = "SHIPPING",
            Lines = new[]
            {
                line with
                {
                    Splits = new[]
                    {
                        line.Splits[0],
                        line.Splits[1] with { ShipDate = new DateOnly(2026, 12, 3) },
                    },
                },
            },
        };

        var changes = TdSynnexOrderDiff.Compare(before, after);
        Assert.Contains(changes, c => c.Kind == TdSynnexChangeKind.Status && c.Before == "OPEN" && c.After == "SHIPPING");
        Assert.Contains(changes, c => c.Kind == TdSynnexChangeKind.ShipDate && c.Text.Contains("27/11/2026 → 03/12/2026"));
        Assert.NotEqual(TdSynnexOrderDiff.Hash(before), TdSynnexOrderDiff.Hash(after));

        var shipped = after with
        {
            Lines = new[]
            {
                after.Lines[0] with
                {
                    Splits = new[]
                    {
                        after.Lines[0].Splits[0],
                        after.Lines[0].Splits[1] with { Status = "SHIPPED", DeliveryNotes = new[] { "DN-2" }, Invoices = new[] { "INV-9" } },
                    },
                },
            },
        };
        var more = TdSynnexOrderDiff.Compare(after, shipped);
        Assert.Contains(more, c => c.Kind == TdSynnexChangeKind.Shipment && c.Text.Contains("DN-2"));
        Assert.Contains(more, c => c.Kind == TdSynnexChangeKind.Invoice && c.Text.Contains("INV-9"));
    }

    [Theory]
    [InlineData("https://api.tdsynnex.com/eu", true)]
    [InlineData("https://test.api.tdsynnex.com/eu/auth/token", true)]
    [InlineData("https://tdsynnex.com", true)]
    [InlineData("http://api.tdsynnex.com/eu", false)]
    [InlineData("https://api.tdsynnex.com.example.net/eu", false)]
    [InlineData("https://eviltdsynnex.com/eu", false)]
    [InlineData("https://user:pw@api.tdsynnex.com/eu", false)]
    [InlineData("https://api.tdsynnex.com:8443/eu", false)]
    [InlineData("https://api.tdsynnex.com/eu?x=1", false)]
    [InlineData("", false)]
    public void Endpoint_policy_only_allows_https_tdsynnex_hosts(string url, bool allowed)
    {
        Assert.Equal(allowed, TdSynnexEndpointPolicy.IsAllowed(url));
    }

    [Fact]
    public void Orders_url_carries_the_documented_parameters()
    {
        var url = TdSynnexApiClient.BuildOrdersUrl("https://api.tdsynnex.com/eu/",
            new TdSynnexOrderQuery(ModifiedFromUtc: new DateTime(2026, 11, 20, 8, 5, 0, DateTimeKind.Utc), Statuses: new[] { "OPEN", "SHIPPING" }),
            page: 2, pageSize: 20);

        Assert.StartsWith("https://api.tdsynnex.com/eu/resellers/v1/orders?", url);
        Assert.Contains("IncludeDetails=true", url);
        Assert.Contains("ModifiedFrom=2026-11-20T08%3A05%3A00Z", url);
        Assert.Contains("OrderStatus=OPEN&OrderStatus=SHIPPING", url);
        Assert.Contains("Page=2", url);
    }

    // ---- trigger pass ---------------------------------------------------

    private static TriggerOrderEvent Event(params string[] kinds) => new(
        Guid.NewGuid(), "I00999001", "PO-TEST-1", "SHIPPING", "OPEN",
        kinds.ToHashSet(StringComparer.OrdinalIgnoreCase), new[] { "Status OPEN → SHIPPING" },
        new DateOnly(2026, 11, 27), IsLate: false, "Example Ltd");

    private static IReadOnlySet<string> Refs(params string[] keys) => keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Order_passes_run_only_order_triggers_and_vice_versa()
    {
        var orderPass = TriggerChangeSet.ForOrder(Event(TdSynnexChangeKind.Status));
        var ticketPass = TriggerChangeSet.AllFieldsNew();

        Assert.True(orderPass.RunsTriggerReferencing(Refs(TriggerFieldKeys.OrderChange, TriggerFieldKeys.TicketQueueId)));
        Assert.False(orderPass.RunsTriggerReferencing(Refs(TriggerFieldKeys.TicketQueueId)));
        Assert.False(orderPass.RunsTriggerReferencing(Refs(TriggerFieldKeys.FormSaveEvent)));
        Assert.False(ticketPass.RunsTriggerReferencing(Refs(TriggerFieldKeys.OrderStatus)));
        Assert.True(ticketPass.RunsTriggerReferencing(Refs(TriggerFieldKeys.TicketQueueId)));
    }

    [Fact]
    public void Order_conditions_match_the_event()
    {
        var matcher = new TriggerConditionMatcher();
        var changeSet = TriggerChangeSet.ForOrder(Event(TdSynnexChangeKind.Status, TdSynnexChangeKind.ShipDate));
        // Order fields never read the ticket, so no ticket snapshot is needed.
        var ctx = new TriggerEvaluationContext(Guid.NewGuid(), null!, null, changeSet, DateTime.UtcNow);

        using var hit = JsonDocument.Parse("""
            {"op":"AND","items":[
              {"field":"order.change","operator":"contains_one","value":["ship_date"]},
              {"field":"order.status","operator":"is","value":"SHIPPING"},
              {"field":"order.status","operator":"has_changed"},
              {"field":"order.is_late","operator":"is","value":"false"}]}
            """);
        using var miss = JsonDocument.Parse("""
            {"op":"AND","items":[{"field":"order.change","operator":"contains_one","value":["invoice"]}]}
            """);

        Assert.True(matcher.Matches(hit.RootElement, ctx));
        Assert.False(matcher.Matches(miss.RootElement, ctx));
    }

    [Fact]
    public void Validator_keeps_order_triggers_internal()
    {
        const string cond = """{"op":"AND","items":[{"field":"order.change","operator":"contains_one","value":["status"]}]}""";

        var ok = TriggerValidator.Validate("Reopen on order change", "action", "selective", cond,
            """[{"kind":"set_status","status_id":"00000000-0000-0000-0000-000000000001"},{"kind":"add_internal_note","body_text":"#{order.changes}"}]""",
            null, null);
        var mail = TriggerValidator.Validate("Mail customer", "action", "selective", cond,
            """[{"kind":"send_mail","to":"customer"}]""", null, null);
        var publicNote = TriggerValidator.Validate("Public note", "action", "selective", cond,
            """[{"kind":"add_public_note","body_text":"x"}]""", null, null);
        var timeTrigger = TriggerValidator.Validate("Time", "time", "reminder", cond,
            """[{"kind":"add_internal_note","body_text":"x"}]""", null, null);
        var mixed = TriggerValidator.Validate("Mixed", "action", "selective",
            """{"op":"AND","items":[{"field":"order.change","operator":"contains_one","value":["status"]},{"field":"form.save_event","operator":"is","value":"created"}]}""",
            """[{"kind":"add_internal_note","body_text":"x"}]""", null, null);

        Assert.True(ok.IsValid, ok.Error);
        Assert.False(mail.IsValid);
        Assert.Contains("customer", mail.Error!);
        Assert.False(publicNote.IsValid);
        Assert.False(timeTrigger.IsValid);
        Assert.False(mixed.IsValid);
    }
}
