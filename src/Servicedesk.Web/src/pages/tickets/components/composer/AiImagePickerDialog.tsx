import { Sparkles } from "lucide-react";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import type { ClaudeTicketImage } from "@/lib/claude-api";

export function AiImagePickerDialog({
  open,
  onOpenChange,
  images,
  selectedIds,
  onToggle,
  onGenerate,
  busy,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  images: ClaudeTicketImage[];
  selectedIds: string[];
  onToggle: (id: string) => void;
  onGenerate: () => void;
  busy: boolean;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Select screenshots for AI</DialogTitle>
          <DialogDescription>
            Choose which screenshots to include with your AI proposal request.
            Uncheck any that are not relevant to reduce cost and improve focus.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3 max-h-80 overflow-y-auto">
          {images.map((img) => {
            const checked = selectedIds.includes(img.id);
            return (
              <label
                key={img.id}
                className="flex items-center gap-3 cursor-pointer rounded-lg border border-glass p-2 hover:bg-glass-hover transition-colors"
              >
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => onToggle(img.id)}
                  className="h-4 w-4 shrink-0 accent-violet-500"
                />
                <img
                  src={img.url}
                  alt={img.filename}
                  className="h-12 w-12 shrink-0 rounded object-cover border border-glass"
                />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-foreground">
                    {img.filename}
                  </p>
                  <p className="text-xs text-muted-foreground/60">
                    {img.mimeType} · {(img.sizeBytes / 1024).toFixed(0)} KB
                  </p>
                </div>
              </label>
            );
          })}
        </div>

        <DialogFooter>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onOpenChange(false)}
            disabled={busy}
          >
            Cancel
          </Button>
          <Button
            size="sm"
            disabled={busy}
            onClick={onGenerate}
            className="gap-1.5"
          >
            <Sparkles className="h-3.5 w-3.5" />
            Generate proposal
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
