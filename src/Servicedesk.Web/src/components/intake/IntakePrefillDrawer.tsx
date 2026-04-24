import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Save, Trash2 } from "lucide-react";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
  SheetFooter,
} from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  intakeFormsApi,
  type IntakeFormAgentView,
  type IntakeQuestion,
} from "@/lib/intakeForms-api";

type Props = {
  ticketId: string;
  instanceId: string | null;
  onClose: () => void;
  /// Called when the agent deletes a Draft from the drawer — the composer
  /// should drop the matching `::`-chip so the mail body stays in sync.
  onDeleted?: (instanceId: string) => void;
};

/// Side drawer opened when an agent clicks a `::` intake-form chip in the
/// mail composer. Lets the agent override the resolved prefill values on a
/// per-field basis before sending, without leaving the composer.
///
/// The instance is already a Draft at this point (the chip-insertion step
/// creates it via POST /api/tickets/{id}/intake-forms and replaces the
/// chip's attrs.templateId with attrs.instanceId). All we do here is
/// fetch its current prefill, let the agent edit, and PUT back.
export function IntakePrefillDrawer({
  ticketId,
  instanceId,
  onClose,
  onDeleted,
}: Props) {
  const qc = useQueryClient();
  const open = instanceId !== null;

  const viewQ = useQuery({
    queryKey: ["intake-forms", "instance", ticketId, instanceId],
    queryFn: () => intakeFormsApi.getInstance(ticketId, instanceId!),
    enabled: open,
  });

  return (
    <Sheet open={open} onOpenChange={(v) => !v && onClose()}>
      <SheetContent className="flex w-full max-w-md flex-col gap-4">
        <SheetHeader>
          <SheetTitle>Intake form — defaults</SheetTitle>
          <SheetDescription>
            Pre-fill values the customer sees when they open the link. Leave
            blank to show an empty field.
          </SheetDescription>
        </SheetHeader>

        {viewQ.isLoading && (
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <Loader2 className="h-4 w-4 animate-spin" />
            Loading…
          </div>
        )}
        {viewQ.isError && (
          <p className="text-sm text-red-300">Could not load form.</p>
        )}
        {viewQ.data && (
          <PrefillEditor
            ticketId={ticketId}
            view={viewQ.data}
            onSaved={() => {
              qc.invalidateQueries({
                queryKey: ["intake-forms", "instance", ticketId, instanceId],
              });
            }}
            onDeleted={() => {
              if (instanceId) onDeleted?.(instanceId);
              onClose();
            }}
            onClose={onClose}
          />
        )}
      </SheetContent>
    </Sheet>
  );
}

function PrefillEditor({
  ticketId,
  view,
  onSaved,
  onDeleted,
  onClose,
}: {
  ticketId: string;
  view: IntakeFormAgentView;
  onSaved: () => void;
  onDeleted: () => void;
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const [values, setValues] = React.useState<Record<string, unknown>>(
    () => ({ ...(view.instance.prefill as Record<string, unknown>) }),
  );

  const save = useMutation({
    mutationFn: () =>
      intakeFormsApi.updatePrefill(ticketId, view.instance.id, values),
    onSuccess: () => {
      toast.success("Defaults saved.");
      onSaved();
    },
    onError: () => toast.error("Could not save defaults."),
  });

  const destroy = useMutation({
    mutationFn: () => intakeFormsApi.deleteDraft(ticketId, view.instance.id),
    onSuccess: () => {
      toast.success("Draft removed.");
      qc.invalidateQueries({
        queryKey: ["intake-forms", "for-ticket", ticketId],
      });
      onDeleted();
    },
    onError: () => toast.error("Could not remove draft."),
  });

  const readOnly = view.instance.status !== "Draft";

  return (
    <div className="flex flex-1 flex-col gap-4 overflow-hidden">
      <div className="rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs text-muted-foreground">
        <div>
          <span className="font-medium text-foreground">
            {view.template.name}
          </span>
          {view.template.description && (
            <span className="ml-2 text-muted-foreground/70">
              — {view.template.description}
            </span>
          )}
        </div>
        <div className="mt-1 flex items-center gap-2">
          <span className="rounded-full bg-white/10 px-2 py-0.5 text-[10px] uppercase tracking-wider">
            {view.instance.status}
          </span>
          {view.instance.sentToEmail && (
            <span>Sent to {view.instance.sentToEmail}</span>
          )}
        </div>
      </div>

      {readOnly && (
        <div className="rounded-md border border-amber-400/30 bg-amber-400/5 px-3 py-2 text-xs text-amber-200">
          This form is no longer in draft. Defaults can only be edited before
          the mail is sent.
        </div>
      )}

      <div className="flex-1 overflow-auto pr-1">
        <div className="flex flex-col gap-4">
          {view.template.questions.map((q) => {
            if (q.type === "SectionHeader") {
              return (
                <div
                  key={q.id}
                  className="border-b border-white/[0.06] pb-1 text-xs font-semibold uppercase tracking-wider text-muted-foreground/80"
                >
                  {q.label}
                </div>
              );
            }
            const key = q.id.toString();
            return (
              <PrefillField
                key={q.id}
                question={q}
                disabled={readOnly}
                value={values[key]}
                onChange={(v) => setValues((prev) => ({ ...prev, [key]: v }))}
              />
            );
          })}
        </div>
      </div>

      <SheetFooter className="flex justify-between">
        {!readOnly && (
          <Button
            variant="ghost"
            onClick={() => {
              if (confirm("Remove this draft form from the mail?")) {
                destroy.mutate();
              }
            }}
            disabled={destroy.isPending}
          >
            <Trash2 className="h-4 w-4" />
            Remove draft
          </Button>
        )}
        <div className="ml-auto flex gap-2">
          <Button variant="ghost" onClick={onClose}>
            Close
          </Button>
          {!readOnly && (
            <Button
              onClick={() => save.mutate()}
              disabled={save.isPending}
            >
              <Save className="h-4 w-4" />
              Save defaults
            </Button>
          )}
        </div>
      </SheetFooter>
    </div>
  );
}

function PrefillField({
  question,
  value,
  disabled,
  onChange,
}: {
  question: IntakeQuestion;
  value: unknown;
  disabled: boolean;
  onChange: (v: unknown) => void;
}) {
  const inputCn =
    "rounded-md border border-white/[0.08] bg-white/[0.02] px-3 text-sm text-foreground outline-none focus:border-primary/40 disabled:opacity-60";
  switch (question.type) {
    case "ShortText":
      return (
        <label className="flex flex-col gap-1">
          <LabelRow q={question} />
          <input
            type="text"
            disabled={disabled}
            value={typeof value === "string" ? value : ""}
            onChange={(e) => onChange(e.target.value)}
            className={`${inputCn} h-9`}
          />
        </label>
      );
    case "LongText":
      return (
        <label className="flex flex-col gap-1">
          <LabelRow q={question} />
          <textarea
            disabled={disabled}
            value={typeof value === "string" ? value : ""}
            onChange={(e) => onChange(e.target.value)}
            rows={4}
            className={`${inputCn} py-2 resize-y`}
          />
        </label>
      );
    case "Number":
      return (
        <label className="flex flex-col gap-1">
          <LabelRow q={question} />
          <input
            type="number"
            disabled={disabled}
            value={typeof value === "number" ? value : typeof value === "string" ? value : ""}
            onChange={(e) => {
              if (e.target.value === "") onChange(null);
              else {
                const n = Number(e.target.value);
                onChange(Number.isFinite(n) ? n : e.target.value);
              }
            }}
            className={`${inputCn} h-9`}
          />
        </label>
      );
    case "Date":
      return (
        <label className="flex flex-col gap-1">
          <LabelRow q={question} />
          <input
            type="date"
            disabled={disabled}
            value={typeof value === "string" ? value.slice(0, 10) : ""}
            onChange={(e) => onChange(e.target.value || null)}
            className={`${inputCn} h-9`}
          />
        </label>
      );
    case "YesNo":
      return (
        <div className="flex flex-col gap-1">
          <LabelRow q={question} />
          <div className="flex gap-4">
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              <input
                type="radio"
                disabled={disabled}
                checked={value === true}
                onChange={() => onChange(true)}
                className="h-4 w-4"
              />
              Yes
            </label>
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              <input
                type="radio"
                disabled={disabled}
                checked={value === false}
                onChange={() => onChange(false)}
                className="h-4 w-4"
              />
              No
            </label>
            <button
              type="button"
              disabled={disabled}
              onClick={() => onChange(null)}
              className="text-xs text-muted-foreground hover:underline disabled:opacity-60"
            >
              Clear
            </button>
          </div>
        </div>
      );
    case "DropdownSingle": {
      const current = typeof value === "string" && value.length > 0 ? value : "__none";
      return (
        <div className="flex flex-col gap-1">
          <LabelRow q={question} />
          <Select
            disabled={disabled}
            value={current}
            onValueChange={(v) => onChange(v === "__none" ? null : v)}
          >
            <SelectTrigger className="h-9 border-white/10 bg-white/[0.04] text-sm focus:border-white/20 focus:bg-white/[0.06] focus:ring-0 disabled:opacity-60">
              <SelectValue placeholder="—" />
            </SelectTrigger>
            <SelectContent className="border-white/10 bg-popover/80 backdrop-blur-xl">
              <SelectItem value="__none">
                <span className="text-muted-foreground">—</span>
              </SelectItem>
              {question.options.map((o) => (
                <SelectItem key={o.value} value={o.value}>
                  {o.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      );
    }
    case "DropdownMulti": {
      const selected = Array.isArray(value) ? (value as string[]) : [];
      return (
        <div className="flex flex-col gap-1">
          <LabelRow q={question} />
          <div className="flex flex-col gap-1 rounded-md border border-white/[0.08] bg-white/[0.02] p-2">
            {question.options.map((o) => {
              const checked = selected.includes(o.value);
              return (
                <label
                  key={o.value}
                  className="flex items-center gap-2 text-sm text-muted-foreground"
                >
                  <input
                    type="checkbox"
                    disabled={disabled}
                    checked={checked}
                    onChange={(e) => {
                      const next = e.target.checked
                        ? [...selected, o.value]
                        : selected.filter((v) => v !== o.value);
                      onChange(next);
                    }}
                    className="h-4 w-4"
                  />
                  {o.label}
                </label>
              );
            })}
          </div>
        </div>
      );
    }
    default:
      return null;
  }
}

function LabelRow({ q }: { q: IntakeQuestion }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-sm font-medium text-foreground">
        {q.label}
        {q.isRequired && <span className="ml-1 text-red-400">*</span>}
      </span>
      {q.helpText && (
        <span className="text-xs text-muted-foreground">{q.helpText}</span>
      )}
      {q.defaultToken && (
        <span className="text-[11px] text-muted-foreground/60">
          Bound to <code className="text-muted-foreground">{q.defaultToken}</code>
        </span>
      )}
    </div>
  );
}
