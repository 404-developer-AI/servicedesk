import { Link } from "@tanstack/react-router";
import { AtSign, ChevronRight, Moon, Sun } from "lucide-react";
import { useAuth } from "@/auth/authStore";
import { TwoFactorSection } from "@/pages/profile/TwoFactorSection";
import { useTheme } from "@/app/ThemeProvider";
import { cn } from "@/lib/utils";

export function ProfilePage() {
  const { user } = useAuth();
  const { theme, setTheme } = useTheme();

  return (
    <div className="mx-auto max-w-3xl space-y-6 py-4">
      <header className="space-y-1">
        <div className="text-[11px] uppercase tracking-[0.22em] text-muted-foreground">
          Profile
        </div>
        <h1 className="font-display text-display-sm font-semibold">
          {user?.email ?? "Profile"}
        </h1>
        <p className="text-xs text-muted-foreground">
          Your personal account settings. App-wide configuration lives under
          Settings.
        </p>
      </header>

      <section className="glass-card space-y-3 p-6">
        <div className="text-sm font-medium">Account</div>
        <dl className="grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-2">
          <dt className="text-muted-foreground">Email</dt>
          <dd className="truncate">{user?.email}</dd>
          <dt className="text-muted-foreground">Role</dt>
          <dd>{user?.role}</dd>
          <dt className="text-muted-foreground">Session class</dt>
          <dd className="font-mono text-[11px] uppercase tracking-[0.1em]">{user?.amr}</dd>
        </dl>
      </section>

      <section className="glass-card space-y-3 p-6">
        <div className="flex items-baseline justify-between gap-3">
          <div className="text-sm font-medium">Appearance</div>
          <div className="text-[11px] text-muted-foreground">
            Saved to your account — applies on every device.
          </div>
        </div>
        <div
          role="radiogroup"
          aria-label="Theme"
          className="inline-flex rounded-lg border border-glass bg-glass p-1"
        >
          <button
            type="button"
            role="radio"
            aria-checked={theme === "light"}
            onClick={() => setTheme("light")}
            className={cn(
              "flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium transition-colors",
              theme === "light"
                ? "bg-primary text-primary-foreground shadow-sm"
                : "text-muted-foreground hover:text-foreground",
            )}
          >
            <Sun className="h-3.5 w-3.5" />
            Light
          </button>
          <button
            type="button"
            role="radio"
            aria-checked={theme === "dark"}
            onClick={() => setTheme("dark")}
            className={cn(
              "flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium transition-colors",
              theme === "dark"
                ? "bg-primary text-primary-foreground shadow-sm"
                : "text-muted-foreground hover:text-foreground",
            )}
          >
            <Moon className="h-3.5 w-3.5" />
            Dark
          </button>
        </div>
      </section>

      {(user?.role === "Agent" || user?.role === "Admin") ? (
        <Link
          to="/profile/mentions"
          className="glass-card flex items-center gap-3 p-4 transition-colors hover:bg-glass-hover"
        >
          <div className="flex h-9 w-9 items-center justify-center rounded-lg border border-purple-500/30 bg-purple-500/15">
            <AtSign className="h-4 w-4 text-purple-200" />
          </div>
          <div className="min-w-0 flex-1">
            <div className="text-sm font-medium">Mijn tags</div>
            <div className="text-xs text-muted-foreground">
              Alle @@-tags die jij ontvangen hebt, met ack / viewed status.
            </div>
          </div>
          <ChevronRight className="h-4 w-4 text-muted-foreground" />
        </Link>
      ) : null}

      <TwoFactorSection />
    </div>
  );
}
