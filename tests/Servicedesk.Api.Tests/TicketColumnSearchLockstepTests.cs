using System.Text.RegularExpressions;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Lockstep between the server whitelist (TicketColumnSearch.FieldMap) and the
/// client's COLUMN_SEARCH_TEXT (src/Servicedesk.Web/src/lib/ticketColumns.ts):
/// the server search only runs when the list was truncated, so a column that
/// is searchable on one side only makes the same search answer differently
/// depending on the list size (CLAUDE.md: "keep … in lockstep").
public sealed class TicketColumnSearchLockstepTests
{
    [Fact]
    public void Server_and_client_search_the_same_columns()
    {
        var client = ClientColumnIds();
        var server = TicketColumnSearch.SearchableColumnIds.ToHashSet(StringComparer.Ordinal);

        Assert.True(client.Count > 0, "No column ids found in COLUMN_SEARCH_TEXT — has its declaration changed shape?");
        Assert.True(client.SetEquals(server),
            $"Client only: [{string.Join(", ", client.Except(server))}]; "
            + $"server only: [{string.Join(", ", server.Except(client))}].");
    }

    private static HashSet<string> ClientColumnIds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Servicedesk.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Servicedesk.Web", "src", "lib", "ticketColumns.ts"));

        // The object literal from `export const COLUMN_SEARCH_TEXT … = {` to its
        // closing `};`; each entry starts a line with `id: (t) =>`.
        var start = source.IndexOf("export const COLUMN_SEARCH_TEXT", StringComparison.Ordinal);
        Assert.True(start >= 0, "COLUMN_SEARCH_TEXT not found in ticketColumns.ts.");
        var open = source.IndexOf("= {", start, StringComparison.Ordinal);
        var close = source.IndexOf("\n};", open, StringComparison.Ordinal);
        var body = source[open..close];
        return Regex.Matches(body, @"^\s+(\w+):\s*\(t\)\s*=>", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
