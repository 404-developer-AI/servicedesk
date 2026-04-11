import { Badge } from "@/components/ui/badge";
import type { SettingsSection } from "@/shell/settingsSections";

// Inner stub for a settings section that isn't implemented yet. This lives
// inside the SettingsLayout <Outlet />, so we don't render a mesh background
// or page-level container — the layout already provides the frame.
export function SettingsSectionStub({ section }: { section: SettingsSection }) {
  const Icon = section.icon;
  return (
    <div className="glass-card p-10">
      <div className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-4 text-primary">
            <Icon className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">
            {section.label}
          </h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            {section.description}
          </p>
        </div>
        {section.comingIn && (
          <Badge
            variant="secondary"
            className="shrink-0 border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground"
          >
            Coming in {section.comingIn}
          </Badge>
        )}
      </div>
    </div>
  );
}
