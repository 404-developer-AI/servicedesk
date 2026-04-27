using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers;

public interface ITriggerConditionMatcher
{
    /// Walks the conditions JSONB tree and returns <c>true</c> when the
    /// trigger matches the given evaluation context. An empty AND-block
    /// (the default for a freshly-created trigger) matches everything;
    /// admins must add at least one leaf to narrow the trigger.
    bool Matches(JsonElement conditions, TriggerEvaluationContext ctx);

    /// The set of dotted-path field-keys referenced anywhere in the tree.
    /// Used by the Selective-mode short-circuit: if none of these fields
    /// is in <c>ChangeSet.ChangedFields</c> and no article was added, the
    /// trigger doesn't even need a snapshot fetch.
    IReadOnlySet<string> ReferencedFields(JsonElement conditions);
}
