import * as React from "react";
import { Layers, Megaphone, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { AddToCampaignDialog } from "@/components/campaigns/AddToCampaignDialog";
import { cn } from "@/lib/utils";
import type { TicketListItem } from "@/lib/ticket-api";

/// The toolbar above the list while rows are selected: Bulk edit (when bulk
/// actions are on) and Add to campaign (Campaigns flag holders).
export function BulkSelectionBar({
  selectedItems,
  bulkEnabled,
  bulkMax,
  campaignsEnabled,
  onBulkEdit,
  onClear,
}: {
  selectedItems: TicketListItem[];
  bulkEnabled: boolean;
  bulkMax: number;
  campaignsEnabled: boolean;
  onBulkEdit: () => void;
  onClear: () => void;
}) {
  const [campaignOpen, setCampaignOpen] = React.useState(false);
  const overCap = bulkEnabled && selectedItems.length > bulkMax;

  return (
    <div
      className={cn(
        "glass-panel flex shrink-0 items-center justify-between gap-3 px-3 py-2 ring-1 transition-colors",
        overCap ? "ring-amber-500/40" : "ring-primary/30",
      )}
      role="toolbar"
      aria-label="Bulk actions"
    >
      <div className="flex min-w-0 items-center gap-2 text-xs">
        <span className="rounded-md bg-primary/15 px-2 py-0.5 font-semibold text-primary">
          {selectedItems.length} selected
        </span>
        {overCap ? (
          <span className="truncate text-amber-300">
            Bulk actions are limited to {bulkMax} tickets at a time — deselect some to continue.
          </span>
        ) : (
          <span className="truncate text-muted-foreground">
            Shift-click a checkbox to select a range.
          </span>
        )}
      </div>
      <div className="flex shrink-0 items-center gap-1.5">
        {campaignsEnabled && (
          <Button size="sm" variant="outline" className="h-8 gap-1.5" onClick={() => setCampaignOpen(true)}>
            <Megaphone className="h-3.5 w-3.5" />
            Add to campaign…
          </Button>
        )}
        {bulkEnabled && (
          <Button size="sm" className="h-8 gap-1.5" disabled={overCap} onClick={onBulkEdit}>
            <Layers className="h-3.5 w-3.5" />
            Bulk edit…
          </Button>
        )}
        <Button
          size="sm"
          variant="ghost"
          className="h-8 gap-1 px-2 text-xs text-muted-foreground"
          onClick={onClear}
        >
          <X className="h-3.5 w-3.5" />
          Clear
        </Button>
      </div>
      {campaignsEnabled && (
        <AddToCampaignDialog
          open={campaignOpen}
          onOpenChange={setCampaignOpen}
          ticketIds={selectedItems.map((t) => t.id)}
        />
      )}
    </div>
  );
}
