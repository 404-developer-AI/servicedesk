import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";

/// Shared dropdown for ticket taxonomies (status, priority, queue,
/// category). Renders a colored dot in front of every option AND in
/// front of the selected value in the trigger, so the choice reads at
/// a glance — both when creating a ticket (NewTicketDrawer) and when
/// editing one (TicketSidePanel).
export type TaxonomyOption = {
  id: string;
  name: string;
  color: string;
  /** Optional short tag rendered after the name, e.g. status state category. */
  badge?: string;
  /** Optional suffix for pinned-but-inactive rows (e.g. "— inactive"). */
  suffix?: string;
};

export type TaxonomySelectProps = {
  value: string;
  onChange: (value: string) => void;
  options: TaxonomyOption[];
  placeholder?: string;
  disabled?: boolean;
  allowEmpty?: boolean;
  emptyLabel?: string;
  /** Override the default trigger className. */
  triggerClassName?: string;
};

function ColorDot({ color }: { color: string }) {
  return (
    <span
      className="inline-block h-2 w-2 rounded-full shrink-0"
      style={{ backgroundColor: color || "#6b7280" }}
    />
  );
}

export function TaxonomySelect({
  value,
  onChange,
  options,
  placeholder,
  disabled,
  allowEmpty,
  emptyLabel = "None",
  triggerClassName,
}: TaxonomySelectProps) {
  const selected = options.find((opt) => opt.id === value);
  return (
    <Select
      value={value || undefined}
      onValueChange={(v) => onChange(v === "__empty__" ? "" : v)}
      disabled={disabled}
    >
      <SelectTrigger
        className={cn(
          "h-9 border-white/10 bg-white/[0.04] focus:border-white/20 focus:bg-white/[0.06] transition-colors",
          !value && "text-muted-foreground",
          triggerClassName,
        )}
      >
        {/* We render the trigger label ourselves instead of using
            SelectValue, because Radix's SelectValue re-renders the
            entire JSX of the selected SelectItem (dot + name + badge +
            suffix) — combining that with an explicit dot here doubled
            the bullet. The wrapper is a <div> so shadcn's built-in
            [&>span]:line-clamp-1 rule doesn't morph our flex into a
            -webkit-box and ellipsize the whole stack. */}
        <div className="flex min-w-0 flex-1 items-center gap-2 overflow-hidden">
          {selected ? (
            <>
              <ColorDot color={selected.color} />
              <span className="truncate">{selected.name}</span>
            </>
          ) : (
            <span className="truncate">{placeholder}</span>
          )}
        </div>
      </SelectTrigger>
      <SelectContent className="border-white/10 bg-background/95 backdrop-blur-xl">
        {allowEmpty && (
          <SelectItem value="__empty__">{emptyLabel}</SelectItem>
        )}
        {options.map((opt) => (
          <SelectItem key={opt.id} value={opt.id}>
            <div className="flex items-center gap-2">
              <ColorDot color={opt.color} />
              <span>{opt.name}</span>
              {opt.badge && (
                <span className="text-[10px] text-muted-foreground">
                  ({opt.badge})
                </span>
              )}
              {opt.suffix && (
                <span className="text-[10px] text-muted-foreground/70">
                  {opt.suffix}
                </span>
              )}
            </div>
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
