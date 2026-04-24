import { useEffect, useMemo, useState } from "react";
import {
  ArrowDown,
  ArrowUp,
  Plus,
  Save,
  Trash2,
  X,
} from "lucide-react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  INTAKE_TOKENS,
  intakeFormsApi,
  type IntakeQuestionType,
  type IntakeTemplate,
  type QuestionInputDto,
} from "@/lib/intakeForms-api";

type QuestionDraft = QuestionInputDto & { clientId: string };

const QUESTION_TYPES: { value: IntakeQuestionType; label: string; takesInput: boolean }[] = [
  { value: "ShortText", label: "Short text", takesInput: true },
  { value: "LongText", label: "Long text", takesInput: true },
  { value: "DropdownSingle", label: "Dropdown (single)", takesInput: true },
  { value: "DropdownMulti", label: "Dropdown (multi)", takesInput: true },
  { value: "Number", label: "Number", takesInput: true },
  { value: "Date", label: "Date", takesInput: true },
  { value: "YesNo", label: "Yes / No", takesInput: true },
  { value: "SectionHeader", label: "Section header", takesInput: false },
];

function blankQuestion(order: number): QuestionDraft {
  return {
    clientId: crypto.randomUUID(),
    sortOrder: order,
    type: "ShortText",
    label: "",
    helpText: null,
    isRequired: false,
    defaultValue: null,
    defaultToken: null,
    options: [],
  };
}

export function TemplateEditor({
  existing,
  onDone,
}: {
  existing: IntakeTemplate | null;
  onDone: () => void;
}) {
  const qc = useQueryClient();
  const [name, setName] = useState(existing?.name ?? "");
  const [description, setDescription] = useState(existing?.description ?? "");
  const [isActive, setIsActive] = useState(existing?.isActive ?? true);
  const [questions, setQuestions] = useState<QuestionDraft[]>(() =>
    existing
      ? existing.questions.map((q) => ({
          clientId: crypto.randomUUID(),
          sortOrder: q.sortOrder,
          type: q.type,
          label: q.label,
          helpText: q.helpText,
          isRequired: q.isRequired,
          defaultValue: q.defaultValue,
          defaultToken: q.defaultToken,
          options: q.options.map((o) => ({
            sortOrder: o.sortOrder,
            value: o.value,
            label: o.label,
          })),
        }))
      : [blankQuestion(0)],
  );

  useEffect(() => {
    if (existing) {
      setName(existing.name);
      setDescription(existing.description ?? "");
      setIsActive(existing.isActive);
    }
  }, [existing]);

  const saving = useMutation({
    mutationFn: async () => {
      const payload = {
        name: name.trim(),
        description: description.trim() ? description.trim() : null,
        isActive,
        questions: questions.map((q, idx) => ({
          sortOrder: idx,
          type: q.type,
          label: q.label.trim(),
          helpText: q.helpText?.trim() || null,
          isRequired: q.type === "SectionHeader" ? false : q.isRequired,
          defaultValue:
            q.type === "SectionHeader" ? null : q.defaultValue?.trim() || null,
          defaultToken:
            q.type === "SectionHeader" ? null : q.defaultToken?.trim() || null,
          options:
            q.type === "DropdownSingle" || q.type === "DropdownMulti"
              ? q.options
                  .map((o, oi) => ({
                    sortOrder: oi,
                    value: o.value.trim(),
                    label: o.label.trim(),
                  }))
                  .filter((o) => o.value.length > 0 && o.label.length > 0)
              : [],
        })),
      };

      if (existing) {
        return intakeFormsApi.updateTemplate(existing.id, payload);
      }
      return intakeFormsApi.createTemplate(payload);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["intake-forms", "templates"] });
      if (existing) {
        qc.invalidateQueries({ queryKey: ["intake-forms", "template", existing.id] });
      }
      toast.success("Template saved.");
      onDone();
    },
    onError: (err) => {
      const payload = (err as { payload?: { error?: string } }).payload;
      toast.error(payload?.error ?? "Could not save template.");
    },
  });

  const canSave = useMemo(() => {
    if (name.trim().length === 0) return false;
    if (questions.length === 0) return false;
    for (const q of questions) {
      if (q.label.trim().length === 0) return false;
      if ((q.type === "DropdownSingle" || q.type === "DropdownMulti") && q.options.filter((o) => o.value && o.label).length === 0)
        return false;
    }
    return true;
  }, [name, questions]);

  function moveQuestion(idx: number, delta: -1 | 1) {
    const next = idx + delta;
    if (next < 0 || next >= questions.length) return;
    setQuestions((qs) => {
      const copy = [...qs];
      const [item] = copy.splice(idx, 1);
      copy.splice(next, 0, item!);
      return copy;
    });
  }

  function updateQuestion(clientId: string, patch: Partial<QuestionDraft>) {
    setQuestions((qs) =>
      qs.map((q) => (q.clientId === clientId ? { ...q, ...patch } : q)),
    );
  }

  function addQuestion() {
    setQuestions((qs) => [...qs, blankQuestion(qs.length)]);
  }

  function deleteQuestion(clientId: string) {
    setQuestions((qs) => qs.filter((q) => q.clientId !== clientId));
  }

  return (
    <div className="flex flex-col gap-5">
      <div className="grid gap-4 md:grid-cols-2">
        <label className="flex flex-col gap-1.5">
          <span className="text-xs font-medium text-muted-foreground">Name *</span>
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={200}
            placeholder="e.g. Intake — laptop"
            className="h-9 rounded-md border border-white/[0.08] bg-white/[0.02] px-3 text-sm text-foreground outline-none focus:border-primary/40"
          />
        </label>
        <label className="flex flex-col gap-1.5">
          <span className="text-xs font-medium text-muted-foreground">Description</span>
          <input
            type="text"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            maxLength={2000}
            placeholder="Optional — shown at the top of the public form"
            className="h-9 rounded-md border border-white/[0.08] bg-white/[0.02] px-3 text-sm text-foreground outline-none focus:border-primary/40"
          />
        </label>
      </div>

      <label className="flex items-center gap-2 text-sm text-muted-foreground">
        <input
          type="checkbox"
          checked={isActive}
          onChange={(e) => setIsActive(e.target.checked)}
          className="h-4 w-4"
        />
        Active — agents can pick this template when composing a mail
      </label>

      <div className="flex flex-col gap-3">
        <div className="flex items-center justify-between">
          <h3 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Questions
          </h3>
          <Button size="sm" variant="ghost" onClick={addQuestion}>
            <Plus className="h-4 w-4" />
            Add question
          </Button>
        </div>

        <ul className="flex flex-col gap-3">
          {questions.map((q, idx) => (
            <li
              key={q.clientId}
              className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-4"
            >
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-[11px] font-medium uppercase tracking-widest text-muted-foreground/60">
                  #{idx + 1}
                </span>
                <Select
                  value={q.type}
                  onValueChange={(v) =>
                    updateQuestion(q.clientId, {
                      type: v as IntakeQuestionType,
                      options: [],
                    })
                  }
                >
                  <SelectTrigger className="h-8 w-auto min-w-[12rem] border-white/10 bg-white/[0.04] text-sm focus:border-white/20 focus:bg-white/[0.06] focus:ring-0">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent className="border-white/10 bg-popover/80 backdrop-blur-xl">
                    {QUESTION_TYPES.map((t) => (
                      <SelectItem key={t.value} value={t.value}>
                        {t.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <div className="ml-auto flex items-center gap-1">
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label="Move up"
                    disabled={idx === 0}
                    onClick={() => moveQuestion(idx, -1)}
                  >
                    <ArrowUp className="h-4 w-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label="Move down"
                    disabled={idx === questions.length - 1}
                    onClick={() => moveQuestion(idx, 1)}
                  >
                    <ArrowDown className="h-4 w-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label="Delete question"
                    onClick={() => deleteQuestion(q.clientId)}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>
              </div>

              <div className="mt-3 grid gap-3 md:grid-cols-2">
                <label className="flex flex-col gap-1">
                  <span className="text-xs text-muted-foreground">Label *</span>
                  <input
                    type="text"
                    value={q.label}
                    onChange={(e) =>
                      updateQuestion(q.clientId, { label: e.target.value })
                    }
                    maxLength={500}
                    className="h-9 rounded-md border border-white/[0.08] bg-white/[0.02] px-3 text-sm text-foreground outline-none focus:border-primary/40"
                  />
                </label>
                <label className="flex flex-col gap-1">
                  <span className="text-xs text-muted-foreground">Help text</span>
                  <input
                    type="text"
                    value={q.helpText ?? ""}
                    onChange={(e) =>
                      updateQuestion(q.clientId, { helpText: e.target.value })
                    }
                    maxLength={2000}
                    className="h-9 rounded-md border border-white/[0.08] bg-white/[0.02] px-3 text-sm text-foreground outline-none focus:border-primary/40"
                  />
                </label>
              </div>

              {q.type !== "SectionHeader" && (
                <div className="mt-3 grid gap-3 md:grid-cols-[auto,1fr,1fr]">
                  <label className="flex items-center gap-2 text-sm text-muted-foreground">
                    <input
                      type="checkbox"
                      checked={q.isRequired}
                      onChange={(e) =>
                        updateQuestion(q.clientId, { isRequired: e.target.checked })
                      }
                      className="h-4 w-4"
                    />
                    Required
                  </label>
                  <label className="flex flex-col gap-1">
                    <span className="text-xs text-muted-foreground">Default value</span>
                    <input
                      type="text"
                      value={q.defaultValue ?? ""}
                      onChange={(e) =>
                        updateQuestion(q.clientId, {
                          defaultValue: e.target.value,
                        })
                      }
                      className="h-9 rounded-md border border-white/[0.08] bg-white/[0.02] px-3 text-sm text-foreground outline-none focus:border-primary/40"
                    />
                  </label>
                  <label className="flex flex-col gap-1">
                    <span className="text-xs text-muted-foreground">Default token</span>
                    <Select
                      value={q.defaultToken ?? "__none"}
                      onValueChange={(v) =>
                        updateQuestion(q.clientId, {
                          defaultToken: v === "__none" ? null : v,
                        })
                      }
                    >
                      <SelectTrigger className="h-9 border-white/10 bg-white/[0.04] text-sm focus:border-white/20 focus:bg-white/[0.06] focus:ring-0">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent className="border-white/10 bg-popover/80 backdrop-blur-xl">
                        <SelectItem value="__none">
                          <span className="text-muted-foreground">(none)</span>
                        </SelectItem>
                        {INTAKE_TOKENS.map((tk) => (
                          <SelectItem key={tk} value={tk}>
                            <code className="text-xs">{tk}</code>
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </label>
                </div>
              )}

              {(q.type === "DropdownSingle" || q.type === "DropdownMulti") && (
                <DropdownOptionsEditor
                  value={q.options}
                  onChange={(options) => updateQuestion(q.clientId, { options })}
                />
              )}
            </li>
          ))}
        </ul>
      </div>

      <footer className="flex justify-end gap-2 border-t border-white/[0.06] pt-4">
        <Button variant="ghost" onClick={onDone}>
          <X className="h-4 w-4" />
          Cancel
        </Button>
        <Button
          disabled={!canSave || saving.isPending}
          onClick={() => saving.mutate()}
        >
          <Save className="h-4 w-4" />
          {existing ? "Save changes" : "Create template"}
        </Button>
      </footer>
    </div>
  );
}

function DropdownOptionsEditor({
  value,
  onChange,
}: {
  value: Array<{ sortOrder: number; value: string; label: string }>;
  onChange: (
    next: Array<{ sortOrder: number; value: string; label: string }>,
  ) => void;
}) {
  return (
    <div className="mt-3 rounded-md border border-white/[0.06] bg-white/[0.01] p-3">
      <div className="mb-2 flex items-center justify-between">
        <span className="text-xs font-medium text-muted-foreground">
          Dropdown options
        </span>
        <Button
          variant="ghost"
          size="sm"
          onClick={() =>
            onChange([
              ...value,
              { sortOrder: value.length, value: "", label: "" },
            ])
          }
        >
          <Plus className="h-4 w-4" />
          Add option
        </Button>
      </div>
      {value.length === 0 && (
        <p className="text-xs text-muted-foreground">
          At least one option is required for a dropdown.
        </p>
      )}
      <ul className="flex flex-col gap-2">
        {value.map((opt, idx) => (
          <li key={idx} className="grid gap-2 md:grid-cols-[1fr,1fr,auto]">
            <input
              type="text"
              placeholder="Value (stored)"
              value={opt.value}
              onChange={(e) => {
                const next = [...value];
                next[idx] = { ...opt, value: e.target.value };
                onChange(next);
              }}
              maxLength={200}
              className="h-8 rounded-md border border-white/[0.08] bg-white/[0.02] px-2 text-sm text-foreground outline-none focus:border-primary/40"
            />
            <input
              type="text"
              placeholder="Label (shown)"
              value={opt.label}
              onChange={(e) => {
                const next = [...value];
                next[idx] = { ...opt, label: e.target.value };
                onChange(next);
              }}
              maxLength={200}
              className="h-8 rounded-md border border-white/[0.08] bg-white/[0.02] px-2 text-sm text-foreground outline-none focus:border-primary/40"
            />
            <Button
              variant="ghost"
              size="icon"
              aria-label="Remove option"
              onClick={() => onChange(value.filter((_, i) => i !== idx))}
            >
              <Trash2 className="h-4 w-4" />
            </Button>
          </li>
        ))}
      </ul>
    </div>
  );
}
