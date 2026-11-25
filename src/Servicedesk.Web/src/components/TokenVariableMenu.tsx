import { ChevronDown, Tag } from "lucide-react";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";

export type TokenVariable = { token: string; label: string };

/// The shared "Insert variable" dropdown (compose / ticket templates, survey
/// invite, campaigns). `onPick` receives the literal `{{token}}` placeholder
/// so each caller drops it into its own field.
export function TokenVariableMenu({
  tokens,
  onPick,
  title = "Insert a placeholder that resolves to the ticket's data",
  disabled = false,
}: {
  tokens: TokenVariable[];
  onPick: (token: string) => void;
  title?: string;
  disabled?: boolean;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild disabled={disabled}>
        <button
          type="button"
          className={cn(
            "sd-token-menu inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
            "border-violet-400/30 bg-violet-400/10 text-violet-200 hover:bg-violet-400/15 disabled:opacity-50",
          )}
          title={title}
        >
          <Tag className="h-3.5 w-3.5" />
          Insert variable
          <ChevronDown className="h-3.5 w-3.5" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="max-h-80 w-72 overflow-auto">
        <DropdownMenuLabel className="text-[11px] uppercase tracking-wider text-muted-foreground/70">
          Available variables
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {tokens.length === 0 ? (
          <div className="px-2 py-2 text-xs text-muted-foreground">Loading…</div>
        ) : (
          tokens.map((t) => (
            <DropdownMenuItem
              key={t.token}
              onSelect={(e) => {
                e.preventDefault();
                onPick(t.token);
              }}
              className="flex items-center justify-between gap-3"
            >
              <span className="truncate text-sm">{t.label}</span>
              <code className="shrink-0 rounded bg-glass-strong px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                {t.token}
              </code>
            </DropdownMenuItem>
          ))
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
