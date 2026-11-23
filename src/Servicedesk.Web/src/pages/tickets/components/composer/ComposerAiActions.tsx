import { FileText, ListChecks, Sparkles } from "lucide-react";
import { cn } from "@/lib/utils";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";
import { AiImagePickerDialog } from "./AiImagePickerDialog";
import type { useComposerAi } from "./useComposerAi";

type Ai = ReturnType<typeof useComposerAi>;

/// The three AI buttons under the editor.
export function ComposerAiButtons({ ai }: { ai: Ai }) {
  const { aiBusy, aiLoading, summaryLoading, statusLoading, handleAskAi, handleSummary, runAiStatus } = ai;
  return (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <button
            type="button"
            disabled={aiBusy}
            onClick={handleAskAi}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
              "border-amber-400/30 bg-amber-400/10 text-amber-200 hover:bg-amber-400/15",
              aiLoading && "opacity-50 cursor-not-allowed",
            )}
            title="Generate a draft internal note using Claude AI based on the ticket context"
          >
            <Sparkles className="h-3.5 w-3.5" />
            {aiLoading ? "Generating…" : "Analyze & propose a solution by AI"}
          </button>
          <button
            type="button"
            disabled={aiBusy}
            onClick={handleSummary}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
              "border-amber-400/30 bg-amber-400/10 text-amber-200 hover:bg-amber-400/15",
              summaryLoading && "opacity-50 cursor-not-allowed",
            )}
            title="Generate a compact customer-facing summary of this ticket from the summary template, as a draft internal note"
          >
            <FileText className="h-3.5 w-3.5" />
            {summaryLoading ? "Summarizing…" : "Summary"}
          </button>
          <TooltipProvider delayDuration={150}>
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  disabled={aiBusy}
                  onClick={() => void runAiStatus()}
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
                    "border-amber-400/30 bg-amber-400/10 text-amber-200 hover:bg-amber-400/15",
                    statusLoading && "opacity-50 cursor-not-allowed",
                  )}
                >
                  <ListChecks className="h-3.5 w-3.5" />
                  {statusLoading ? "Writing status…" : "Status update by AI"}
                </button>
              </TooltipTrigger>
              <TooltipContent side="top" className="max-w-xs space-y-1 leading-relaxed">
                <p>
                  Posts a short brief of where this ticket stands — current status, what
                  happened, what it is waiting on and the next steps — straight onto the
                  ticket as an internal note headed “Summary by AI”.
                </p>
                <p>Internal only: never visible to the customer, no mail is sent.</p>
                <p>Whatever you are typing in the composer stays where it is.</p>
                <p>Screenshots are not sent to the AI.</p>
              </TooltipContent>
            </Tooltip>
          </TooltipProvider>
        </div>
  );
}

/// The summary replace-confirm and the screenshot picker.
export function ComposerAiDialogs({ ai }: { ai: Ai }) {
  const {
    summaryConfirmOpen,
    setSummaryConfirmOpen,
    runAiSummary,
    aiImages,
    aiImageDialogOpen,
    setAiImageDialogOpen,
    aiSelectedIds,
    setAiSelectedIds,
    runAiProposal,
    aiLoading,
  } = ai;
  return (
    <>
      <Dialog open={summaryConfirmOpen} onOpenChange={setSummaryConfirmOpen}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>Replace the current draft?</DialogTitle>
            <DialogDescription>
              The composer already contains text. The AI summary will replace it.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button size="sm" variant="ghost" onClick={() => setSummaryConfirmOpen(false)}>
              Cancel
            </Button>
            <Button
              size="sm"
              className="gap-1.5"
              onClick={() => {
                setSummaryConfirmOpen(false);
                void runAiSummary();
              }}
            >
              <FileText className="h-3.5 w-3.5" />
              Replace with summary
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {aiImages !== null && (
        <AiImagePickerDialog
          open={aiImageDialogOpen}
          onOpenChange={setAiImageDialogOpen}
          images={aiImages}
          selectedIds={aiSelectedIds}
          onToggle={(id) =>
            setAiSelectedIds((prev) =>
              prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id],
            )
          }
          onGenerate={async () => {
            setAiImageDialogOpen(false);
            await runAiProposal(aiSelectedIds);
          }}
          busy={aiLoading}
        />
      )}
    </>
  );
}
