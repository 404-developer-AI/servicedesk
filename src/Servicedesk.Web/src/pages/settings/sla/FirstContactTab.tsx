import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Save } from "lucide-react";
import { toast } from "sonner";
import { settingsApi } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";

const TRIGGERS = [
  { key: "Mail", label: "Outbound mail", description: "A Mail event (reply to customer)" },
  { key: "Comment", label: "Public reply/comment", description: "Agent writes a non-internal comment" },
  { key: "Note", label: "Internal note", description: "Agent writes an internal note (touch, not a reply)" },
  { key: "StatusChange", label: "Status change", description: "Agent moves ticket out of 'New'" },
  { key: "AssignmentChange", label: "Assignee change", description: "Ticket gets picked up / reassigned" },
  { key: "QueueChange", label: "Queue change", description: "Ticket is moved to another queue" },
];

const KEY = "Sla.FirstContact.Triggers";
const LIST_KEY = ["settings", "list", "Sla"] as const;

export function FirstContactTab() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: LIST_KEY, queryFn: () => settingsApi.list("Sla") });
  const [selected, setSelected] = useState<string[]>([]);
  const [pauseOnPending, setPauseOnPending] = useState(true);

  useEffect(() => {
    if (!q.data) return;
    const entry = q.data.find((e) => e.key === KEY);
    if (entry) {
      try {
        const arr = JSON.parse(entry.value);
        if (Array.isArray(arr)) setSelected(arr);
      } catch {
        setSelected(["Mail", "Comment"]);
      }
    }
    const pause = q.data.find((e) => e.key === "Sla.PauseOnPending");
    if (pause) setPauseOnPending(pause.value === "true");
  }, [q.data]);

  const save = useMutation({
    mutationFn: async () => {
      await settingsApi.update(KEY, JSON.stringify(selected));
      await settingsApi.update("Sla.PauseOnPending", String(pauseOnPending));
    },
    onSuccess: () => {
      toast.success("First-contact rules saved");
      qc.invalidateQueries({ queryKey: LIST_KEY });
    },
    onError: (e: Error) => toast.error(`Save failed: ${e.message}`),
  });

  if (q.isLoading) return <Skeleton className="h-48 w-full" />;

  function toggle(key: string) {
    setSelected((prev) => (prev.includes(key) ? prev.filter((k) => k !== key) : [...prev, key]));
  }

  return (
    <div className="flex flex-col gap-4">
      <p className="text-xs text-muted-foreground">
        Select which agent actions stop the first-response timer. If nothing is selected, the
        timer can only be stopped by outbound mail or a public reply.
      </p>

      <div className="space-y-2">
        {TRIGGERS.map((t) => (
          <label
            key={t.key}
            className="flex cursor-pointer items-start gap-3 rounded-md border border-white/[0.06] bg-white/[0.02] p-3 hover:bg-white/[0.04]"
          >
            <input
              type="checkbox"
              checked={selected.includes(t.key)}
              onChange={() => toggle(t.key)}
              className="mt-1"
            />
            <div>
              <div className="text-sm font-medium text-foreground">{t.label}</div>
              <div className="text-xs text-muted-foreground">{t.description}</div>
            </div>
          </label>
        ))}
      </div>

      <label className="flex items-center gap-2 rounded-md border border-white/[0.06] bg-white/[0.02] p-3 text-sm">
        <input type="checkbox" checked={pauseOnPending} onChange={(e) => setPauseOnPending(e.target.checked)} />
        Pause SLA timers while ticket is in <span className="font-medium">Pending</span> (waiting on customer)
      </label>

      <div>
        <Button onClick={() => save.mutate()} disabled={save.isPending}>
          {save.isPending ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Save className="mr-2 h-4 w-4" />}
          Save
        </Button>
      </div>
    </div>
  );
}
