import { useState } from "react";
import {
  Clock,
  Users as UsersIcon,
  CalendarRange,
  Receipt,
  ClipboardCheck,
  FileX2,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import { useAuth } from "@/auth/authStore";
import { useTimesheetManagerRealtime } from "@/hooks/useTimesheetRealtime";
import { TimesheetTab1 } from "@/pages/timesheet/TimesheetTab1";
import { TimesheetTab2 } from "@/pages/timesheet/TimesheetTab2";
import { TimesheetTab3 } from "@/pages/timesheet/TimesheetTab3";
import { TimesheetTabAdsolut } from "@/pages/timesheet/TimesheetTabAdsolut";
import { TimesheetTabBackoffice } from "@/pages/timesheet/TimesheetTabBackoffice";

type Tab = "day" | "manager" | "month" | "adsolut" | "resolved" | "cwi";

/// Top-level Timesheet page. Hosts up to four tabs:
///   1. **My day** (Tab 1) — the agent's own daily registration. Always
///      visible for users with `timesheet_enabled` or `timesheet_manager`.
///   2. **Manager** (Tab 2) — manager-only overview across all users.
///   3. **Month** (Tab 3) — manager-only month-per-agent rollup.
///   4. **Adsolut** — only shown when the Adsolut integration is connected
///      AND the user carries the "Adsolut Timesheet" feature flag. Lists the
///      mirrored sales receipts (verkoopbonnen) with expandable product +
///      performance lines and a per-row resync.
///
/// The tabs are role/flag-gated client-side AND the underlying APIs
/// enforce the same scope on the server, so a non-manager that hand-picks
/// the URL gets a 401/403, not silent access.
export function TimesheetPage() {
  const { user } = useAuth();
  const isManager = !!user?.timesheetManager;
  // The Adsolut tab needs both the live integration connection and the
  // per-user opt-in flag; either one alone keeps it hidden.
  const showAdsolut = !!user?.adsolutConnected && !!user?.adsolutTimesheetEnabled;
  // v0.0.56 — the two back-office tabs (Resolved / CWI) share one per-user
  // opt-in flag, independent of the manager role.
  const showBackoffice = !!user?.timesheetBackofficeEnabled;
  const [tab, setTab] = useState<Tab>("day");

  // v0.0.35 commit H — live-refresh Tab 2 / Tab 3 on any mutation from
  // another manager (or from an agent's Tab 1 save). Non-managers skip the
  // join entirely.
  useTimesheetManagerRealtime(isManager);

  return (
    <div className="flex min-h-0 w-full flex-1 flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">Timesheet</h1>
          <p className="text-sm text-muted-foreground">
            Register your time per day. Link each entry to a ticket or pick a
            non-ticket task for absence, meetings or administration.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          {isManager ? "Agent + manager" : "Own registration"}
        </Badge>
      </header>

      <div className="glass-panel flex items-center gap-1 p-1">
        <TabButton
          active={tab === "day"}
          onClick={() => setTab("day")}
          icon={<Clock className="h-3.5 w-3.5" />}
          label="My day"
        />
        <TabButton
          active={tab === "manager"}
          onClick={() => setTab("manager")}
          icon={<UsersIcon className="h-3.5 w-3.5" />}
          label="Manager"
          disabled={!isManager}
        />
        <TabButton
          active={tab === "month"}
          onClick={() => setTab("month")}
          icon={<CalendarRange className="h-3.5 w-3.5" />}
          label="Month"
          disabled={!isManager}
        />
        {showAdsolut && (
          <TabButton
            active={tab === "adsolut"}
            onClick={() => setTab("adsolut")}
            icon={<Receipt className="h-3.5 w-3.5" />}
            label="Adsolut"
          />
        )}
        {showBackoffice && (
          <>
            <TabButton
              active={tab === "resolved"}
              onClick={() => setTab("resolved")}
              icon={<ClipboardCheck className="h-3.5 w-3.5" />}
              label="Resolved"
            />
            <TabButton
              active={tab === "cwi"}
              onClick={() => setTab("cwi")}
              icon={<FileX2 className="h-3.5 w-3.5" />}
              label="CWI"
            />
          </>
        )}
      </div>

      {tab === "day" && <TimesheetTab1 />}
      {tab === "manager" && isManager && <TimesheetTab2 />}
      {tab === "month" && isManager && <TimesheetTab3 />}
      {tab === "adsolut" && showAdsolut && <TimesheetTabAdsolut />}
      {tab === "resolved" && showBackoffice && <TimesheetTabBackoffice context="resolved" />}
      {tab === "cwi" && showBackoffice && <TimesheetTabBackoffice context="cwi" />}
    </div>
  );
}

function TabButton({
  active,
  onClick,
  icon,
  label,
  disabled,
}: {
  active: boolean;
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={cn(
        "inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-sm transition-colors",
        active
          ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
          : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
        disabled && "cursor-not-allowed opacity-40 hover:bg-transparent hover:text-muted-foreground",
      )}
      title={disabled ? "Requires Timesheet manager access" : undefined}
    >
      {icon}
      {label}
    </button>
  );
}

