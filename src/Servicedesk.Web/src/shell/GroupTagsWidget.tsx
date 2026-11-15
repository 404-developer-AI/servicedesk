import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Check, ExternalLink, UserCheck, Users } from "lucide-react";
import { toast } from "sonner";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import { errorMessage } from "@/lib/http";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { GROUP_TAGS_KEY, groupTagApi, type GroupTag } from "@/lib/group-tags-api";
import { PulseDot } from "@/shell/PulseDot";

/// v0.1.54 — the Group tags block under the personal Tags block. Every
/// member of a group sees the same tags; one member acknowledging a tag
/// clears it for all (the server pushes GroupTagsChanged). Tags of a
/// personal-acknowledgement group ("Everyone confirms") are marked as such
/// and the check only marks them read for the caller. Clicking a tag
/// opens the ticket but does NOT acknowledge it, and there is deliberately
/// no "Ack all". Hidden for agents who belong to no active group.
export function GroupTagsWidget({ collapsed, className }: { collapsed: boolean; className?: string }) {
  const { data, isLoading } = useQuery({
    queryKey: [...GROUP_TAGS_KEY, "pending"],
    queryFn: () => groupTagApi.pending(),
    refetchOnWindowFocus: true,
    staleTime: 30_000,
  });
  if (!data?.isMember) return null;
  const items = data.items;
  const count = items.length;
  const hasOpen = count > 0;
  const title = hasOpen ? `${count} open group tag${count === 1 ? "" : "s"}` : "No open group tags";

  return (
    <div className={cn("sd-group-tags", className ?? "mx-3 mb-2", collapsed && "flex justify-center")}>
      <Popover>
        <PopoverTrigger asChild>
          {collapsed ? (
            <button type="button" title={title}
              className={cn(
                "relative flex h-9 w-9 items-center justify-center rounded-lg border border-glass bg-glass text-muted-foreground transition-colors",
                "hover:bg-glass-hover hover:text-foreground",
                hasOpen && "text-primary",
              )}>
              <Users className="h-4 w-4" />
              {hasOpen ? <PulseDot count={count} /> : null}
            </button>
          ) : (
            <button type="button" title={title}
              className="flex h-8 w-full min-w-0 items-center gap-1 rounded-lg border border-glass bg-glass px-2 text-left transition-colors hover:bg-glass-hover">
              <div className="relative shrink-0">
                <Users className={cn("h-3.5 w-3.5", hasOpen ? "text-primary" : "text-muted-foreground")} />
                {hasOpen ? <PulseDot count={null} /> : null}
              </div>
              <span className="truncate text-[10px] font-medium uppercase tracking-[0.06em] text-muted-foreground">Group tags</span>
              {hasOpen ? (
                <span className="ml-auto shrink-0 rounded-full border border-primary/30 bg-primary/12 px-1.5 py-px text-[10px] font-medium text-primary">
                  {count}
                </span>
              ) : null}
            </button>
          )}
        </PopoverTrigger>
        <PopoverContent side="right" align="end" className="w-88 p-0">
          <GroupTagPanel items={items} loading={isLoading} />
        </PopoverContent>
      </Popover>
    </div>
  );
}

function GroupTagPanel({ items, loading }: { items: GroupTag[]; loading: boolean }) {
  const qc = useQueryClient();
  const navigate = useNavigate();
  const zone = useServerTimeZone();

  const ack = useMutation({
    mutationFn: (id: string) => groupTagApi.ack(id),
    // Optimistically drop the row; the server push refreshes every member.
    onMutate: (id) => {
      qc.setQueryData<{ isMember: boolean; items: GroupTag[] }>([...GROUP_TAGS_KEY, "pending"],
        (old) => (old ? { ...old, items: old.items.filter((t) => t.id !== id) } : old));
    },
    onError: (e) => toast.error(errorMessage(e, "Could not acknowledge the tag")),
    onSettled: () => void qc.invalidateQueries({ queryKey: GROUP_TAGS_KEY }),
  });

  const open = (t: GroupTag) =>
    void navigate({ to: "/tickets/$ticketId", params: { ticketId: t.ticketId }, hash: `event-${t.eventId}` });

  return (
    <div className="flex flex-col">
      {items.length === 0 ? (
        <div className="px-3 py-6 text-center text-xs text-muted-foreground/70">
          {loading ? "Loading…" : "No open group tags"}
        </div>
      ) : (
        <div className="max-h-[320px] divide-y divide-glass overflow-y-auto">
          {items.map((t) => {
            const who = t.sourceUserEmail?.split("@")[0] ?? "agent";
            const Icon = t.personalAck ? UserCheck : Users;
            return (
              <div key={t.id} className="flex items-start gap-2 px-3 py-2 transition-colors hover:bg-glass-hover">
                <button type="button" onClick={() => open(t)} className="flex min-w-0 flex-1 items-start gap-2 text-left"
                  title={t.personalAck
                    ? "Open the ticket (the tag stays until you mark it as read)"
                    : "Open the ticket (the tag stays until someone acknowledges it)"}>
                  <Icon className="mt-0.5 h-3.5 w-3.5 shrink-0 text-primary" />
                  <div className="min-w-0 flex-auto">
                    <div className="flex items-center gap-1 text-xs">
                      <span className="truncate font-medium text-foreground">{t.groupName}</span>
                      <span className="font-mono text-[11px] text-muted-foreground">#{t.ticketNumber}</span>
                      {t.personalAck ? (
                        <span className="ml-auto shrink-0 rounded-md border border-primary/30 bg-primary/10 px-1.5 py-px text-[10px] font-medium text-primary"
                          title="Every member marks this tag as read personally">
                          Everyone confirms
                        </span>
                      ) : null}
                    </div>
                    <div className="mt-0.5 truncate text-[11px] text-muted-foreground/90">
                      {t.previewText || t.ticketSubject}
                    </div>
                    <div className="mt-0.5 text-[10px] text-muted-foreground/60">
                      @{who} · {formatDateTime(t.createdUtc, zone)}
                    </div>
                  </div>
                </button>
                <button type="button" onClick={() => ack.mutate(t.id)} disabled={ack.isPending}
                  title={t.personalAck ? "Mark as read (only for you)" : "Acknowledge for the whole group"}
                  aria-label={t.personalAck
                    ? `Mark tag on #${t.ticketNumber} from ${t.groupName} as read for yourself`
                    : `Acknowledge tag on #${t.ticketNumber} for ${t.groupName}`}
                  className="shrink-0 rounded-md border border-glass p-1 text-muted-foreground transition-colors hover:border-primary/40 hover:bg-primary/10 hover:text-primary">
                  <Check className="h-3.5 w-3.5" />
                </button>
              </div>
            );
          })}
        </div>
      )}
      <div className="flex items-center justify-end border-t border-glass px-3 py-2 text-[11px]">
        <button type="button" onClick={() => navigate({ to: "/profile/group-tags" })}
          className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-muted-foreground/80 transition-colors hover:bg-glass-hover hover:text-foreground">
          All group tags <ExternalLink className="h-3 w-3" />
        </button>
      </div>
    </div>
  );
}
