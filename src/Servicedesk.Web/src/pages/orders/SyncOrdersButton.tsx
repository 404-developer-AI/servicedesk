import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { RefreshCw } from "lucide-react";
import { ordersApi } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

/// "Sync orders" trigger surfaced in the ticket status panel for users with
/// the Orders feature flag. Fires an incremental Adsolut orders sync (the
/// worker only pulls what changed). Gating on the flag is the caller's job —
/// this component just renders the button and fires the request.
export function SyncOrdersButton({ className }: { className?: string }) {
  const sync = useMutation({
    mutationFn: () => ordersApi.sync(),
    onSuccess: () => toast.success("Orders sync started — pulls only what changed."),
    onError: () => toast.error("Could not start the orders sync. Is the Adsolut Orders pull enabled?"),
  });

  return (
    <Button
      size="sm"
      variant="outline"
      className={cn("h-8 w-full gap-1.5", className)}
      onClick={() => sync.mutate()}
      disabled={sync.isPending}
    >
      <RefreshCw className={cn("h-3.5 w-3.5", sync.isPending && "animate-spin")} />
      Sync orders
    </Button>
  );
}
