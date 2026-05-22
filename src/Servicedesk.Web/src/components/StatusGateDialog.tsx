import * as React from "react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { GateQuestion, StatusGateMatch } from "@/lib/ticket-api";

type Props = {
  /// Null hides the dialog. The parent passes the next gate in the
  /// sequence (or null when done) so a single component instance can
  /// walk through multiple matching gates without remount churn.
  gate: StatusGateMatch | null;
  /// Fires when the agent clicks the bottom confirm button. Receives a
  /// `{key: value}` map keyed by every question's `key` — the typed
  /// text for free-text fields, the clicked Yes-button label for
  /// yes/no questions. Questions without an active answer are omitted.
  onConfirm: (answers: Record<string, string>) => void;
  /// Fires when the agent clicks the bottom cancel button, clicks a
  /// "No" button on any question, or dismisses the dialog (overlay /
  /// Esc). The parent rolls back the dropdown state and surfaces a
  /// "status not changed" toast.
  onCancel: () => void;
};

/// v0.0.42 — confirmation dialog for an in-flight status change that
/// matched a gate trigger. Renders title + optional message + a list of
/// inline questions (free-text or yes/no rows) + bottom confirm/cancel
/// buttons. Clicking any visible "No" button cancels the gate
/// immediately; "Yes" locks that question to its positive answer. The
/// bottom Confirm button enables only once every question that demands
/// an answer has one. The dialog is controlled by `gate` so the parent
/// can swap in the next gate without an unmount round-trip.
export function StatusGateDialog({ gate, onConfirm, onCancel }: Props) {
  const [textAnswers, setTextAnswers] = React.useState<Record<string, string>>({});
  const [yesnoAnswers, setYesnoAnswers] = React.useState<Record<string, string>>({});

  // Reset both answer maps each time a new gate slot is opened so
  // answers from gate #1 do not pre-fill gate #2.
  React.useEffect(() => {
    if (gate) {
      setTextAnswers({});
      setYesnoAnswers({});
    }
  }, [gate?.triggerId]);

  if (!gate) return null;

  const canConfirm = isGateSatisfied(gate.questions, textAnswers, yesnoAnswers);

  function handleConfirm() {
    if (!gate) return;
    const merged: Record<string, string> = {};
    for (const q of gate.questions) {
      if (q.type === "text") {
        const v = (textAnswers[q.key] ?? "").trim();
        if (v.length > 0) merged[q.key] = v;
      } else {
        const v = yesnoAnswers[q.key];
        if (v) merged[q.key] = v;
      }
    }
    onConfirm(merged);
  }

  return (
    <Dialog
      open={!!gate}
      onOpenChange={(o) => {
        if (!o) onCancel();
      }}
    >
      <DialogContent className="sm:max-w-lg border border-white/10 bg-[hsl(240_10%_5%/0.96)] backdrop-blur-xl">
        <DialogHeader>
          <DialogTitle>{gate.title}</DialogTitle>
          {gate.message ? (
            <DialogDescription className="whitespace-pre-wrap text-sm text-muted-foreground">
              {gate.message}
            </DialogDescription>
          ) : null}
        </DialogHeader>
        {gate.questions.length > 0 && (
          <div className="space-y-3.5">
            {gate.questions.map((q) => (
              <QuestionRow
                key={q.key}
                question={q}
                textValue={textAnswers[q.key] ?? ""}
                yesnoValue={yesnoAnswers[q.key] ?? null}
                onTextChange={(v) =>
                  setTextAnswers((prev) => ({ ...prev, [q.key]: v }))
                }
                onYesClick={(label) =>
                  setYesnoAnswers((prev) => ({ ...prev, [q.key]: label }))
                }
                onNoClick={onCancel}
              />
            ))}
          </div>
        )}
        <DialogFooter className="gap-2 sm:gap-2">
          <Button variant="ghost" onClick={onCancel}>
            {gate.cancelLabel}
          </Button>
          <Button
            onClick={handleConfirm}
            disabled={!canConfirm}
            className="bg-gradient-to-r from-violet-600 to-indigo-600 hover:from-violet-500 hover:to-indigo-500 text-white shadow-[0_0_20px_rgba(124,58,237,0.3)]"
          >
            {gate.confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function QuestionRow({
  question,
  textValue,
  yesnoValue,
  onTextChange,
  onYesClick,
  onNoClick,
}: {
  question: GateQuestion;
  textValue: string;
  yesnoValue: string | null;
  onTextChange: (v: string) => void;
  onYesClick: (label: string) => void;
  onNoClick: () => void;
}) {
  if (question.type === "text") {
    return (
      <div className="space-y-1.5">
        <label className="text-xs font-medium text-muted-foreground">
          {question.label}
          {question.required && <span className="ml-1 text-rose-400">*</span>}
        </label>
        <textarea
          value={textValue}
          onChange={(e) => onTextChange(e.target.value)}
          rows={3}
          className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
        />
      </div>
    );
  }
  const selected = yesnoValue !== null && yesnoValue === question.yesLabel;
  return (
    <div className="space-y-1.5">
      <span className="text-xs font-medium text-muted-foreground">
        {question.label}
      </span>
      <div className="flex flex-wrap gap-2">
        {question.yesLabel !== null && (
          <button
            type="button"
            onClick={() => onYesClick(question.yesLabel!)}
            className={cn(
              "rounded-md border px-3 py-1.5 text-sm transition-colors",
              selected
                ? "border-emerald-400/60 bg-emerald-500/20 text-emerald-100 shadow-[0_0_12px_rgba(16,185,129,0.25)]"
                : "border-white/10 bg-white/[0.04] text-foreground/80 hover:border-emerald-400/30 hover:bg-emerald-500/10",
            )}
          >
            {question.yesLabel}
          </button>
        )}
        {question.noLabel !== null && (
          <button
            type="button"
            onClick={onNoClick}
            className="rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground/80 transition-colors hover:border-rose-400/30 hover:bg-rose-500/10 hover:text-rose-100"
          >
            {question.noLabel}
          </button>
        )}
      </div>
    </div>
  );
}

/// True when every question that demands an answer has one. Mirrors
/// the server-side `GateAnswersSatisfyQuestions` so the bottom Confirm
/// button stays disabled until the PATCH would actually succeed.
///   * Required text questions need a non-whitespace value.
///   * Yes/No questions need a positive (Yes-button) answer when the
///     Yes button is visible — clicking No has already cancelled the
///     dialog, so we only check the visible-Yes case.
///   * Yes/No questions with only a No button do not block confirm:
///     the agent can either cancel via that button or proceed via the
///     bottom Confirm button.
function isGateSatisfied(
  questions: GateQuestion[],
  textAnswers: Record<string, string>,
  yesnoAnswers: Record<string, string>,
): boolean {
  for (const q of questions) {
    if (q.type === "text") {
      if (q.required && !(textAnswers[q.key] ?? "").trim()) return false;
    } else {
      if (q.yesLabel !== null && yesnoAnswers[q.key] !== q.yesLabel) return false;
    }
  }
  return true;
}
