import { forwardRef } from "react";
import { cn } from "@/lib/utils";

type ToolbarButtonProps = {
  onClick: () => void;
  active?: boolean;
  title: string;
  children: React.ReactNode;
};

/// Toolbar button for the rich-text editors. Acts on mousedown (with the
/// default prevented) so the editor keeps its selection while clicking.
export function ToolbarButton({ onClick, active, title, children }: ToolbarButtonProps) {
  return (
    <button
      type="button"
      onMouseDown={(e) => {
        e.preventDefault();
        onClick();
      }}
      title={title}
      aria-label={title}
      aria-pressed={active ?? undefined}
      className={toolbarButtonClass(active)}
    >
      {children}
    </button>
  );
}

/// Same look as ToolbarButton, but a regular click target — used as a
/// Radix dropdown trigger (Radix opens on pointerdown, which a prevented
/// mousedown would swallow).
export const ToolbarMenuButton = forwardRef<
  HTMLButtonElement,
  React.ButtonHTMLAttributes<HTMLButtonElement> & { active?: boolean }
>(function ToolbarMenuButton({ active, className, children, ...rest }, ref) {
  return (
    <button
      ref={ref}
      type="button"
      aria-label={rest.title}
      className={cn(toolbarButtonClass(active), "flex items-center gap-0.5", className)}
      {...rest}
    >
      {children}
    </button>
  );
});

export function ToolbarSeparator() {
  return <div className="w-px h-4 bg-glass-strong mx-1 shrink-0" />;
}

function toolbarButtonClass(active: boolean | undefined) {
  return cn(
    "p-1.5 rounded-md transition-colors",
    active
      ? "bg-glass-strong text-foreground"
      : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
  );
}
