import { Link } from "@tanstack/react-router";
import { ScrollText, Wrench } from "lucide-react";
import { Badge } from "@/components/ui/badge";

type Tile = {
  title: string;
  description: string;
  to: string | null;
  icon: React.ReactNode;
  comingIn?: string;
};

const TILES: readonly Tile[] = [
  {
    title: "Audit log",
    description:
      "Append-only HMAC-chained record of security events — rate limits, CSP violations, setting changes.",
    to: "/settings/audit",
    icon: <ScrollText className="h-5 w-5" />,
  },
  {
    title: "General, Tickets, Mail, SLA, Integrations",
    description:
      "Tunable values for every part of the app. Grouped, searchable, inline descriptions, audit-logged changes.",
    to: null,
    icon: <Wrench className="h-5 w-5" />,
    comingIn: "v0.0.7",
  },
];

export function SettingsIndexPage() {
  return (
    <div className="app-background min-h-[calc(100vh-8rem)] p-8">
      <div className="mx-auto w-full max-w-5xl space-y-6">
        <header className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">Settings</h1>
          <p className="text-sm text-muted-foreground">
            Admin-only app configuration. More sections land as features ship.
          </p>
        </header>

        <div className="grid gap-4 md:grid-cols-2">
          {TILES.map((tile) =>
            tile.to ? (
              <Link
                key={tile.title}
                to={tile.to}
                className="glass-card glass-hover group block p-6"
              >
                <TileBody tile={tile} />
              </Link>
            ) : (
              <div key={tile.title} className="glass-card block p-6 opacity-70">
                <TileBody tile={tile} />
              </div>
            ),
          )}
        </div>
      </div>
    </div>
  );
}

function TileBody({ tile }: { tile: Tile }) {
  return (
    <>
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-3 text-primary">
          {tile.icon}
          <span className="font-medium text-foreground">{tile.title}</span>
        </div>
        {tile.comingIn && (
          <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
            Coming in {tile.comingIn}
          </Badge>
        )}
      </div>
      <p className="mt-3 text-sm text-muted-foreground">{tile.description}</p>
    </>
  );
}
