import { useState } from "react";
import { Plus } from "lucide-react";
import { Drawer } from "vaul";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";

export function NewTicketFab() {
  const [open, setOpen] = useState(false);

  return (
    <Drawer.Root open={open} onOpenChange={setOpen}>
      <Drawer.Trigger asChild>
        <button
          type="button"
          aria-label="New ticket"
          className="group fixed bottom-6 right-6 z-40 flex h-14 w-14 items-center justify-center rounded-full bg-gradient-to-br from-accent-purple to-accent-blue text-white shadow-[0_10px_30px_-8px_hsl(var(--primary)/0.55)] transition-transform hover:scale-[1.04] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          data-testid="new-ticket-fab"
        >
          <Plus className="h-6 w-6" />
        </button>
      </Drawer.Trigger>
      <Drawer.Portal>
        <Drawer.Overlay className="fixed inset-0 z-50 bg-black/60 backdrop-blur-sm" />
        <Drawer.Content className="fixed inset-x-0 bottom-0 z-50 mx-auto flex max-h-[85vh] max-w-2xl flex-col rounded-t-[var(--radius)] border border-white/10 bg-background/90 backdrop-blur-xl">
          <Drawer.Title className="sr-only">New ticket</Drawer.Title>
          <Drawer.Description className="sr-only">
            Ticket creation form — not implemented yet.
          </Drawer.Description>
          <div className="mx-auto mt-3 h-1 w-10 rounded-full bg-white/20" aria-hidden />
          <div className="space-y-3 p-8 text-center">
            <h2 className="font-display text-display-sm font-semibold">New ticket</h2>
            <p className="text-sm text-muted-foreground">
              The ticket form, categories and SLA picker arrive in v0.0.5.
            </p>
            <Badge variant="secondary" className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
              Coming in v0.0.5
            </Badge>
          </div>
          <div className="flex justify-end gap-2 border-t border-white/10 p-4">
            <Button variant="ghost" onClick={() => setOpen(false)}>
              Close
            </Button>
          </div>
        </Drawer.Content>
      </Drawer.Portal>
    </Drawer.Root>
  );
}
