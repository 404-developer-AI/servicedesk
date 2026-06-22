using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// <inheritdoc cref="IKbChatService"/>
public sealed class KbChatService : IKbChatService
{
    /// The one tool the assistant is ever given. Its handler runs the
    /// auth-scoped KB search server-side; the model can only ask, never reach.
    private const string SearchToolName = "search_knowledge_base";

    /// Defensive ceiling on a single user message length, independent of the
    /// rolling-window setting — bounds the per-turn input regardless of config.
    private const int MaxUserMessageChars = 4000;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;
    private readonly IClaudeApiClient _api;
    private readonly IClaudeUsageStore _usage;
    private readonly IUserService _users;
    private readonly ILogger<KbChatService> _logger;

    public KbChatService(
        NpgsqlDataSource dataSource,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IClaudeApiClient api,
        IClaudeUsageStore usage,
        IUserService users,
        ILogger<KbChatService> logger)
    {
        _dataSource = dataSource;
        _settings = settings;
        _secrets = secrets;
        _api = api;
        _usage = usage;
        _users = users;
        _logger = logger;
    }

    public async Task<KbChatResult> SendAsync(
        Guid userId,
        IReadOnlyList<KbChatMessage> history,
        string userMessage,
        CancellationToken ct)
    {
        var monthStartUtc = MonthStartUtc(DateTime.UtcNow);

        // ---- Guards (no API call, no cost) -----------------------------
        var enabled = await _settings.GetAsync<bool>(SettingKeys.Claude.KbChatEnabled, ct);
        if (!enabled)
            return await BlockedAsync(userId, "disabled", KbChatOutcome.Disabled,
                "The knowledge-base assistant is turned off.", monthStartUtc, ct);

        var hasKey = await _secrets.HasAsync(ProtectedSecretKeys.ClaudeApiKey, ct);
        if (!hasKey)
            return await BlockedAsync(userId, "not_configured", KbChatOutcome.NotConfigured,
                "The Claude AI integration has no API key configured.", monthStartUtc, ct);

        var zdrConfirmed = await _settings.GetAsync<bool>(SettingKeys.Claude.ZeroDataRetentionConfirmed, ct);
        if (!zdrConfirmed)
            return await BlockedAsync(userId, "zdr_not_confirmed", KbChatOutcome.ZdrNotConfirmed,
                "Zero data retention has not been confirmed for the Claude organisation.", monthStartUtc, ct);

        // Authorization: the per-user KB-access gate. Checked here, once, so the
        // whole turn — every search the model makes — is bound to an agent who
        // may see the knowledge base. An agent without it gets nothing.
        var kbEnabled = await _users.GetKbEnabledAsync(userId, ct);
        if (!kbEnabled)
            return await BlockedAsync(userId, "no_kb_access", KbChatOutcome.NoKbAccess,
                "You do not have access to the knowledge base.", monthStartUtc, ct);

        var effectiveBudgetCents = await ResolveBudgetCentsAsync(userId, ct);
        var budgetMicro = (long)effectiveBudgetCents * 10_000L;
        if (effectiveBudgetCents <= 0)
            return await BlockedAsync(userId, "no_budget", KbChatOutcome.NoBudget,
                "You have no Claude AI budget assigned.", monthStartUtc, ct);

        var spendMicro = await _usage.GetMonthSpendMicroEurAsync(userId, monthStartUtc, ct);
        if (spendMicro >= budgetMicro)
        {
            await _usage.LogAsync(new ClaudeUsageEntry(userId, null, "", 0, 0, 0, 0, "blocked", "budget_exceeded", null), ct);
            return new KbChatResult(KbChatOutcome.BudgetExceeded, null, null,
                "Your monthly Claude AI budget is exhausted.",
                Array.Empty<KbChatCitation>(), 0, 0, 0, spendMicro, budgetMicro);
        }

        // ---- Config ----------------------------------------------------
        var systemPrompt = await _settings.GetAsync<string>(SettingKeys.Claude.KbChatSystemPrompt, ct) ?? string.Empty;
        var model = (await _settings.GetAsync<string>(SettingKeys.Claude.KbChatModel, ct) ?? "claude-haiku-4-5-20251001").Trim();
        var maxTokens = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.KbChatMaxTokens, ct), 256, 8192);
        var resultLimit = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.KbChatResultLimit, ct), 1, 20);
        var maxSearches = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.KbChatMaxSearches, ct), 1, 8);
        var historyWindow = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.KbChatHistoryWindow, ct), 2, 50);
        var snippetChars = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.KbChatSnippetChars, ct), 200, 8000);

        var messages = BuildMessages(history, userMessage, historyWindow);
        var tools = BuildTools();

        // ---- Tool-use loop --------------------------------------------
        var citations = new Dictionary<Guid, KbChatCitation>();
        var totalInput = 0;
        var totalOutput = 0;

        try
        {
            ClaudeChatResult turn = await _api.CreateChatTurnAsync(systemPrompt, messages, tools, model, maxTokens, ct);
            totalInput += turn.InputTokens;
            totalOutput += turn.OutputTokens;

            var searchesUsed = 0;
            while (string.Equals(turn.StopReason, "tool_use", StringComparison.OrdinalIgnoreCase) && turn.ToolUses.Count > 0)
            {
                // Echo the assistant's tool_use turn back into the transcript.
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn.AssistantContent });

                var capReached = searchesUsed >= maxSearches;
                var toolResults = new JsonArray();
                foreach (var call in turn.ToolUses)
                {
                    string resultText;
                    if (call.Name != SearchToolName)
                    {
                        // The model invented a tool we never offered — refuse.
                        resultText = "Unknown tool. Only search_knowledge_base is available.";
                    }
                    else if (capReached)
                    {
                        resultText = "Search limit reached for this turn. Answer now using the articles already retrieved; if they are insufficient, tell the user no matching knowledge-base article was found.";
                    }
                    else
                    {
                        var query = call.Input["query"]?.GetValue<string>() ?? string.Empty;
                        var hits = await RetrieveAsync(query, resultLimit, snippetChars, ct);
                        foreach (var h in hits)
                            citations[h.ArticleId] = new KbChatCitation(h.ArticleId, h.Title, h.Slug, h.SectionId);
                        resultText = FormatSearchResults(query, hits);
                    }

                    toolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = call.Id,
                        ["content"] = resultText,
                    });
                }

                messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });

                if (!capReached) searchesUsed++;

                // On the turn after the cap, drop the tools so the model is
                // forced to answer with text rather than search again.
                var nextTools = capReached ? new JsonArray() : tools;
                turn = await _api.CreateChatTurnAsync(systemPrompt, messages, nextTools, model, maxTokens, ct);
                totalInput += turn.InputTokens;
                totalOutput += turn.OutputTokens;

                if (capReached) break;
            }

            // ---- Cost + log --------------------------------------------
            var inputPriceCents = await _settings.GetAsync<int>(SettingKeys.Claude.InputPriceCentsPerMTok, ct);
            var outputPriceCents = await _settings.GetAsync<int>(SettingKeys.Claude.OutputPriceCentsPerMTok, ct);
            var costMicro = ((long)totalInput * inputPriceCents + (long)totalOutput * outputPriceCents) / 100L;

            await _usage.LogAsync(new ClaudeUsageEntry(
                userId, null, model, totalInput, totalOutput, costMicro, 0, "ok", null, turn.RequestId), ct);

            var replyText = string.IsNullOrWhiteSpace(turn.Text)
                ? "I could not find anything about that in the knowledge base."
                : turn.Text;

            var orderedCitations = citations.Values.ToList();
            return new KbChatResult(
                KbChatOutcome.Ok,
                replyText,
                ClaudeMarkdown.MarkdownToHtml(replyText),
                null,
                orderedCitations,
                totalInput, totalOutput, costMicro,
                spendMicro + costMicro, budgetMicro);
        }
        catch (ClaudeApiException ex)
        {
            await _usage.LogAsync(new ClaudeUsageEntry(
                userId, null, model, totalInput, totalOutput, 0, 0, "error",
                ex.UpstreamErrorCode ?? "api_error", null), ct);
            throw;
        }
    }

    // ---- Transcript + tools -------------------------------------------

    private static JsonArray BuildMessages(IReadOnlyList<KbChatMessage> history, string userMessage, int historyWindow)
    {
        // Keep only the rolling window of prior turns, normalised to clean
        // user/assistant text messages. Anything that isn't one of those two
        // roles is dropped — the client never gets to inject tool blocks.
        var window = history
            .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(historyWindow)
            .ToList();

        // The Messages API requires the transcript to start with a user turn.
        while (window.Count > 0 && window[0].Role != "user")
            window.RemoveAt(0);

        var messages = new JsonArray();
        foreach (var m in window)
        {
            messages.Add(new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = Clamp(m.Text, MaxUserMessageChars) } },
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = Clamp(userMessage, MaxUserMessageChars) } },
        });
        return messages;
    }

    private static JsonArray BuildTools() => new()
    {
        new JsonObject
        {
            ["name"] = SearchToolName,
            ["description"] = "Search the helpdesk's internal knowledge base and return the most relevant articles (title + a short excerpt). This is your only source of information. Call it with a concise query built from the user's question.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Keywords or a short phrase to search for, in the language of the user's question.",
                    },
                },
                ["required"] = new JsonArray { "query" },
            },
        },
    };

    private static string FormatSearchResults(string query, IReadOnlyList<KbHit> hits)
    {
        if (hits.Count == 0)
            return $"No knowledge-base articles matched \"{query}\".";

        var sb = new StringBuilder();
        sb.Append("Found ").Append(hits.Count).Append(" knowledge-base article(s) for \"").Append(query).Append("\":\n\n");
        for (var i = 0; i < hits.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ").Append(hits[i].Title).Append('\n');
            if (!string.IsNullOrWhiteSpace(hits[i].Snippet))
                sb.Append(hits[i].Snippet).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    // ---- Authorized retrieval -----------------------------------------

    /// Runs the same two-path (FTS + trigram) match as the global KB search
    /// source, scoped to Internal/Published articles in the install's default
    /// locale. The per-user KB-access gate has already been checked for this
    /// turn, so these are exactly the articles the agent may open.
    private async Task<IReadOnlyList<KbHit>> RetrieveAsync(string query, int limit, int snippetChars, CancellationToken ct)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length == 0) return Array.Empty<KbHit>();

        const string sql = """
            WITH q AS (
                SELECT plainto_tsquery('simple', lower(@query)) AS tsq,
                       lower(@query) AS norm
            ),
            hits AS (
                SELECT a.id, a.section_id, a.slug, t.title, t.body_text,
                       CASE
                           WHEN t.search_vector @@ (SELECT tsq FROM q)
                               THEN ts_rank_cd(t.search_vector, (SELECT tsq FROM q))
                           ELSE 0
                       END AS fts_rank,
                       CASE
                           WHEN lower(t.title) % (SELECT norm FROM q)
                               THEN similarity(lower(t.title), (SELECT norm FROM q))
                           ELSE 0
                       END AS trgm_rank
                  FROM kb_articles a
                  JOIN knowledge_base kb ON TRUE
                  LEFT JOIN kb_article_translations t
                       ON t.article_id = a.id AND t.locale_code = kb.default_locale_code
                 WHERE a.status IN ('Internal','Published')
                   AND t.id IS NOT NULL
                   AND (
                        t.search_vector @@ (SELECT tsq FROM q)
                     OR lower(t.title) % (SELECT norm FROM q)
                     OR lower(t.title) LIKE '%' || (SELECT norm FROM q) || '%'
                       )
            )
            SELECT id         AS Id,
                   section_id AS SectionId,
                   slug       AS Slug,
                   title      AS Title,
                   body_text  AS BodyText
              FROM hits
             ORDER BY (fts_rank + trgm_rank) DESC, title
             LIMIT @limit;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<KbHitRow>(new CommandDefinition(
            sql, new { query = normalized, limit }, cancellationToken: ct))).ToList();

        return rows.Select(r => new KbHit(
            r.Id, r.SectionId, r.Slug, r.Title, Snippet(r.BodyText, snippetChars))).ToList();
    }

    private static string Snippet(string? body, int max)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var trimmed = body.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    // ---- Budget --------------------------------------------------------

    private async Task<int> ResolveBudgetCentsAsync(Guid userId, CancellationToken ct)
    {
        var overrideCents = await _usage.GetUserBudgetOverrideCentsAsync(userId, ct);
        if (overrideCents.HasValue) return overrideCents.Value;
        return await _settings.GetAsync<int>(SettingKeys.Claude.DefaultMonthlyBudgetEurCents, ct);
    }

    private static DateTime MonthStartUtc(DateTime nowUtc) =>
        new(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<KbChatResult> BlockedAsync(
        Guid userId, string code, KbChatOutcome outcome, string message, DateTime monthStartUtc, CancellationToken ct)
    {
        await _usage.LogAsync(new ClaudeUsageEntry(userId, null, "", 0, 0, 0, 0, "blocked", code, null), ct);
        var spend = await _usage.GetMonthSpendMicroEurAsync(userId, monthStartUtc, ct);
        return new KbChatResult(outcome, null, null, message, Array.Empty<KbChatCitation>(), 0, 0, 0, spend, 0);
    }

    private static string Clamp(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max];
    }

    private sealed record KbHit(Guid ArticleId, Guid SectionId, string Slug, string Title, string Snippet);

    private sealed record KbHitRow(Guid Id, Guid SectionId, string Slug, string Title, string? BodyText);
}
