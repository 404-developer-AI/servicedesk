using Servicedesk.Infrastructure.KnowledgeBase;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Pure helper that turns a flat list of Zammad categories (for a
/// specific KB) into a depth-ordered proposal tree the SPA can render.
/// Depth and parent-ordering are computed in one pass; the importer
/// later relies on this ordering to ensure a parent KbSection exists
/// before its children are created.
///
/// The default action for every proposal node is <c>Create</c>; the
/// admin flips individual nodes to <c>Merge</c> or <c>Skip</c> in the
/// review step. Pre-existing mappings (when a prior run already chose
/// for the same Zammad category id) override the default — the SPA
/// renders them as "previously decided" with the option to revise.
public static class ZammadKbSectionProposalBuilder
{
    /// Builds the proposal. <paramref name="answerCountByCategory"/> is
    /// the pre-computed answer count per category (so the UI can show
    /// "12 answers" inline). <paramref name="existingDecisions"/> is the
    /// dictionary of prior section mappings (zammad_category_id → action
    /// + target_section_id) used to pre-fill the form.
    public static ZammadKbProposal Build(
        ZammadKnowledgeBase knowledgeBase,
        IReadOnlyList<ZammadKbCategory> categories,
        IReadOnlyDictionary<long, int> answerCountByCategory,
        IReadOnlyDictionary<long, (string Action, Guid? TargetSectionId)>? existingDecisions,
        string? localePreference)
    {
        var byId = categories.ToDictionary(c => c.Id);
        // Group by parent — use 0L as the synthetic "root" key so the
        // dictionary's TKey stays non-nullable. Root categories carry
        // ParentId == null and end up in the 0L bucket.
        var children = categories
            .GroupBy(c => c.ParentId ?? 0L)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Position).ThenBy(c => c.Id).ToList());

        // Pick a display locale for category titles. Prefer the supplied
        // preference (typically the KB's default locale) and fall back to
        // the first translation we see — Zammad always seeds at least one.
        string? PickTitle(ZammadKbCategory c)
        {
            if (c.Translations.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(localePreference))
            {
                var hit = c.Translations.FirstOrDefault(t =>
                    string.Equals(t.LocaleCode, localePreference, StringComparison.OrdinalIgnoreCase));
                if (hit is not null && !string.IsNullOrWhiteSpace(hit.Title)) return hit.Title;
            }
            return c.Translations.Select(t => t.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }

        var nodes = new List<ZammadKbProposalNode>(categories.Count);

        void Walk(long? parentId, int depth)
        {
            var key = parentId ?? 0L;
            if (!children.TryGetValue(key, out var list)) return;
            foreach (var cat in list)
            {
                var rawTitle = PickTitle(cat) ?? $"Category #{cat.Id}";
                var slug = KbSlugGenerator.Slugify(rawTitle);
                string action = "create";
                Guid? targetSectionId = null;
                if (existingDecisions is not null
                    && existingDecisions.TryGetValue(cat.Id, out var prior))
                {
                    action = prior.Action;
                    targetSectionId = prior.TargetSectionId;
                }
                var answerCount = answerCountByCategory.TryGetValue(cat.Id, out var n) ? n : 0;
                nodes.Add(new ZammadKbProposalNode(
                    ZammadCategoryId: cat.Id,
                    ZammadParentId: cat.ParentId,
                    Depth: depth,
                    Position: cat.Position,
                    ProposedTitle: rawTitle,
                    ProposedSlug: slug,
                    Action: action,
                    TargetSectionId: targetSectionId,
                    AnswerCount: answerCount));
                Walk(cat.Id, depth + 1);
            }
        }

        Walk(null, 0);

        var total = answerCountByCategory.Values.Sum();
        return new ZammadKbProposal(
            KnowledgeBaseId: knowledgeBase.Id,
            KnowledgeBaseName: knowledgeBase.Name,
            DefaultLocale: knowledgeBase.DefaultLocale ?? localePreference ?? "nl-BE",
            Nodes: nodes,
            TotalAnswerCount: total);
    }
}
