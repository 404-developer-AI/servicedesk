import * as React from "react";
import { Plus, Trash2, GripVertical } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { type PromptChoiceOption, type PromptQuestion } from "../types";
import { cn } from "@/lib/utils";
import { FieldRow, NativeSelect } from "./actionFieldPrimitives";

/// Generates a stable initial key for a freshly-added question. Falls
/// back to `q1`, `q2`, … so the admin can rename without thinking about
/// uniqueness up-front. The validator enforces the alphanumeric rule;
/// duplicates are also caught at save-time.
function nextQuestionKey(existing: PromptQuestion[]): string {
  for (let i = 1; i < 1000; i++) {
    const candidate = `q${i}`;
    if (!existing.some((q) => q.key === candidate)) return candidate;
  }
  return `q${Math.floor(Math.random() * 100000)}`;
}


/// In-place list editor for the prompt_confirm questions array. Each
/// row gets a type badge, key input, label input and the type-specific
/// fields (required-flag for text, two button-label slots with hide
/// toggles for yesno). Move-up/move-down + remove buttons sit on the
/// right-hand side; the add menu offers Text and Yes/No.
export function PromptQuestionsEditor({
  value,
  onChange,
}: {
  value: PromptQuestion[];
  onChange: (next: PromptQuestion[]) => void;
}) {
  function update(idx: number, q: PromptQuestion) {
    const next = value.slice();
    next[idx] = q;
    onChange(next);
  }
  function remove(idx: number) {
    const next = value.slice();
    next.splice(idx, 1);
    onChange(next);
  }
  function move(idx: number, dir: -1 | 1) {
    const target = idx + dir;
    if (target < 0 || target >= value.length) return;
    const next = value.slice();
    [next[idx], next[target]] = [next[target], next[idx]];
    onChange(next);
  }
  function add(type: PromptQuestion["type"]) {
    const key = nextQuestionKey(value);
    let blank: PromptQuestion;
    if (type === "text") {
      blank = { key, type: "text", label: "", required: false };
    } else if (type === "yesno") {
      blank = { key, type: "yesno", label: "", yes_label: "Yes", no_label: "No" };
    } else {
      blank = {
        key,
        type: "choice",
        label: "",
        options: [
          { label: "", outcome: "allow" },
          { label: "", outcome: "keep_open" },
        ],
      };
    }
    onChange([...value, blank]);
  }
  return (
    <div className="space-y-2">
      {value.length === 0 && (
        <p className="rounded-md border border-dashed border-glass-strong bg-glass px-3 py-3 text-xs text-muted-foreground/70">
          No questions — the dialog will show only the title and the bottom buttons.
        </p>
      )}
      {value.map((q, idx) => (
        <PromptQuestionCard
          key={idx}
          question={q}
          idx={idx}
          total={value.length}
          onChange={(next) => update(idx, next)}
          onRemove={() => remove(idx)}
          onMove={(dir) => move(idx, dir)}
        />
      ))}
      <AddPromptQuestionMenu onAdd={add} />
    </div>
  );
}


export function AddPromptQuestionMenu({
  onAdd,
}: {
  onAdd: (type: PromptQuestion["type"]) => void;
}) {
  const [open, setOpen] = React.useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm" className="text-xs">
          <Plus className="h-3.5 w-3.5" />
          Add question
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-56 p-1 border border-glass bg-popover/95 backdrop-blur-xl">
        <button
          type="button"
          onClick={() => { onAdd("text"); setOpen(false); }}
          className="w-full text-left rounded px-2 py-1.5 text-xs text-foreground/80 hover:bg-glass-hover"
        >
          Free-text field
        </button>
        <button
          type="button"
          onClick={() => { onAdd("yesno"); setOpen(false); }}
          className="w-full text-left rounded px-2 py-1.5 text-xs text-foreground/80 hover:bg-glass-hover"
        >
          Yes / No buttons
        </button>
        <button
          type="button"
          onClick={() => { onAdd("choice"); setOpen(false); }}
          className="w-full text-left rounded px-2 py-1.5 text-xs text-foreground/80 hover:bg-glass-hover"
        >
          Choice (allow / keep open)
        </button>
      </PopoverContent>
    </Popover>
  );
}


export function PromptQuestionCard({
  question,
  idx,
  total,
  onChange,
  onRemove,
  onMove,
}: {
  question: PromptQuestion;
  idx: number;
  total: number;
  onChange: (next: PromptQuestion) => void;
  onRemove: () => void;
  onMove: (dir: -1 | 1) => void;
}) {
  return (
    <div className="rounded-lg border border-glass-strong bg-glass px-3 py-2.5 space-y-2.5">
      <header className="flex items-center gap-2">
        <GripVertical className="h-4 w-4 text-muted-foreground/50" />
        <span className={cn(
          "rounded-md border px-2 py-0.5 text-[11px] font-medium",
          question.type === "text"
            ? "border-sky-400/30 bg-sky-400/10 text-sky-200"
            : question.type === "yesno"
              ? "border-emerald-400/30 bg-emerald-400/10 text-emerald-200"
              : "border-violet-400/30 bg-violet-400/10 text-violet-200",
        )}>
          {idx + 1}. {question.type === "text" ? "Free text" : question.type === "yesno" ? "Yes / No" : "Choice"}
        </span>
        <input
          type="text"
          value={question.key}
          onChange={(e) => onChange({ ...question, key: e.target.value.trim() })}
          placeholder="key"
          className="w-24 rounded-md border border-glass bg-glass px-2 py-1 text-xs font-mono text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
          title="Token name for #{prompt.<key>} — lowercase, alphanumeric + underscore."
        />
        <div className="ml-auto flex items-center gap-1">
          <Button
            variant="ghost" size="sm"
            className="h-6 w-6 p-0"
            onClick={() => onMove(-1)}
            disabled={idx === 0}
            title="Move up"
          >▲</Button>
          <Button
            variant="ghost" size="sm"
            className="h-6 w-6 p-0"
            onClick={() => onMove(1)}
            disabled={idx === total - 1}
            title="Move down"
          >▼</Button>
          <Button
            variant="ghost" size="sm"
            className="h-7 text-destructive"
            onClick={onRemove}
            title="Remove question"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </div>
      </header>

      <FieldRow label="Question">
        <input
          type="text"
          value={question.label}
          onChange={(e) => onChange({ ...question, label: e.target.value })}
          placeholder={question.type === "yesno"
            ? "Sales receipt created?"
            : question.type === "choice"
              ? "What do you want to do?"
              : "Add a short summary of what you did"}
          className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
        />
      </FieldRow>

      {question.type === "text" ? (
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={question.required}
            onChange={(e) => onChange({ ...question, required: e.target.checked })}
            className="h-4 w-4 rounded border-glass-strong bg-glass"
          />
          Must be filled in
        </label>
      ) : question.type === "yesno" ? (
        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <PromptYesNoButtonField
            label="Positive answer"
            value={question.yes_label}
            placeholder="Yes"
            tone="emerald"
            onChange={(v) => onChange({ ...question, yes_label: v })}
          />
          <PromptYesNoButtonField
            label="Negative answer (cancel)"
            value={question.no_label}
            placeholder="No"
            tone="rose"
            onChange={(v) => onChange({ ...question, no_label: v })}
          />
        </div>
      ) : (
        <PromptChoiceOptionsEditor
          options={question.options}
          onChange={(opts) => onChange({ ...question, options: opts })}
        />
      )}
    </div>
  );
}


/// Editor for a choice question's options. Each row carries an option
/// label and an outcome (allow the status change vs keep the ticket open).
/// At least two options is the natural shape; the editor enforces one
/// minimum so an admin can't end up with an unselectable question.
export function PromptChoiceOptionsEditor({
  options,
  onChange,
}: {
  options: PromptChoiceOption[];
  onChange: (next: PromptChoiceOption[]) => void;
}) {
  function update(idx: number, opt: PromptChoiceOption) {
    const next = options.slice();
    next[idx] = opt;
    onChange(next);
  }
  function remove(idx: number) {
    const next = options.slice();
    next.splice(idx, 1);
    onChange(next);
  }
  function add() {
    onChange([...options, { label: "", outcome: "allow" }]);
  }
  return (
    <div className="space-y-2">
      <span className="text-xs font-medium text-muted-foreground">Options</span>
      {options.map((opt, idx) => (
        <div key={idx} className="flex items-center gap-2">
          <input
            type="text"
            value={opt.label}
            onChange={(e) => update(idx, { ...opt, label: e.target.value })}
            placeholder="Close it — order delivered"
            className="flex-1 rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
          />
          <div className="w-40 shrink-0">
            <NativeSelect
              value={opt.outcome}
              onChange={(v) => update(idx, { ...opt, outcome: v === "keep_open" ? "keep_open" : "allow" })}
            >
              <option value="allow">Allow close</option>
              <option value="keep_open">Keep open</option>
            </NativeSelect>
          </div>
          <Button
            variant="ghost" size="sm"
            className="h-7 text-destructive"
            onClick={() => remove(idx)}
            disabled={options.length <= 1}
            title="Remove option"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </div>
      ))}
      <Button variant="ghost" size="sm" className="text-xs" onClick={add}>
        <Plus className="h-3.5 w-3.5" />
        Add option
      </Button>
    </div>
  );
}


/// Single button-label slot for a yes/no question. The text input is
/// active when the button is shown; toggling the checkbox to "hidden"
/// nulls the label so the dialog skips that button. Tone is just a
/// visual cue (green for positive, red for negative).
export function PromptYesNoButtonField({
  label,
  value,
  placeholder,
  tone,
  onChange,
}: {
  label: string;
  value: string | null;
  placeholder: string;
  tone: "emerald" | "rose";
  onChange: (next: string | null) => void;
}) {
  const visible = value !== null;
  const accent = tone === "emerald"
    ? "border-emerald-400/30 focus:ring-emerald-400/40"
    : "border-rose-400/30 focus:ring-rose-400/40";
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between gap-2">
        <span className="text-xs font-medium text-muted-foreground">{label}</span>
        <label className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
          <input
            type="checkbox"
            checked={visible}
            onChange={(e) => onChange(e.target.checked ? (value ?? placeholder) : null)}
            className="h-3.5 w-3.5 rounded border-glass-strong bg-glass"
          />
          {visible ? "Visible" : "Hidden"}
        </label>
      </div>
      <input
        type="text"
        value={value ?? ""}
        disabled={!visible}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className={cn(
          "w-full rounded-md border bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 disabled:opacity-40",
          visible ? accent : "border-glass",
        )}
      />
    </div>
  );
}
