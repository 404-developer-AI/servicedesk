import { Receipt } from "lucide-react";

/// Timesheet → Adsolut tab. Only mounted when the Adsolut integration is
/// connected AND the current user has the "Adsolut Timesheet" feature flag
/// (both enforced in `TimesheetPage`). For now this is a placeholder shell —
/// pulling the actual Adsolut data lands in a follow-up step.
export function TimesheetTabAdsolut() {
  return (
    <div className="glass-panel flex flex-1 flex-col items-center justify-center gap-3 p-12 text-center">
      <div className="flex h-12 w-12 items-center justify-center rounded-full border border-glass bg-glass">
        <Receipt className="h-5 w-5 text-muted-foreground" />
      </div>
      <div className="space-y-1">
        <h2 className="text-display-sm font-semibold text-foreground">Adsolut</h2>
        <p className="max-w-md text-sm text-muted-foreground">
          The Adsolut integration is connected. Pulling timesheet data from
          Adsolut will be available here in a next step.
        </p>
      </div>
    </div>
  );
}
