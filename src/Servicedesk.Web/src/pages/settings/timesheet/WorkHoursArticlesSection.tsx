import * as React from "react";
import { Boxes } from "lucide-react";
import { Button } from "@/components/ui/button";
import { AdsolutWorkHoursDialog } from "../AdsolutWorkHoursDialog";
import { SectionHeader, FieldRow } from "./timesheetSettingsShared";

// ---- Work-hours articles (Adsolut "VK Werkuren" matching) -------------

/// Opens the scrollable manager where an admin flags which Adsolut catalogue
/// products count as billable work hours. Drives the Timesheet → Adsolut "VK
/// Werkuren" column + the registered-hours match (hardware excluded).
export function WorkHoursArticlesSection() {
  const [open, setOpen] = React.useState(false);
  return (
    <section className="glass-card p-6">
      <SectionHeader
        icon={<Boxes className="h-5 w-5" />}
        title="Work-hours articles"
        description="Choose which Adsolut catalogue products count as billable work hours. The Timesheet → Adsolut 'VK Werkuren' total — and the registered-hours match — sums only the receipt lines whose product is flagged here, so hardware no longer skews the comparison. The catalogue is mirrored alongside the sales receipts."
      />
      <FieldRow
        label="Manage work-hours articles"
        hint="Search, filter and tick the products that represent work hours. Changes apply to the matching immediately."
      >
        <Button size="sm" variant="ghost" className="h-9 gap-1.5" onClick={() => setOpen(true)}>
          <Boxes className="h-4 w-4" />
          Manage articles
        </Button>
      </FieldRow>
      <AdsolutWorkHoursDialog open={open} onClose={() => setOpen(false)} />
    </section>
  );
}
