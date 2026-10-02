using System.Text;
using Dapper;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// v0.1.18 — server side of the per-view "Columns" search. The client filters
/// the loaded rows itself; this path only runs when the view's list was
/// truncated by Tickets.ListPageSize, so the search must cover rows the
/// browser never received. It matches the same visible text the client
/// matches: a case- and accent-insensitive substring per whitespace token,
/// every token must hit at least one of the requested columns.
public static class TicketColumnSearch
{
    /// Hard caps — the term is user input and every token adds an OR-arm.
    public const int MaxTermLength = 200;
    public const int MaxTokens = 8;

    /// Whitelist: frontend column id → SQL text expression over the
    /// ticket-list joins. Never interpolates client input; unknown ids are
    /// dropped. Keep in lockstep with the client's column `searchText`.
    private static readonly Dictionary<string, string> FieldMap = new(StringComparer.Ordinal)
    {
        ["number"]        = "('#' || t.number::text)",
        ["subject"]       = "t.subject",
        ["requester"]     = "concat_ws(' ', c.first_name, c.last_name, c.email)",
        ["companyName"]   = "COALESCE(co.name, '')",
        ["queueName"]     = "q.name",
        ["statusName"]    = "s.name",
        ["priorityName"]  = "p.name",
        ["categoryName"]  = "COALESCE(cat.name, '')",
        ["assigneeEmail"] = "COALESCE(u.email, '')",
    };

    public static bool IsSearchable(string columnId) => FieldMap.ContainsKey(columnId);

    /// Keeps only whitelisted, distinct column ids, in the given order.
    public static IReadOnlyList<string> FilterFields(IEnumerable<string>? ids) =>
        ids is null
            ? Array.Empty<string>()
            : ids.Select(i => i.Trim()).Where(IsSearchable).Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> Tokenize(string? term) =>
        string.IsNullOrWhiteSpace(term)
            ? Array.Empty<string>()
            : term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(MaxTokens).ToList();

    /// Appends ` AND (…)` per token to <paramref name="sql"/> and binds the
    /// token parameters. No-op when there are no tokens or no valid fields.
    public static void Append(StringBuilder sql, DynamicParameters parameters, string? term, IEnumerable<string>? fields)
    {
        var tokens = Tokenize(term);
        var valid = FilterFields(fields);
        if (tokens.Count == 0 || valid.Count == 0) return;

        for (var i = 0; i < tokens.Count; i++)
        {
            var name = "ColTok" + i;
            parameters.Add(name, "%" + EscapeLike(tokens[i]) + "%");
            sql.Append(" AND (");
            for (var f = 0; f < valid.Count; f++)
            {
                if (f > 0) sql.Append(" OR ");
                sql.Append("unaccent(").Append(FieldMap[valid[f]]).Append(") ILIKE unaccent(@").Append(name).Append(')');
            }
            sql.Append(')');
        }
    }

    /// Escapes LIKE metacharacters (default escape char is backslash) so a
    /// typed `%` or `_` matches literally.
    internal static string EscapeLike(string token) =>
        token.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
