import { useAuth } from "@/auth/authStore";
import { TwoFactorSection } from "@/pages/profile/TwoFactorSection";

export function ProfilePage() {
  const { user } = useAuth();

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

      <TwoFactorSection />
    </div>
  );
}
