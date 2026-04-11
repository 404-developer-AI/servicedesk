import { useEffect } from "react";
import { useNavigate } from "@tanstack/react-router";
import { Command } from "cmdk";
import { Dialog, DialogContent, DialogTitle, DialogDescription } from "@/components/ui/dialog";
import { visibleNavItems } from "@/shell/navItems";
import { useCurrentRole } from "@/hooks/useCurrentRole";

type CommandPaletteProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
};

export function CommandPalette({ open, onOpenChange }: CommandPaletteProps) {
  const navigate = useNavigate();
  const role = useCurrentRole();
  const items = visibleNavItems(role);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        onOpenChange(!open);
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [open, onOpenChange]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="overflow-hidden border-white/10 bg-background/80 p-0 backdrop-blur-xl sm:max-w-lg">
        <DialogTitle className="sr-only">Command palette</DialogTitle>
        <DialogDescription className="sr-only">
          Jump to pages and run commands.
        </DialogDescription>
        <Command
          className="flex flex-col"
          loop
          filter={(value, search) =>
            value.toLowerCase().includes(search.toLowerCase()) ? 1 : 0
          }
        >
          <div className="border-b border-white/10 px-4 py-3">
            <Command.Input
              placeholder="Jump to a page…"
              className="w-full bg-transparent text-sm outline-none placeholder:text-muted-foreground"
            />
          </div>
          <Command.List className="max-h-[320px] overflow-y-auto p-2">
            <Command.Empty className="px-3 py-6 text-center text-xs text-muted-foreground">
              Nothing matches that yet — search lands in v0.0.6.
            </Command.Empty>
            <Command.Group heading="Navigation" className="text-[10px] uppercase tracking-[0.18em] text-muted-foreground [&_[cmdk-group-heading]]:px-3 [&_[cmdk-group-heading]]:py-2">
              {items.map((item) => {
                const Icon = item.icon;
                return (
                  <Command.Item
                    key={item.to}
                    value={item.label}
                    onSelect={() => {
                      onOpenChange(false);
                      navigate({ to: item.to });
                    }}
                    className="flex cursor-pointer items-center gap-3 rounded-md px-3 py-2 text-sm text-muted-foreground aria-selected:bg-white/[0.06] aria-selected:text-foreground"
                  >
                    <Icon className="h-4 w-4" />
                    <span>{item.label}</span>
                    <span className="ml-auto text-[10px] text-muted-foreground/70">{item.to}</span>
                  </Command.Item>
                );
              })}
            </Command.Group>
          </Command.List>
        </Command>
      </DialogContent>
    </Dialog>
  );
}
