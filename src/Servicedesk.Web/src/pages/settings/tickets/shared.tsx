import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ApiError } from "@/lib/api";
import { errorMessage } from "@/lib/http";
import { Skeleton } from "@/components/ui/skeleton";

export function TableShell({ children }: { children: React.ReactNode }) {
  return (
    <section className="glass-card overflow-hidden">
      <table className="w-full text-left text-sm">{children}</table>
    </section>
  );
}

export function ColorSwatch({ color }: { color: string }) {
  return (
    <span
      className="inline-block h-4 w-4 rounded-sm border border-glass"
      style={{ backgroundColor: color }}
      aria-hidden
    />
  );
}

export function useDeleteHandler(
  remove: (id: string) => Promise<void>,
  invalidateKey: unknown[],
  label: string,
) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: remove,
    onSuccess: (_result, deletedId) => {
      qc.setQueryData<Array<{ id: string }>>(invalidateKey, (old) =>
        old?.filter((item) => item.id !== deletedId),
      );
      qc.invalidateQueries({ queryKey: invalidateKey });
      toast.success(`${label} deleted`);
    },
    onError: (err) => {
      const fallback =
        err instanceof ApiError && err.status === 409
          ? "Still in use or system-protected. Reassign tickets or rename instead."
          : `Failed to delete ${label.toLowerCase()}`;
      toast.error(errorMessage(err, fallback));
    },
  });
}

export function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs text-muted-foreground">{label}</span>
      {children}
      {hint && <span className="text-[11px] text-muted-foreground/60">{hint}</span>}
    </label>
  );
}

export function LoadingSkeleton() {
  return (
    <div className="glass-card space-y-2 p-4">
      {Array.from({ length: 5 }).map((_, i) => (
        <Skeleton key={i} className="h-10 w-full" />
      ))}
    </div>
  );
}
