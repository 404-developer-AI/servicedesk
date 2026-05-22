import * as React from "react";
import {
  Search,
  Clock,
  Users as UsersIcon,
  ClipboardCheck,
  ShieldAlert,
  BookOpen,
  Settings2,
  ChevronDown,
  Lock,
  Activity,
} from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import type { UserAdminRow, FeatureFlagsUpdate } from "@/lib/ticket-api";
import { DASHBOARD_TILES, roleSatisfies } from "@/lib/dashboardTiles";

type FlagKey = keyof FeatureFlagsUpdate;

type FlagSpec = {
  key: FlagKey;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
};

type Group = { title: string; flags: FlagSpec[] };

const FLAG_GROUPS: Group[] = [
  {
    title: "Sidebar",
    flags: [{ key: "searchEnabled", label: "Search", icon: Search }],
  },
  {
    title: "Timesheet",
    flags: [
      { key: "timesheetEnabled", label: "Timesheet", icon: Clock },
      { key: "timesheetManager", label: "TS manager", icon: UsersIcon },
    ],
  },
  {
    title: "ISO 27001",
    flags: [
      { key: "isIsoMgm", label: "ISO MGM", icon: ClipboardCheck },
      { key: "isIsoDpo", label: "ISO DPO", icon: ShieldAlert },
    ],
  },
  {
    title: "Knowledge base",
    flags: [{ key: "kbEnabled", label: "KB", icon: BookOpen }],
  },
  {
    title: "Activity feed",
    flags: [{ key: "activityFeedEnabled", label: "Activity feed", icon: Activity }],
  },
];

const ALL_FLAG_KEYS: FlagKey[] = FLAG_GROUPS.flatMap((g) =>
  g.flags.map((f) => f.key),
);

export function FeatureFlagsDropdown({
  user,
  disabled,
  onToggleFlag,
  onToggleTile,
}: {
  user: UserAdminRow;
  disabled: boolean;
  onToggleFlag: (
    update: FeatureFlagsUpdate,
    label: string,
    enabling: boolean,
  ) => void;
  onToggleTile: (
    nextTileIds: string[],
    label: string,
    enabling: boolean,
  ) => void;
}) {
  const enabledFlagCount = ALL_FLAG_KEYS.reduce(
    (n, key) => n + (user[key as keyof UserAdminRow] ? 1 : 0),
    0,
  );
  const totalCount = enabledFlagCount + (user.dashboardTiles?.length ?? 0);

  return (
    <Popover>
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={disabled}
          title="Per-user feature flags"
          className={cn(
            "shrink-0 inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11px] font-medium transition-colors",
            "border-white/10 bg-white/[0.04] text-foreground hover:bg-white/[0.07]",
            "disabled:opacity-50 disabled:cursor-not-allowed",
          )}
        >
          <Settings2 className="h-3 w-3" />
          Features ({totalCount})
          <ChevronDown className="h-3 w-3 opacity-70" />
        </button>
      </PopoverTrigger>

      <PopoverContent className="w-72 p-2" align="end" side="bottom">
        <div className="space-y-2">
          {FLAG_GROUPS.map((group) => (
            <div key={group.title} className="space-y-0.5">
              <div className="px-2 pt-1 text-[10px] font-medium uppercase tracking-widest text-muted-foreground/70">
                {group.title}
              </div>
              {group.flags.map((flag) => {
                const checked = Boolean(
                  user[flag.key as keyof UserAdminRow],
                );
                const Icon = flag.icon;
                return (
                  <label
                    key={flag.key}
                    className={cn(
                      "flex items-center gap-2.5 rounded px-2 py-1.5 text-sm transition-colors",
                      disabled
                        ? "cursor-not-allowed opacity-50"
                        : "cursor-pointer hover:bg-white/[0.07]",
                    )}
                  >
                    <input
                      type="checkbox"
                      checked={checked}
                      disabled={disabled}
                      onChange={() => {
                        const next = !checked;
                        onToggleFlag(
                          { [flag.key]: next } as FeatureFlagsUpdate,
                          flag.label,
                          next,
                        );
                      }}
                      className="rounded border-white/20 bg-white/[0.04] accent-primary"
                    />
                    <Icon className="h-3.5 w-3.5 text-muted-foreground" />
                    <span
                      className={
                        checked ? "text-foreground" : "text-muted-foreground"
                      }
                    >
                      {flag.label}
                    </span>
                  </label>
                );
              })}
            </div>
          ))}

          <div className="space-y-0.5">
            <div className="px-2 pt-1 text-[10px] font-medium uppercase tracking-widest text-muted-foreground/70">
              Dashboard tiles
            </div>
            {DASHBOARD_TILES.map((tile) => {
              const roleOk = roleSatisfies(user.role, tile.minRole);
              const checked = (user.dashboardTiles ?? []).includes(tile.id);
              const isDisabled = disabled || !roleOk;
              const Icon = tile.icon;
              return (
                <label
                  key={tile.id}
                  title={
                    !roleOk
                      ? `${tile.minRole}-only tile — cannot be enabled for this user`
                      : undefined
                  }
                  className={cn(
                    "flex items-center gap-2.5 rounded px-2 py-1.5 text-sm transition-colors",
                    isDisabled
                      ? "cursor-not-allowed opacity-50"
                      : "cursor-pointer hover:bg-white/[0.07]",
                  )}
                >
                  <input
                    type="checkbox"
                    checked={checked && roleOk}
                    disabled={isDisabled}
                    onChange={() => {
                      const current = user.dashboardTiles ?? [];
                      const next = checked
                        ? current.filter((id) => id !== tile.id)
                        : [...current, tile.id];
                      onToggleTile(next, tile.label, !checked);
                    }}
                    className="rounded border-white/20 bg-white/[0.04] accent-primary"
                  />
                  <Icon className="h-3.5 w-3.5 text-muted-foreground" />
                  <span
                    className={
                      checked && roleOk
                        ? "text-foreground"
                        : "text-muted-foreground"
                    }
                  >
                    {tile.label}
                  </span>
                  {!roleOk && (
                    <Lock className="ml-auto h-3 w-3 text-muted-foreground/70" />
                  )}
                </label>
              );
            })}
          </div>
        </div>

        <div className="mt-2 border-t border-white/10 pt-2 px-2 pb-1 text-[10px] text-muted-foreground/70">
          These features only apply to Agent and Admin accounts.
        </div>
      </PopoverContent>
    </Popover>
  );
}
