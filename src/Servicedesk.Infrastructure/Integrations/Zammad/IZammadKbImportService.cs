namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Workflow surface for the v0.0.43 Zammad KB-import.
///
/// Phases:
/// <list type="number">
/// <item><see cref="StartRunAsync"/> — admin clicked "New KB import". Row
/// lands in <c>kb_import_runs</c> with status=pending.</item>
/// <item><see cref="ListKnowledgeBasesAsync"/> — picker for installs with
/// more than one KB. Single-KB installs auto-select.</item>
/// <item><see cref="BuildProposalAsync"/> — fetch Zammad /init, build the
/// proposal tree, persist on the run, set status=awaiting_approval.</item>
/// <item><see cref="SaveSectionDecisionsAsync"/> — admin edits decisions
/// per node; partial saves are supported. Each save overwrites the run's
/// proposed_tree JSONB.</item>
/// <item><see cref="ApplySectionsAsync"/> — execute the create/merge/skip
/// decisions. Creates KbSections in dependency order, writes
/// kb_section_import_mappings, sets status=approved.</item>
/// <item><see cref="ListPickerAsync"/> — paginated answer list scoped to
/// the run's selected KB and the approved section mappings.</item>
/// <item><see cref="StartArticleImportAsync"/> — persist the article
/// selection on the run and enqueue the run-id on the import worker
/// queue; status flips to importing.</item>
/// <item><see cref="GetRunAsync"/> + <see cref="ListRecordsAsync"/> drive
/// the progress page.</item>
/// <item><see cref="CancelRunAsync"/> sets status=cancelled; the worker
/// checks between articles.</item>
/// </list>
public interface IZammadKbImportService
{
    Task<Guid> StartRunAsync(Guid? startedByUserId, CancellationToken ct);

    Task<IReadOnlyList<ZammadKnowledgeBase>> ListKnowledgeBasesAsync(CancellationToken ct);

    Task<ZammadKbProposal?> BuildProposalAsync(
        Guid runId, long knowledgeBaseId, CancellationToken ct);

    Task<bool> SaveSectionDecisionsAsync(
        Guid runId,
        IReadOnlyList<ZammadKbProposalNode> updatedNodes,
        CancellationToken ct);

    /// Applies the decisions on the run's proposed_tree:
    ///   create → new KbSection + section_translation
    ///   merge  → reuse target_section_id (no schema change)
    ///   skip   → record a 'skip' mapping; articles in this category will
    ///            be skipped at import time.
    /// Returns the number of mappings written (or 0 if the run is not in
    /// awaiting_approval).
    Task<int> ApplySectionsAsync(
        Guid runId, Guid actorUserId, CancellationToken ct);

    Task<ZammadKbPickerPage> ListPickerAsync(
        Guid runId,
        string? statusFilter,
        long? categoryFilter,
        string? freeText,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<bool> StartArticleImportAsync(
        Guid runId,
        IReadOnlyList<long> answerIds,
        Guid? startedByUserId,
        CancellationToken ct);

    Task<IReadOnlyList<ZammadKbImportRunSummary>> ListRunsAsync(
        int limit, CancellationToken ct);

    Task<ZammadKbImportRunSummary?> GetRunAsync(Guid runId, CancellationToken ct);

    Task<ZammadKbProposal?> GetProposalAsync(Guid runId, CancellationToken ct);

    Task<ZammadKbImportRecordPage> ListRecordsAsync(
        Guid runId, Guid? cursor, int limit, string? resultFilter, CancellationToken ct);

    Task<bool> CancelRunAsync(Guid runId, CancellationToken ct);
}
