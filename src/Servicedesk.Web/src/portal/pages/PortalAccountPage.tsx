import { useState } from "react";
import { Building2, KeyRound, Mail, ShieldCheck, User } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { portalAuthApi } from "@/lib/portal-api";
import { usePortalMe } from "@/portal/portalShared";

export function PortalAccountPage() {
  const me = usePortalMe();
  const [sent, setSent] = useState(false);
  const user = me.user;

  async function sendReset() {
    if (!user) return;
    try {
      await portalAuthApi.forgotPassword(user.email);
      setSent(true);
      toast.success("Reset link sent — check your inbox.");
    } catch {
      toast.error("Could not send the reset link right now.");
    }
  }

  return (
    <div className="mx-auto max-w-2xl space-y-5" data-testid="portal-account">
      <div>
        <h1 className="font-display text-display-sm tracking-tight">Account</h1>
        <p className="mt-1 text-sm text-muted-foreground">Your portal profile and security settings.</p>
      </div>

      <section className="glass-card divide-y divide-glass">
        <Row icon={User} label="Name" value={user?.displayName || "—"} />
        <Row icon={Mail} label="Email" value={user?.email || "—"} />
      </section>

      <section className="glass-card p-5">
        <h2 className="mb-2 flex items-center gap-2 text-sm font-medium">
          <Building2 className="h-4 w-4 text-muted-foreground" /> Companies
        </h2>
        {user && user.companies.length > 0 ? (
          <ul className="divide-y divide-glass text-sm">
            {user.companies.map((c) => (
              <li key={c.id} className="flex items-center justify-between gap-3 py-2">
                <span className="min-w-0 truncate">
                  {c.name}
                  {c.isPrimary ? <span className="ml-2 text-[10px] uppercase tracking-[0.12em] text-muted-foreground">primary</span> : null}
                </span>
                <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
                  <ShieldCheck className="h-3.5 w-3.5" />
                  {c.canSeeCompanyTickets ? "Ticket manager — every ticket" : "Member — your tickets"}
                </span>
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-xs text-muted-foreground">Not linked to a company — you see only the tickets you opened yourself.</p>
        )}
      </section>

      <section className="glass-card space-y-4 p-5">
        <div>
          <h2 className="text-sm font-medium">Password</h2>
          <p className="mt-1 text-xs text-muted-foreground">
            To change your password we send a reset link to your email address. All other sessions are signed out when you set a new one.
          </p>
        </div>
        <Button variant="secondary" size="sm" className="gap-2" onClick={sendReset} disabled={sent}>
          <KeyRound className="h-3.5 w-3.5" />
          {sent ? "Reset link sent" : "Send me a reset link"}
        </Button>
      </section>

      <section className="glass-card space-y-2 p-5">
        <h2 className="text-sm font-medium">Two-factor authentication</h2>
        <p className="text-xs text-muted-foreground">
          {user?.twoFactorEnrolled
            ? "An authenticator app is linked to your account. Lost your device? Contact the service desk — they can reset it after verifying your identity."
            : "Not set up yet — you will be asked at your next sign-in."}
        </p>
      </section>
    </div>
  );
}

function Row({ icon: Icon, label, value }: { icon: typeof User; label: string; value: string }) {
  return (
    <div className="flex items-center gap-3 px-5 py-3 text-sm">
      <Icon className="h-4 w-4 shrink-0 text-muted-foreground" />
      <span className="w-24 shrink-0 text-xs uppercase tracking-[0.12em] text-muted-foreground">{label}</span>
      <span className="min-w-0 truncate">{value}</span>
    </div>
  );
}
