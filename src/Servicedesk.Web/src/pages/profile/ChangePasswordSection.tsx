import { useState } from "react";
import { KeyRound } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authApi, ApiError, apiErrorMessage } from "@/lib/api";
import { useAuth } from "@/auth/authStore";

/// v0.1.3 (audit v0.1.1 #8) — self-service password change for Local staff
/// accounts. Hidden for M365 sessions (amr "ext"): their password lives at
/// Microsoft. A successful change signs out every other session.
export function ChangePasswordSection() {
  const { user } = useAuth();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (!user || user.amr === "ext") return null;

  const submit = async () => {
    setError(null);
    if (!current) {
      setError("Enter your current password.");
      return;
    }
    if (next.length === 0) {
      setError("Enter a new password.");
      return;
    }
    if (next !== confirm) {
      setError("The new passwords do not match.");
      return;
    }
    setBusy(true);
    try {
      await authApi.changePassword(current, next);
      setCurrent("");
      setNext("");
      setConfirm("");
      toast.success("Password changed — other sessions were signed out");
    } catch (e) {
      if (e instanceof ApiError && e.status === 423) {
        setError("Too many wrong attempts — this account is temporarily locked.");
      } else if (e instanceof ApiError && e.status === 400) {
        setError(apiErrorMessage(e) ?? "The password change was rejected.");
      } else {
        setError("Could not change the password. Try again.");
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="glass-card space-y-4 p-6" data-testid="change-password-section">
      <div>
        <div className="flex items-center gap-2 text-sm font-medium">
          <KeyRound className="h-4 w-4 text-primary" />
          Password
        </div>
        <p className="mt-1 text-xs text-muted-foreground">
          Changing your password signs out every other session of your account.
        </p>
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        <div className="space-y-1.5">
          <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
            Current password
          </label>
          <Input
            type="password"
            autoComplete="current-password"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
          />
        </div>
        <div className="space-y-1.5">
          <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
            New password
          </label>
          <Input
            type="password"
            autoComplete="new-password"
            value={next}
            onChange={(e) => setNext(e.target.value)}
          />
        </div>
        <div className="space-y-1.5">
          <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
            Repeat new password
          </label>
          <Input
            type="password"
            autoComplete="new-password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
          />
        </div>
      </div>

      {error && <p className="text-[11px] text-destructive/90">{error}</p>}

      <Button onClick={submit} disabled={busy}>
        {busy ? "Changing…" : "Change password"}
      </Button>
    </section>
  );
}
