import type { ReactNode } from "react";
import { motion } from "framer-motion";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { BrandWordmark } from "@/components/BrandMark";
import { LoginBanner } from "@/components/auth/LoginBanner";
import { MaintenanceBanner } from "@/components/maintenance/MaintenanceBanner";
import { portalPublicApi, type PortalPublicConfig } from "@/lib/portal-api";
import { Toaster } from "sonner";
import { useTheme } from "@/app/ThemeProvider";
import { cn } from "@/lib/utils";

export const PORTAL_CONFIG_QK = ["portal", "public-config"] as const;

export function usePortalConfig() {
  return useQuery({
    queryKey: PORTAL_CONFIG_QK,
    queryFn: () => portalPublicApi.config(),
    staleTime: 60_000,
  });
}

export function portalOrganisation(config: PortalPublicConfig | undefined): string {
  return config && config.enabled ? config.organisationName : "Servicedesk";
}

type Props = {
  title: string;
  subtitle?: ReactNode;
  children: ReactNode;
  /// Rendered under the card (links like "Back to sign in").
  footer?: ReactNode;
  wide?: boolean;
  testId?: string;
};

/// The auth-page chrome shared by every anonymous portal page (sign in,
/// register, verify, reset, invitation): same aurora surface + glass card
/// as the agent login, with a "Customer portal" eyebrow so a customer who
/// lands here by link knows they are in the right place.
export function PortalAuthLayout({ title, subtitle, children, footer, wide, testId }: Props) {
  const { family } = useTheme();
  const config = usePortalConfig();
  const organisation = portalOrganisation(config.data);
  return (
    <div className="app-background auth-surface relative flex min-h-screen flex-col items-center justify-center px-4 py-10">
      <LoginBanner />
      <MaintenanceBanner variant="auth" />
      <motion.div
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.35, ease: "easeOut" }}
        className={cn("glass-card w-full overflow-hidden", wide ? "max-w-[520px]" : "max-w-[440px]")}
        data-testid={testId ?? "portal-auth-card"}
      >
        <div className="flex items-center justify-between border-b border-glass px-7 py-3">
          <BrandWordmark />
          <span className="rounded-full border border-glass bg-glass px-2.5 py-0.5 text-[10px] font-medium uppercase tracking-[0.18em] text-muted-foreground">
            Customer portal
          </span>
        </div>
        <div className="space-y-5 px-7 py-6">
          <div className="space-y-1">
            <h1 className="font-display text-display-sm tracking-tight">{title}</h1>
            {subtitle ? <p className="text-sm text-muted-foreground">{subtitle}</p> : null}
          </div>
          {config.data && !config.data.enabled ? (
            <div className="rounded-md border border-amber-500/30 bg-amber-500/[0.08] px-3 py-2.5 text-xs">
              The customer portal is not available at the moment.
            </div>
          ) : (
            children
          )}
        </div>
        {footer ? (
          <div className="border-t border-glass px-7 py-3 text-center text-xs text-muted-foreground">{footer}</div>
        ) : null}
      </motion.div>
      <p className="mt-4 text-[11px] text-muted-foreground/70">
        {organisation} · <Link to="/login" className="underline-offset-2 hover:underline">Staff sign-in</Link>
      </p>
      <Toaster theme={family === "steaan" ? "light" : "dark"} position="bottom-right" />
    </div>
  );
}

export function FieldLabel({ children }: { children: ReactNode }) {
  return <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">{children}</label>;
}

export function FieldError({ message }: { message?: string }) {
  if (!message) return null;
  return <p className="text-[11px] text-destructive/90">{message}</p>;
}

export function FormError({ message }: { message: string | null }) {
  if (!message) return null;
  return (
    <div
      role="alert"
      className="rounded-md border border-destructive/40 bg-destructive/[0.06] px-3 py-2 text-xs text-destructive/90"
    >
      {message}
    </div>
  );
}

export function FormNotice({ children, tone = "info" }: { children: ReactNode; tone?: "info" | "success" }) {
  return (
    <div
      className={cn(
        "rounded-md border px-3 py-2.5 text-xs",
        tone === "success"
          ? "border-emerald-500/30 bg-emerald-500/[0.08] text-foreground"
          : "border-glass bg-glass text-foreground",
      )}
    >
      {children}
    </div>
  );
}
