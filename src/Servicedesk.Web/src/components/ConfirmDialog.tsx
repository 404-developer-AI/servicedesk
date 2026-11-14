import * as React from "react";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

/// App-wide confirmation dialog — the themed replacement for the browser's
/// `window.confirm()`. One instance lives under <ConfirmProvider> (mounted
/// in main.tsx); any component asks via the promise API:
///
///   const confirm = useConfirm();
///   if (await confirm({ title: "Delete queue", description: `Delete "${q.name}"?`, tone: "destructive" }))
///     del.mutate(q.id);
///
/// Resolves `true` on confirm, `false` on cancel / Escape / outside click.

export type ConfirmOptions = {
  title: string;
  description?: React.ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  /// "destructive" for deletes and other irreversible actions.
  tone?: "default" | "destructive";
};

type Pending = ConfirmOptions & { resolve: (ok: boolean) => void };

const ConfirmContext = React.createContext<((opts: ConfirmOptions) => Promise<boolean>) | null>(null);

export function ConfirmProvider({ children }: { children: React.ReactNode }) {
  const [pending, setPending] = React.useState<Pending | null>(null);

  const confirm = React.useCallback(
    (opts: ConfirmOptions) =>
      new Promise<boolean>((resolve) => {
        // A second request while one is open cancels the first.
        setPending((prev) => {
          prev?.resolve(false);
          return { ...opts, resolve };
        });
      }),
    [],
  );

  const settle = (ok: boolean) => {
    pending?.resolve(ok);
    setPending(null);
  };

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <Dialog open={pending !== null} onOpenChange={(open) => !open && settle(false)}>
        <DialogContent className="max-w-sm sd-confirm-dialog">
          <DialogHeader>
            <DialogTitle>{pending?.title}</DialogTitle>
            {pending?.description ? <DialogDescription>{pending.description}</DialogDescription> : null}
          </DialogHeader>
          <DialogFooter>
            <Button variant="ghost" onClick={() => settle(false)}>
              {pending?.cancelLabel ?? "Cancel"}
            </Button>
            <Button
              variant={pending?.tone === "destructive" ? "destructive" : "default"}
              onClick={() => settle(true)}
              autoFocus
            >
              {pending?.confirmLabel ?? (pending?.tone === "destructive" ? "Delete" : "Confirm")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </ConfirmContext.Provider>
  );
}

export function useConfirm(): (opts: ConfirmOptions) => Promise<boolean> {
  const ctx = React.useContext(ConfirmContext);
  if (!ctx) throw new Error("useConfirm must be used inside <ConfirmProvider>.");
  return ctx;
}
