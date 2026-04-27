import * as React from "react";
import { Plus, Trash2, ChevronDown, FolderTree } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  isGroup,
  type ConditionGroup,
  type ConditionLeaf,
  type ConditionNode,
} from "./types";
import type {
  TriggerConditionField,
  Queue,
  Status,
  Priority,
  Category,
} from "@/lib/api";
import { cn } from "@/lib/utils";

type Props = {
  value: ConditionGroup;
  onChange: (next: ConditionGroup) => void;
  fields: TriggerConditionField[];
  operators: string[];
  maxDepth: number;
  expert: boolean;
  onExpertChange: (next: boolean) => void;
  taxonomies: {
    queues: Queue[];
    statuses: Status[];
    priorities: Priority[];
    categories: Category[];
  };
};

const SENDER_OPTIONS = [
  { value: "Customer", label: "Customer" },
  { value: "Agent", label: "Agent" },
  { value: "System", label: "System" },
];

const ARTICLE_TYPE_OPTIONS = [
  { value: "MailReceived", label: "Mail received" },
  { value: "MailSent", label: "Mail sent" },
  { value: "Comment", label: "Comment" },
  { value: "Note", label: "Note" },
];

/// Conditions section of the trigger editor. One-level AND mode renders a
/// flat row-list — adding a row appends a leaf to the root group. Expert
/// mode flips the same JSON tree into a recursive group/leaf renderer
/// supporting AND/OR/NOT nesting up to <c>maxDepth</c>. Switching modes
/// preserves the data: a tree that's flatter than one-level becomes the
/// list, a list becomes a flat AND tree, anything richer flips the toggle
/// on automatically.
export function ConditionsEditor({
  value,
  onChange,
  fields,
  operators,
  maxDepth,
  expert,
  onExpertChange,
  taxonomies,
}: Props) {
  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-xs text-muted-foreground/70">
          {expert
            ? "Expert mode: nest AND/OR/NOT blocks (max " + maxDepth + " levels)."
            : "All conditions must match (implicit AND)."}
        </p>
        <label className="flex items-center gap-2 text-xs text-muted-foreground select-none">
          <FolderTree className="h-3.5 w-3.5" />
          Expert mode
          <Switch checked={expert} onCheckedChange={onExpertChange} />
        </label>
      </div>

      {expert ? (
        <GroupNode
          group={value}
          onChange={onChange}
          depth={0}
          maxDepth={maxDepth}
          fields={fields}
          operators={operators}
          taxonomies={taxonomies}
          isRoot
        />
      ) : (
        <SimpleList
          group={value}
          onChange={onChange}
          fields={fields}
          operators={operators}
          taxonomies={taxonomies}
        />
      )}
    </div>
  );
}

function SimpleList({
  group,
  onChange,
  fields,
  operators,
  taxonomies,
}: {
  group: ConditionGroup;
  onChange: (next: ConditionGroup) => void;
  fields: TriggerConditionField[];
  operators: string[];
  taxonomies: Props["taxonomies"];
}) {
  // In simple mode we flatten any nested groups into "(nested group)"
  // rows that the user can either delete or open in expert mode. Should
  // not happen in normal use because we keep the editor in expert mode
  // automatically when nested data is present, but a hand-edited JSON
  // payload could still land here.
  const items = group.items;

  function patch(idx: number, leaf: ConditionLeaf) {
    const next = items.slice();
    next[idx] = leaf;
    onChange({ op: "AND", items: next });
  }
  function add() {
    onChange({
      op: "AND",
      items: [
        ...items,
        { field: fields[0]?.key ?? "", operator: "is", value: "" },
      ],
    });
  }
  function remove(idx: number) {
    const next = items.slice();
    next.splice(idx, 1);
    onChange({ op: "AND", items: next });
  }

  return (
    <div className="space-y-2">
      {items.length === 0 && (
        <p className="rounded-md border border-dashed border-white/[0.08] bg-white/[0.01] px-3 py-3 text-xs text-muted-foreground/70">
          No conditions — the trigger fires on every event of its activator.
        </p>
      )}
      {items.map((item, idx) =>
        isGroup(item) ? (
          <div
            key={idx}
            className="rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs text-muted-foreground"
          >
            Nested group ({item.op}, {item.items.length} item
            {item.items.length === 1 ? "" : "s"}) — switch to expert mode to edit.
            <Button
              variant="ghost"
              size="sm"
              className="ml-2 h-6 text-destructive"
              onClick={() => remove(idx)}
            >
              <Trash2 className="h-3 w-3" />
              Remove
            </Button>
          </div>
        ) : (
          <LeafRow
            key={idx}
            leaf={item}
            onChange={(next) => patch(idx, next)}
            onRemove={() => remove(idx)}
            fields={fields}
            operators={operators}
            taxonomies={taxonomies}
          />
        ),
      )}
      <Button variant="ghost" size="sm" onClick={add} className="text-xs">
        <Plus className="h-3.5 w-3.5" />
        Add condition
      </Button>
    </div>
  );
}

function GroupNode({
  group,
  onChange,
  depth,
  maxDepth,
  fields,
  operators,
  taxonomies,
  isRoot,
}: {
  group: ConditionGroup;
  onChange: (next: ConditionGroup) => void;
  depth: number;
  maxDepth: number;
  fields: TriggerConditionField[];
  operators: string[];
  taxonomies: Props["taxonomies"];
  isRoot?: boolean;
}) {
  function setOp(op: ConditionGroup["op"]) {
    onChange({ ...group, op });
  }
  function patch(idx: number, next: ConditionNode) {
    const items = group.items.slice();
    items[idx] = next;
    onChange({ ...group, items });
  }
  function remove(idx: number) {
    const items = group.items.slice();
    items.splice(idx, 1);
    onChange({ ...group, items });
  }
  function addLeaf() {
    onChange({
      ...group,
      items: [
        ...group.items,
        { field: fields[0]?.key ?? "", operator: "is", value: "" },
      ],
    });
  }
  function addGroup() {
    onChange({
      ...group,
      items: [...group.items, { op: "AND", items: [] }],
    });
  }

  return (
    <div
      className={cn(
        "rounded-lg border bg-white/[0.02] px-3 py-3",
        depth === 0 ? "border-white/[0.08]" : "border-white/[0.05]",
      )}
    >
      <div className="mb-2 flex items-center gap-2">
        <select
          value={group.op}
          onChange={(e) => setOp(e.target.value as ConditionGroup["op"])}
          className="rounded-md border border-white/10 bg-white/[0.04] px-2 py-1 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring [&_option]:bg-zinc-900"
        >
          <option value="AND">All must match (AND)</option>
          <option value="OR">Any may match (OR)</option>
          <option value="NOT">None may match (NOT)</option>
        </select>
        {!isRoot && (
          <Button
            variant="ghost"
            size="sm"
            className="h-6 text-destructive"
            onClick={() => onChange({ ...group, items: [] })}
          >
            Clear group
          </Button>
        )}
      </div>

      <div className="space-y-2 pl-2 border-l border-white/[0.06]">
        {group.items.length === 0 && (
          <p className="text-xs text-muted-foreground/60">Empty.</p>
        )}
        {group.items.map((item, idx) =>
          isGroup(item) ? (
            <div key={idx} className="flex items-start gap-2">
              <div className="flex-1">
                <GroupNode
                  group={item}
                  onChange={(next) => patch(idx, next)}
                  depth={depth + 1}
                  maxDepth={maxDepth}
                  fields={fields}
                  operators={operators}
                  taxonomies={taxonomies}
                />
              </div>
              <Button
                variant="ghost"
                size="sm"
                className="h-7 text-destructive"
                onClick={() => remove(idx)}
              >
                <Trash2 className="h-3 w-3" />
              </Button>
            </div>
          ) : (
            <LeafRow
              key={idx}
              leaf={item}
              onChange={(next) => patch(idx, next)}
              onRemove={() => remove(idx)}
              fields={fields}
              operators={operators}
              taxonomies={taxonomies}
            />
          ),
        )}
      </div>

      <div className="mt-2 flex items-center gap-2">
        <Button variant="ghost" size="sm" onClick={addLeaf} className="text-xs">
          <Plus className="h-3.5 w-3.5" />
          Add condition
        </Button>
        {depth + 1 < maxDepth && (
          <Button variant="ghost" size="sm" onClick={addGroup} className="text-xs">
            <Plus className="h-3.5 w-3.5" />
            Add group
          </Button>
        )}
      </div>
    </div>
  );
}

function LeafRow({
  leaf,
  onChange,
  onRemove,
  fields,
  operators,
  taxonomies,
}: {
  leaf: ConditionLeaf;
  onChange: (next: ConditionLeaf) => void;
  onRemove: () => void;
  fields: TriggerConditionField[];
  operators: string[];
  taxonomies: Props["taxonomies"];
}) {
  const fieldDef = fields.find((f) => f.key === leaf.field);
  const fieldType = fieldDef?.type ?? "string";
  const allowsHasChanged =
    fieldType !== "boolean" && fieldType !== "tags";

  // Filter operator list to those that semantically apply to the field
  // type. The matcher is permissive (unknown operator = false), so this
  // is purely a UX guard — admins shouldn't be able to pick "starts_with"
  // on a status field and wonder why it never matches.
  const applicableOps = React.useMemo(() => {
    return operators.filter((op) => {
      if (op === "has_changed") return allowsHasChanged;
      if (op === "contains_one" || op === "contains_all") return fieldType === "tags";
      if (op === "starts_with" || op === "ends_with" || op === "contains" || op === "does_not_contain") {
        return fieldType === "string";
      }
      return true; // is / is_not always usable
    });
  }, [operators, fieldType, allowsHasChanged]);

  function setField(field: string) {
    const def = fields.find((f) => f.key === field);
    onChange({
      field,
      operator: leaf.operator,
      // Reset value type when switching field type, otherwise an admin
      // would get garbage in the DB (e.g. a guid stored where a tag-array
      // is expected).
      value: def?.type === "tags" ? [] : "",
    });
  }

  return (
    <div className="flex flex-wrap items-start gap-2">
      <select
        value={leaf.field}
        onChange={(e) => setField(e.target.value)}
        className="min-w-[10rem] rounded-md border border-white/10 bg-white/[0.04] px-2 py-1.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring [&_option]:bg-zinc-900"
      >
        {fields.map((f) => (
          <option key={f.key} value={f.key}>{f.label}</option>
        ))}
      </select>
      <select
        value={leaf.operator}
        onChange={(e) => onChange({ ...leaf, operator: e.target.value })}
        className="rounded-md border border-white/10 bg-white/[0.04] px-2 py-1.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring [&_option]:bg-zinc-900"
      >
        {applicableOps.map((o) => (
          <option key={o} value={o}>{prettifyOp(o)}</option>
        ))}
      </select>
      {leaf.operator !== "has_changed" && (
        <ValueInput
          fieldType={fieldType}
          value={leaf.value}
          onChange={(v) => onChange({ ...leaf, value: v })}
          taxonomies={taxonomies}
        />
      )}
      <Button
        variant="ghost"
        size="sm"
        className="h-7 text-destructive"
        onClick={onRemove}
      >
        <Trash2 className="h-3 w-3" />
      </Button>
    </div>
  );
}

function prettifyOp(op: string): string {
  return op.replace(/_/g, " ");
}

function ValueInput({
  fieldType,
  value,
  onChange,
  taxonomies,
}: {
  fieldType: string;
  value: ConditionLeaf["value"];
  onChange: (v: ConditionLeaf["value"]) => void;
  taxonomies: Props["taxonomies"];
}) {
  if (fieldType === "queue") {
    return (
      <TaxonomySelect
        items={taxonomies.queues.map((q) => ({ id: q.id, name: q.name }))}
        value={typeof value === "string" ? value : ""}
        onChange={onChange}
      />
    );
  }
  if (fieldType === "status") {
    return (
      <TaxonomySelect
        items={taxonomies.statuses.map((s) => ({ id: s.id, name: s.name }))}
        value={typeof value === "string" ? value : ""}
        onChange={onChange}
      />
    );
  }
  if (fieldType === "priority") {
    return (
      <TaxonomySelect
        items={taxonomies.priorities.map((p) => ({ id: p.id, name: p.name }))}
        value={typeof value === "string" ? value : ""}
        onChange={onChange}
      />
    );
  }
  if (fieldType === "category") {
    return (
      <TaxonomySelect
        items={taxonomies.categories.map((c) => ({ id: c.id, name: c.name }))}
        value={typeof value === "string" ? value : ""}
        onChange={onChange}
      />
    );
  }
  if (fieldType === "sender") {
    return (
      <select
        value={typeof value === "string" ? value : ""}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-md border border-white/10 bg-white/[0.04] px-2 py-1.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring [&_option]:bg-zinc-900"
      >
        <option value="">—</option>
        {SENDER_OPTIONS.map((o) => (
          <option key={o.value} value={o.value}>{o.label}</option>
        ))}
      </select>
    );
  }
  if (fieldType === "boolean") {
    return (
      <select
        value={typeof value === "string" ? value : "true"}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-md border border-white/10 bg-white/[0.04] px-2 py-1.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring [&_option]:bg-zinc-900"
      >
        <option value="true">true</option>
        <option value="false">false</option>
      </select>
    );
  }
  if (fieldType === "tags") {
    const list = Array.isArray(value) ? value : [];
    return (
      <input
        type="text"
        placeholder="comma-separated tags"
        value={list.join(", ")}
        onChange={(e) =>
          onChange(
            e.target.value
              .split(",")
              .map((s) => s.trim())
              .filter(Boolean),
          )
        }
        className="min-w-[12rem] rounded-md border border-white/10 bg-white/[0.04] px-2 py-1.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring"
      />
    );
  }
  if (fieldType === "article-type") {
    // Dropdown keyed off the FIELD type, not the current value — the
    // previous value-match guard meant the picker only appeared after
    // the admin had already typed a valid string, which never happened
    // because the input started empty. The matcher compares the
    // event_type column verbatim (case-insensitive), so the picker has
    // to emit the canonical strings exactly.
    return (
      <select
        value={typeof value === "string" ? value : ""}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-md border border-white/10 bg-white/[0.04] px-2 py-1.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring [&_option]:bg-zinc-900"
      >
        <option value="">—</option>
        {ARTICLE_TYPE_OPTIONS.map((o) => (
          <option key={o.value} value={o.value}>{o.label}</option>
        ))}
      </select>
    );
  }
  return (
    <input
      type="text"
      value={typeof value === "string" ? value : ""}
      onChange={(e) => onChange(e.target.value)}
      placeholder="value"
      className="min-w-[12rem] rounded-md border border-white/10 bg-white/[0.04] px-2 py-1.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring"
    />
  );
}

function TaxonomySelect({
  items,
  value,
  onChange,
}: {
  items: { id: string; name: string }[];
  value: string;
  onChange: (v: string) => void;
}) {
  const selected = items.find((i) => i.id === value);
  const [open, setOpen] = React.useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="min-w-[10rem] flex items-center justify-between gap-2 rounded-md border border-white/10 bg-white/[0.04] px-2 py-1.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-ring"
        >
          <span className={cn(!selected && "text-muted-foreground/60")}>
            {selected?.name ?? "Select…"}
          </span>
          <ChevronDown className="h-3 w-3 text-muted-foreground/60" />
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-56 p-1 border border-white/10 bg-[hsl(240_10%_6%/0.96)] backdrop-blur-xl">
        {items.length === 0 ? (
          <div className="px-2 py-1 text-xs text-muted-foreground/70">No options.</div>
        ) : (
          items.map((item) => (
            <button
              key={item.id}
              type="button"
              onClick={() => { onChange(item.id); setOpen(false); }}
              className={cn(
                "w-full text-left rounded px-2 py-1.5 text-xs",
                item.id === value ? "bg-white/[0.06] text-foreground" : "text-foreground/80 hover:bg-white/[0.04]",
              )}
            >
              {item.name}
            </button>
          ))
        )}
      </PopoverContent>
    </Popover>
  );
}
