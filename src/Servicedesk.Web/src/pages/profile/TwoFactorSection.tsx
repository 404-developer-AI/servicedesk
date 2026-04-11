import { useEffect, useState } from "react";
import QRCode from "qrcode";
import { ShieldCheck, Copy, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authApi, ApiError } from "@/lib/api";
import { refreshAuth } from "@/auth/bootstrap";
import { useAuth } from "@/auth/authStore";

type Stage = "idle" | "enrolling" | "done";

export function TwoFactorSection() {
  const { user } = useAuth();
  const [stage, setStage] = useState<Stage>("idle");
  const [otpauthUri, setOtpauthUri] = useState<string | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [qrDataUrl, setQrDataUrl] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!otpauthUri) {
      setQrDataUrl(null);
      return;
    }
    let cancelled = false;
    QRCode.toDataURL(otpauthUri, { margin: 1, width: 196 })
      .then((url) => {
        if (!cancelled) setQrDataUrl(url);
      })
      .catch(() => {
        if (!cancelled) setQrDataUrl(null);
      });
    return () => {
      cancelled = true;
    };
  }, [otpauthUri]);

  const beginEnroll = async () => {
    setError(null);
    setBusy(true);
    try {
      const enrollment = await authApi.beginTotpEnroll();
      setOtpauthUri(enrollment.otpauthUri);
      setSecret(enrollment.secret);
      setStage("enrolling");
    } catch (e) {
      setError(e instanceof ApiError ? `Failed (${e.status}).` : "Failed to start enrollment.");
    } finally {
      setBusy(false);
    }
  };

  const confirmEnroll = async () => {
    if (code.trim().length < 6) {
      setError("Enter the 6-digit code from your authenticator.");
      return;
    }
    setError(null);
    setBusy(true);
    try {
      const result = await authApi.confirmTotpEnroll(code.trim());
      setRecoveryCodes(result.recoveryCodes);
      setStage("done");
      setCode("");
      await refreshAuth();
      toast.success("Two-factor authentication enabled");
    } catch {
      setError("Code did not match. Try again.");
    } finally {
      setBusy(false);
    }
  };

  const disable = async () => {
    if (!window.confirm("Disable two-factor authentication?")) return;
    setBusy(true);
    try {
      await authApi.disableTotp();
      await refreshAuth();
      setOtpauthUri(null);
      setSecret(null);
      setRecoveryCodes(null);
      setStage("idle");
      toast.success("Two-factor disabled");
    } catch {
      toast.error("Could not disable two-factor");
    } finally {
      setBusy(false);
    }
  };

  const copyRecoveryCodes = async () => {
    if (!recoveryCodes) return;
    try {
      await navigator.clipboard.writeText(recoveryCodes.join("\n"));
      toast.success("Recovery codes copied");
    } catch {
      toast.error("Clipboard unavailable");
    }
  };

  const enabled = user?.twoFactorEnabled ?? false;

  return (
    <section className="glass-card space-y-5 p-6" data-testid="two-factor-section">
      <div className="flex items-start justify-between gap-4">
        <div>
          <div className="flex items-center gap-2 text-sm font-medium">
            <ShieldCheck className="h-4 w-4 text-primary" />
            Two-factor authentication
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            Protect your account with a time-based one-time password. Optional in
            this release — will become mandatory for admins and agents later.
          </p>
        </div>
        <span
          className={
            "rounded-full border px-2.5 py-0.5 text-[10px] uppercase tracking-[0.14em] " +
            (enabled
              ? "border-emerald-400/30 bg-emerald-400/[0.08] text-emerald-200/90"
              : "border-white/10 bg-white/[0.03] text-muted-foreground")
          }
        >
          {enabled ? "Enabled" : "Disabled"}
        </span>
      </div>

      {stage === "idle" && !enabled && (
        <Button onClick={beginEnroll} disabled={busy}>
          {busy ? "Starting…" : "Enable two-factor"}
        </Button>
      )}

      {stage === "idle" && enabled && (
        <Button variant="destructive" onClick={disable} disabled={busy}>
          Disable two-factor
        </Button>
      )}

      {stage === "enrolling" && (
        <div className="space-y-4">
          <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
            <div className="rounded-lg border border-white/10 bg-white p-2">
              {qrDataUrl ? (
                <img src={qrDataUrl} alt="TOTP QR code" width={196} height={196} />
              ) : (
                <div className="h-[196px] w-[196px] animate-pulse rounded bg-white/10" />
              )}
            </div>
            <div className="min-w-0 flex-1 space-y-3 text-xs text-muted-foreground">
              <p>
                Scan this code with Google Authenticator, 1Password, Bitwarden, or
                any other RFC 6238 app. You can also enter the secret manually:
              </p>
              <code className="block break-all rounded border border-white/10 bg-white/[0.03] px-2 py-1.5 font-mono text-[11px] text-foreground">
                {secret}
              </code>
            </div>
          </div>
          <div className="space-y-1.5">
            <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
              Verification code
            </label>
            <Input
              value={code}
              onChange={(e) => setCode(e.target.value)}
              placeholder="123 456"
              inputMode="numeric"
              autoComplete="one-time-code"
              className="font-mono tracking-[0.2em]"
            />
          </div>
          {error && <p className="text-[11px] text-destructive/90">{error}</p>}
          <div className="flex gap-2">
            <Button onClick={confirmEnroll} disabled={busy}>
              {busy ? "Verifying…" : "Confirm"}
            </Button>
            <Button
              variant="ghost"
              onClick={() => {
                setStage("idle");
                setOtpauthUri(null);
                setSecret(null);
                setCode("");
                setError(null);
              }}
            >
              Cancel
            </Button>
          </div>
        </div>
      )}

      {stage === "done" && recoveryCodes && (
        <div className="space-y-3">
          <div className="rounded-md border border-amber-400/30 bg-amber-400/[0.06] px-4 py-3 text-xs text-amber-100/90">
            Save these recovery codes somewhere safe. Each one works exactly once
            and can be used instead of an authenticator code if you lose your
            device. They are shown only now.
          </div>
          <div className="grid grid-cols-2 gap-2 font-mono text-xs">
            {recoveryCodes.map((c) => (
              <code
                key={c}
                className="rounded border border-white/10 bg-white/[0.03] px-2 py-1.5"
              >
                {c}
              </code>
            ))}
          </div>
          <div className="flex gap-2">
            <Button onClick={copyRecoveryCodes}>
              <Copy className="mr-2 h-4 w-4" /> Copy all
            </Button>
            <Button variant="ghost" onClick={() => setStage("idle")}>
              <RefreshCw className="mr-2 h-4 w-4" /> Done
            </Button>
          </div>
        </div>
      )}

      {error && stage !== "enrolling" && (
        <p className="text-[11px] text-destructive/90">{error}</p>
      )}
    </section>
  );
}
