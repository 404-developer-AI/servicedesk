import { useEffect, useState } from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import QRCode from "qrcode";
import { LockKeyhole, Mail, ShieldCheck, Copy, Check } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ApiError } from "@/lib/api";
import { portalAuthApi } from "@/lib/portal-api";
import { authStore } from "@/auth/authStore";
import { refreshAuth } from "@/auth/bootstrap";
import { FieldError, FieldLabel, FormError, FormNotice, PortalAuthLayout, usePortalConfig } from "@/portal/PortalAuthLayout";

const loginSchema = z.object({
  email: z.string().min(1, "Email is required").email("Enter a valid email"),
  password: z.string().min(1, "Password is required"),
});
const codeSchema = z.object({
  code: z.string().min(6, "Enter the 6-digit code or a recovery code"),
});
type LoginValues = z.infer<typeof loginSchema>;
type CodeValues = z.infer<typeof codeSchema>;

type Stage = "credentials" | "two-factor" | "enroll" | "recovery-codes";

export function PortalLoginPage() {
  const navigate = useNavigate();
  const config = usePortalConfig();
  const [stage, setStage] = useState<Stage>("credentials");
  const [serverError, setServerError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [enrollment, setEnrollment] = useState<{ secret: string; otpauthUri: string; qr: string | null } | null>(null);
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);

  // Resume: a pending customer session (password accepted, TOTP owed or
  // not yet enrolled) lands back here after a reload; a complete session
  // goes straight to the portal; agents never belong here.
  useEffect(() => {
    const user = authStore.get().user;
    if (!user) return;
    if (user.role !== "Customer") {
      navigate({ to: "/" });
      return;
    }
    if (user.amr === "mfa-pending") {
      if (user.twoFactorEnabled) setStage("two-factor");
      else void startEnrollment();
      return;
    }
    navigate({ to: "/portal" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const flag = params.get("notice");
    if (flag === "reset") setNotice("Your password was changed. Sign in with your new password.");
    if (flag === "activated") setNotice("Your account is ready. Sign in to continue.");
    if (flag === "verified") setNotice("Your email address is confirmed. You can sign in as soon as your registration is approved.");
  }, []);

  const loginForm = useForm<LoginValues>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: "", password: "" },
  });
  const codeForm = useForm<CodeValues>({ resolver: zodResolver(codeSchema), defaultValues: { code: "" } });
  const enrollForm = useForm<CodeValues>({ resolver: zodResolver(codeSchema), defaultValues: { code: "" } });

  async function startEnrollment() {
    setServerError(null);
    try {
      const res = await portalAuthApi.beginEnroll();
      let qr: string | null = null;
      try {
        qr = await QRCode.toDataURL(res.otpauthUri, { margin: 1, width: 196 });
      } catch {
        qr = null;
      }
      setEnrollment({ secret: res.secret, otpauthUri: res.otpauthUri, qr });
      setStage("enroll");
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        // Already enrolled after all → challenge instead.
        setStage("two-factor");
        return;
      }
      setServerError(describeAuthError(e, "Could not start the authenticator setup."));
      setStage("credentials");
    }
  }

  const onLogin = loginForm.handleSubmit(async (values) => {
    setServerError(null);
    setNotice(null);
    try {
      const res = await portalAuthApi.login(values.email, values.password);
      await refreshAuth();
      if (res.twoFactorRequired) {
        setStage("two-factor");
        return;
      }
      if (res.enrollmentRequired) {
        await startEnrollment();
        return;
      }
      navigate({ to: "/portal" });
    } catch (e) {
      setServerError(describeAuthError(e, "Sign-in failed."));
    }
  });

  const onVerify = codeForm.handleSubmit(async (values) => {
    setServerError(null);
    try {
      await portalAuthApi.verifyTwoFactor(values.code.trim());
      await refreshAuth();
      toast.success("Welcome back");
      navigate({ to: "/portal" });
    } catch (e) {
      setServerError(describeAuthError(e, "Verification failed."));
    }
  });

  const onConfirmEnroll = enrollForm.handleSubmit(async (values) => {
    setServerError(null);
    try {
      const res = await portalAuthApi.confirmEnroll(values.code.trim());
      await refreshAuth();
      setRecoveryCodes(res.recoveryCodes);
      setStage("recovery-codes");
    } catch (e) {
      if (e instanceof ApiError && e.status === 400) {
        setServerError("The code is not valid. Check your authenticator app and try again.");
        return;
      }
      setServerError(describeAuthError(e, "Could not confirm the authenticator."));
    }
  });

  const registrationEnabled = config.data?.enabled ? config.data.registrationEnabled : false;

  return (
    <PortalAuthLayout
      title={
        stage === "credentials"
          ? "Sign in"
          : stage === "two-factor"
            ? "Two-factor verification"
            : stage === "enroll"
              ? "Set up two-factor authentication"
              : "Save your recovery codes"
      }
      subtitle={
        stage === "credentials"
          ? "Follow your tickets and open new requests."
          : stage === "two-factor"
            ? "Enter the 6-digit code from your authenticator app, or a single-use recovery code."
            : stage === "enroll"
              ? "Two-factor authentication is required for every portal account. Scan the code with an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password, …) and enter the 6-digit code it shows."
              : "Each code works once when you cannot use your authenticator app. Store them somewhere safe — they are shown only now."
      }
      footer={
        stage === "credentials" ? (
          <div className="flex items-center justify-between">
            <Link to="/portal/forgot-password" className="hover:text-foreground">
              Forgot your password?
            </Link>
            {registrationEnabled ? (
              <Link to="/portal/register" className="font-medium text-primary hover:underline">
                Create an account
              </Link>
            ) : null}
          </div>
        ) : undefined
      }
    >
      {stage === "credentials" && (
        <form onSubmit={onLogin} className="space-y-4" noValidate data-testid="portal-login-form">
          {notice ? <FormNotice tone="success">{notice}</FormNotice> : null}
          <div className="space-y-1.5">
            <FieldLabel>Email</FieldLabel>
            <div className="relative">
              <Mail className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/70" />
              <Input type="email" autoComplete="username" placeholder="you@company.com" className="pl-9" {...loginForm.register("email")} />
            </div>
            <FieldError message={loginForm.formState.errors.email?.message} />
          </div>
          <div className="space-y-1.5">
            <FieldLabel>Password</FieldLabel>
            <div className="relative">
              <LockKeyhole className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/70" />
              <Input type="password" autoComplete="current-password" placeholder="••••••••••••" className="pl-9" {...loginForm.register("password")} />
            </div>
            <FieldError message={loginForm.formState.errors.password?.message} />
          </div>
          <FormError message={serverError} />
          <Button type="submit" className="w-full" disabled={loginForm.formState.isSubmitting}>
            {loginForm.formState.isSubmitting ? "Signing in…" : "Sign in"}
          </Button>
        </form>
      )}

      {stage === "two-factor" && (
        <form onSubmit={onVerify} className="space-y-4" noValidate>
          <div className="space-y-1.5">
            <FieldLabel>Code</FieldLabel>
            <Input
              autoFocus
              inputMode="text"
              autoComplete="one-time-code"
              placeholder="123 456"
              className="font-mono tracking-[0.2em]"
              {...codeForm.register("code")}
            />
            <FieldError message={codeForm.formState.errors.code?.message} />
          </div>
          <FormError message={serverError} />
          <Button type="submit" className="w-full" disabled={codeForm.formState.isSubmitting}>
            {codeForm.formState.isSubmitting ? "Verifying…" : "Verify"}
          </Button>
          <button type="button" className="block w-full text-center text-xs text-muted-foreground hover:text-foreground" onClick={() => signOutToStart(navigate)}>
            Use a different account
          </button>
        </form>
      )}

      {stage === "enroll" && enrollment && (
        <form onSubmit={onConfirmEnroll} className="space-y-4" noValidate>
          <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
            <div className="rounded-lg border border-glass bg-white p-2">
              {enrollment.qr ? (
                <img src={enrollment.qr} alt="Authenticator QR code" width={172} height={172} className="block" />
              ) : (
                <div className="flex h-[172px] w-[172px] items-center justify-center text-center text-xs text-neutral-500">
                  QR unavailable — use the key below.
                </div>
              )}
            </div>
            <div className="min-w-0 flex-1 space-y-2 text-xs text-muted-foreground">
              <p>Cannot scan? Enter this key manually:</p>
              <SecretBox value={enrollment.secret} />
            </div>
          </div>
          <div className="space-y-1.5">
            <FieldLabel>Code from the app</FieldLabel>
            <Input
              autoFocus
              inputMode="numeric"
              autoComplete="one-time-code"
              placeholder="123 456"
              className="font-mono tracking-[0.2em]"
              {...enrollForm.register("code")}
            />
            <FieldError message={enrollForm.formState.errors.code?.message} />
          </div>
          <FormError message={serverError} />
          <Button type="submit" className="w-full gap-2" disabled={enrollForm.formState.isSubmitting}>
            <ShieldCheck className="h-4 w-4" />
            {enrollForm.formState.isSubmitting ? "Confirming…" : "Activate two-factor"}
          </Button>
          <button type="button" className="block w-full text-center text-xs text-muted-foreground hover:text-foreground" onClick={() => signOutToStart(navigate)}>
            Cancel and sign out
          </button>
        </form>
      )}

      {stage === "recovery-codes" && recoveryCodes && (
        <div className="space-y-4">
          <RecoveryCodes codes={recoveryCodes} />
          <Button type="button" className="w-full" onClick={() => navigate({ to: "/portal" })}>
            I have saved my codes — continue
          </Button>
        </div>
      )}
    </PortalAuthLayout>
  );
}

async function signOutToStart(navigate: ReturnType<typeof useNavigate>) {
  try {
    await portalAuthApi.logout();
  } catch {
    // ignore
  }
  await refreshAuth();
  navigate({ to: "/portal/login" });
  window.location.reload();
}

function SecretBox({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  const grouped = value.replace(/(.{4})/g, "$1 ").trim();
  return (
    <div className="flex items-center gap-2 rounded-md border border-glass bg-glass px-2.5 py-1.5">
      <code className="min-w-0 flex-1 break-all font-mono text-[11px] text-foreground">{grouped}</code>
      <button
        type="button"
        className="shrink-0 text-muted-foreground hover:text-foreground"
        title="Copy"
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(value);
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
          } catch {
            toast.error("Could not copy");
          }
        }}
      >
        {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
      </button>
    </div>
  );
}

export function RecoveryCodes({ codes }: { codes: string[] }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="space-y-2">
      <div className="grid grid-cols-2 gap-1.5 rounded-md border border-glass bg-glass p-3 font-mono text-xs">
        {codes.map((c) => (
          <span key={c} className="text-foreground">
            {c}
          </span>
        ))}
      </div>
      <Button
        type="button"
        variant="secondary"
        size="sm"
        className="gap-2"
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(codes.join("\n"));
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
          } catch {
            toast.error("Could not copy");
          }
        }}
      >
        {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
        Copy all codes
      </Button>
    </div>
  );
}

export function describeAuthError(err: unknown, fallback: string): string {
  if (err instanceof ApiError) {
    if (err.status === 423) return "This account is temporarily locked. Try again in a few minutes.";
    if (err.status === 401) return "Invalid email address or password.";
    if (err.status === 429) return "Too many attempts. Wait a minute and try again.";
    if (err.status === 404) return "The customer portal is not available.";
    if (err.status === 403 && err.body && typeof err.body === "object") {
      const state = (err.body as { state?: string }).state;
      switch (state) {
        case "PendingVerification":
          return "Please confirm your email address first — check your inbox for the confirmation link.";
        case "PendingApproval":
          return "Your registration is awaiting approval by the service desk. You will receive a mail once it is approved.";
        case "Rejected":
          return "This registration was not approved. Contact the service desk if you think this is a mistake.";
        case "Deactivated":
          return "This account has been deactivated. Contact the service desk.";
        default:
          return "This account cannot sign in at the moment.";
      }
    }
  }
  return fallback;
}
