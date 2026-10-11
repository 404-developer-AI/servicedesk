import * as React from "react";
import { useNavigate } from "@tanstack/react-router";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { LogOut, Monitor } from "lucide-react";
import { useAuth, authStore } from "@/auth/authStore";
import { useTheme } from "@/app/ThemeProvider";
import { authApi } from "@/lib/api";
import { ThemePicker } from "@/components/ThemePicker";
import { MySessionsCard } from "@/components/auth/MySessionsCard";
import { useMobileHeader } from "../mobileHeader";
import { setMobilePreference } from "../mobileMode";
import { clearAllDrafts } from "../compose";

/// `/m/me` — account, appearance, signed-in devices, the switch to the
/// desktop version and sign-out. Admin and settings functions stay on
/// desktop by design (out of the mobile scope).
export function MobileMePage() {
  useMobileHeader("Me");
  const { user } = useAuth();
  const { theme, setTheme } = useTheme();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [signingOut, setSigningOut] = React.useState(false);

  const signOut = async () => {
    setSigningOut(true);
    try {
      await authApi.logout();
    } catch {
      // Never fake a sign-out: the server session would survive.
      toast.error("Sign-out failed — you are still signed in. Try again.");
      setSigningOut(false);
      return;
    }
    // A phone may be shared or handed over: drop unsent drafts and every
    // cached response. The server also asks the browser to clear its HTTP
    // cache (Clear-Site-Data) on this response.
    clearAllDrafts();
    queryClient.clear();
    authStore.patch({ user: null });
    toast.success("Signed out");
    void navigate({ to: "/login" });
  };

  return (
    <div className="space-y-4">
      <section className="glass-card px-4 py-4">
        <div className="truncate text-[15px] font-semibold">{user?.email}</div>
        <div className="mt-0.5 text-[13px] text-muted-foreground">{user?.role}</div>
      </section>

      <section className="glass-card space-y-3 px-4 py-4">
        <div className="flex items-baseline justify-between gap-3">
          <h2 className="text-base font-semibold">Appearance</h2>
          <span className="text-[11px] text-muted-foreground">Saved to your account</span>
        </div>
        <ThemePicker value={theme} onChange={setTheme} label="Theme" />
      </section>

      <MySessionsCard touch />

      <button
        type="button"
        onClick={() => {
          setMobilePreference("desktop");
          window.location.assign("/");
        }}
        className="glass-card flex min-h-14 w-full items-center gap-3 px-4 py-3 text-left active:bg-glass-hover"
      >
        <Monitor className="h-5 w-5 text-muted-foreground" />
        <span className="min-w-0 flex-1">
          <span className="block text-[15px] font-medium">Use desktop version</span>
          <span className="block text-[12px] text-muted-foreground">Settings, reports and admin tools live there. Switch back from your profile menu.</span>
        </span>
      </button>

      <button
        type="button"
        onClick={() => void signOut()}
        disabled={signingOut}
        className="flex h-12 w-full items-center justify-center gap-2 rounded-xl border border-destructive/40 text-[15px] font-semibold text-destructive disabled:opacity-60"
      >
        <LogOut className="h-4 w-4" />
        Sign out
      </button>
    </div>
  );
}
