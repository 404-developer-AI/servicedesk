using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// KB-chat usage and tuning state for the performance report: period
/// aggregates from <c>claude_usage_log</c> (never per user, never message
/// content) plus the live article-index size against its cap. Lets an admin —
/// or Claude Code reading the export — see when the knowledge base has
/// outgrown the full title index or when cost per question drifts.
public sealed class KbChatUsageSummary
{
    // ---- Configuration now -------------------------------------------
    public bool Enabled { get; init; }
    public string Model { get; init; } = "";
    public bool PromptCaching { get; init; }
    public int CatalogMaxArticles { get; init; }
    public int MaxSearches { get; init; }
    public int ResultLimit { get; init; }
    public int SnippetChars { get; init; }
    public int InputPriceCentsPerMTok { get; init; }
    public int OutputPriceCentsPerMTok { get; init; }
    public int CacheWritePricePct { get; init; }
    public int CacheReadPricePct { get; init; }

    // ---- Article index now -------------------------------------------
    public string CatalogMode { get; init; } = KbChatCatalogMode.Full;
    public int SearchableArticles { get; init; }
    public int Sections { get; init; }
    public int CatalogEstimatedTokens { get; init; }

    // ---- Period ------------------------------------------------------
    public long Turns { get; init; }
    public long ErrorTurns { get; init; }
    public long BlockedTurns { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long CostMicroEur { get; init; }

    /// Turns logged with the v0.1.47 tuning columns (older rows have none).
    public long TrackedTurns { get; init; }
    public long Searches { get; init; }
    public long EmptySearches { get; init; }
    public long TurnsAtSearchCap { get; init; }
    public long SectionModeTurns { get; init; }

    public double AvgCostMicroEurPerTurn => Turns == 0 ? 0 : (double)CostMicroEur / Turns;
    public double AvgSearchesPerTurn => TrackedTurns == 0 ? 0 : (double)Searches / TrackedTurns;
    public double EmptySearchPct => Searches == 0 ? 0 : 100.0 * EmptySearches / Searches;
    public double SearchCapPct => TrackedTurns == 0 ? 0 : 100.0 * TurnsAtSearchCap / TrackedTurns;

    /// Share of all input tokens served from the prompt cache.
    public double CacheReadPct
    {
        get
        {
            var all = InputTokens + CacheReadTokens + CacheWriteTokens;
            return all == 0 ? 0 : 100.0 * CacheReadTokens / all;
        }
    }

    /// Cost of sending the index once, uncached and as a cache read (µ€).
    public long CatalogCostUncachedMicroEur =>
        KbChatPricing.CostMicroEur(CatalogEstimatedTokens, 0, 0, 0, InputPriceCentsPerMTok, 0, 0, 0);
    public long CatalogCostCachedMicroEur =>
        KbChatPricing.CostMicroEur(0, 0, CatalogEstimatedTokens, 0, InputPriceCentsPerMTok, 0, 0, CacheReadPricePct);

    /// Estimated index size if every searchable article title were listed —
    /// what raising the cap to cover the whole knowledge base would cost.
    public int FullIndexEstimatedTokens { get; init; }
}

public static class KbChatUsageReport
{
    public static async Task<KbChatUsageSummary> ReadAsync(
        NpgsqlDataSource dataSource, ISettingsService settings, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var maxSearches = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Claude.KbChatMaxSearches, ct), 1, 8);
        var catalogMax = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Claude.KbChatCatalogMaxArticles, ct), 0, 5000);
        var model = (await settings.GetAsync<string>(SettingKeys.Claude.KbChatModel, ct) ?? "").Trim();
        var price = ClaudeModelPrices.Resolve(await settings.GetAsync<string>(SettingKeys.Claude.ModelPrices, ct), model);

        const string sql = """
            SELECT count(*) FILTER (WHERE outcome = 'ok')                                AS Turns,
                   count(*) FILTER (WHERE outcome = 'error')                             AS ErrorTurns,
                   count(*) FILTER (WHERE outcome = 'blocked')                           AS BlockedTurns,
                   COALESCE(sum(input_tokens)       FILTER (WHERE outcome = 'ok'), 0)    AS InputTokens,
                   COALESCE(sum(output_tokens)      FILTER (WHERE outcome = 'ok'), 0)    AS OutputTokens,
                   COALESCE(sum(cache_read_tokens)  FILTER (WHERE outcome = 'ok'), 0)    AS CacheReadTokens,
                   COALESCE(sum(cache_write_tokens) FILTER (WHERE outcome = 'ok'), 0)    AS CacheWriteTokens,
                   COALESCE(sum(cost_micro_eur), 0)::bigint                              AS CostMicroEur,
                   count(*) FILTER (WHERE outcome = 'ok' AND kb_searches IS NOT NULL)    AS TrackedTurns,
                   COALESCE(sum(kb_searches)       FILTER (WHERE outcome = 'ok'), 0)     AS Searches,
                   COALESCE(sum(kb_empty_searches) FILTER (WHERE outcome = 'ok'), 0)     AS EmptySearches,
                   count(*) FILTER (WHERE outcome = 'ok' AND kb_searches >= @maxSearches) AS TurnsAtSearchCap,
                   count(*) FILTER (WHERE outcome = 'ok' AND kb_catalog_mode = 'sections') AS SectionModeTurns
              FROM claude_usage_log
             WHERE feature = 'kbchat'
               AND utc >= @from AND utc < @to
            """;

        Row row;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            row = await conn.QuerySingleAsync<Row>(new CommandDefinition(sql,
                new { from = from.UtcDateTime, to = to.UtcDateTime, maxSearches }, cancellationToken: ct));
        }

        // The index as the next question would get it, and — when the cap
        // already forces section mode — what the full title list would weigh.
        var catalog = await KbChatCatalog.LoadAsync(dataSource, catalogMax, ct);
        var fullTokens = catalog.Mode == KbChatCatalogMode.Full
            ? catalog.EstimatedTokens()
            : (await KbChatCatalog.LoadAsync(dataSource, 5000, ct)).EstimatedTokens();

        return new KbChatUsageSummary
        {
            Enabled = await settings.GetAsync<bool>(SettingKeys.Claude.KbChatEnabled, ct),
            Model = model,
            PromptCaching = await settings.GetAsync<bool>(SettingKeys.Claude.KbChatPromptCaching, ct),
            CatalogMaxArticles = catalogMax,
            MaxSearches = maxSearches,
            ResultLimit = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Claude.KbChatResultLimit, ct), 1, 20),
            SnippetChars = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Claude.KbChatSnippetChars, ct), 200, 8000),
            InputPriceCentsPerMTok = price?.InputCentsPerMTok ?? await settings.GetAsync<int>(SettingKeys.Claude.KbChatInputPriceCentsPerMTok, ct),
            OutputPriceCentsPerMTok = price?.OutputCentsPerMTok ?? await settings.GetAsync<int>(SettingKeys.Claude.KbChatOutputPriceCentsPerMTok, ct),
            CacheWritePricePct = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Claude.KbChatCacheWritePricePct, ct), 0, 1000),
            CacheReadPricePct = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Claude.KbChatCacheReadPricePct, ct), 0, 1000),

            CatalogMode = catalog.Mode,
            SearchableArticles = catalog.ArticleCount,
            Sections = catalog.Sections.Count,
            CatalogEstimatedTokens = catalog.EstimatedTokens(),
            FullIndexEstimatedTokens = fullTokens,

            Turns = row.Turns,
            ErrorTurns = row.ErrorTurns,
            BlockedTurns = row.BlockedTurns,
            InputTokens = row.InputTokens,
            OutputTokens = row.OutputTokens,
            CacheReadTokens = row.CacheReadTokens,
            CacheWriteTokens = row.CacheWriteTokens,
            CostMicroEur = row.CostMicroEur,
            TrackedTurns = row.TrackedTurns,
            Searches = row.Searches,
            EmptySearches = row.EmptySearches,
            TurnsAtSearchCap = row.TurnsAtSearchCap,
            SectionModeTurns = row.SectionModeTurns,
        };
    }

    private sealed record Row(
        long Turns, long ErrorTurns, long BlockedTurns,
        long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long CostMicroEur,
        long TrackedTurns, long Searches, long EmptySearches, long TurnsAtSearchCap, long SectionModeTurns);
}
