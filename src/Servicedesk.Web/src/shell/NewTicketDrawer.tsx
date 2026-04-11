import { useState, type ReactNode } from "react";
import { Drawer } from "vaul";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";

// Drawer + stub content for "new ticket". The trigger is passed as `children`
// so the same drawer can be mounted from anywhere in the shell (currently the
// Sidebar's status block). The real ticket form lands in v0.0.5.
export function NewTicketDrawer({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);

  return (
    <Drawer.Root open={open} onOpenChange={setOpen}>
      <Drawer.Trigger asChild>{children}</Drawer.Trigger>
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
            <Badge
              variant="secondary"
              className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground"
            >
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
