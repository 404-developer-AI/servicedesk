import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useQuery } from "@tanstack/react-query";
import { motion } from "framer-motion";
import { LockKeyhole, Mail, ShieldCheck, AlertTriangle } from "lucide-react";
import ticksyWordmark from "@/assets/brand/ticksy-dark.svg";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authApi, ApiError } from "@/lib/api";
import { authStore } from "@/auth/authStore";
import { refreshAuth } from "@/auth/bootstrap";
import { MaintenanceBanner } from "@/components/maintenance/MaintenanceBanner";

const loginSchema = z.object({
  email: z.string().min(1, "Email is required").email("Enter a valid email"),
  password: z.string().min(1, "Password is required"),
});

const codeSchema = z.object({
  code: z.string().min(6, "Enter the 6-digit code or a recovery code"),
});

type LoginValues = z.infer<typeof loginSchema>;
type CodeValues = z.infer<typeof codeSchema>;

type Stage = "credentials" | "two-factor";

export function LoginPage() {
  const navigate = useNavigate();
  const [stage, setStage] = useState<Stage>("credentials");
  const [serverError, setServerError] = useState<string | null>(null);

  // Feature-flag snapshot for the M365 button. Failing fast here (stale
  // or missing config) must not break the local-login path — we render
  // the page regardless and only gate the Microsoft button on the flag.
  const { data: config } = useQuery({
    queryKey: ["auth", "config"],
    queryFn: () => authApi.config(),
    staleTime: 60_000,
  });

  // Reads `?error=…` set by the M365 callback on redirect-to-/login.
  // Done once on mount; subsequent renders shouldn't surface a stale
  // banner after a user-driven retry. Grabbed from window.location
  // because this page is outside the router's typed-search zone.
  const callbackError = useMemo(() => {
    if (typeof window === "undefined") return null;
    const params = new URLSearchParams(window.location.search);
    return params.get("error");
  }, []);
  const callbackErrorMessage = useMemo(
    () => (callbackError ? describeCallbackError(callbackError) : null),
    [callbackError],
  );

  useEffect(() => {
    // If someone lands on /login with an active session, bounce to dashboard.
    if (authStore.get().user) {
      navigate({ to: "/" });
    }
  }, [navigate]);

  const loginForm = useForm<LoginValues>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: "", password: "" },
  });

  const codeForm = useForm<CodeValues>({
    resolver: zodResolver(codeSchema),
    defaultValues: { code: "" },
  });

  const onLogin = loginForm.handleSubmit(async (values) => {
    setServerError(null);
    try {
      const res = await authApi.login(values.email, values.password);
      await refreshAuth();
      if (res.twoFactorRequired) {
        setStage("two-factor");
        return;
      }
      toast.success("Welcome back");
      navigate({ to: "/" });
    } catch (e) {
      setServerError(describeAuthError(e, "Invalid credentials."));
    }
  });

  const onVerify = codeForm.handleSubmit(async (values) => {
    setServerError(null);
    try {
      await authApi.verifyTwoFactor(values.code.trim());
      await refreshAuth();
      toast.success("Two-factor verified");
      navigate({ to: "/" });
    } catch (e) {
      setServerError(describeAuthError(e, "Verification failed."));
    }
  });

  const onMicrosoft = () => {
    // Top-level redirect into the backend's /challenge endpoint. The
    // server reads the Auth.Microsoft.Enabled flag again on that request
    // — belt-and-braces in case config.microsoftEnabled is stale. It
    // then sets the intent cookie, redirects to login.microsoftonline.com,
    // and the whole OIDC round-trip lands back on /login with either a
    // minted session (→ redirect to /) or ?error=… query.
    window.location.href = "/api/auth/microsoft/challenge";
  };

  return (
    <div className="app-background relative flex min-h-screen flex-col items-center justify-center px-4 py-10">
      <MaintenanceBanner variant="auth" />
      <motion.div
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.35, ease: "easeOut" }}
        className="glass-card w-full max-w-[420px] overflow-hidden"
      >
        <div className="flex items-center justify-center border-b border-white/5 px-7 py-3">
          <img
            src={ticksyWordmark}
            alt="Ticksy"
            draggable={false}
            className="h-24 w-auto select-none"
          />
        </div>

        <div className="space-y-5 px-7 py-6">
          {callbackErrorMessage && stage === "credentials" && (
            <div
              role="alert"
              className="flex items-start gap-2.5 rounded-md border border-amber-500/30 bg-amber-500/[0.08] px-3 py-2.5 text-xs text-amber-200"
            >
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-300" />
              <div>
                <p className="font-medium">Microsoft sign-in failed</p>
                <p className="mt-0.5 opacity-90">{callbackErrorMessage}</p>
              </div>
            </div>
          )}

          {stage === "credentials" ? (
            <form onSubmit={onLogin} className="space-y-4" noValidate>
              <div className="space-y-1.5">
                <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
                  Email
                </label>
                <div className="relative">
                  <Mail className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/70" />
                  <Input
                    type="email"
                    autoComplete="username"
                    placeholder="you@example.com"
                    className="pl-9"
                    {...loginForm.register("email")}
                  />
                </div>
                {loginForm.formState.errors.email && (
                  <p className="text-[11px] text-destructive/90">
                    {loginForm.formState.errors.email.message}
                  </p>
                )}
              </div>

              <div className="space-y-1.5">
                <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
                  Password
                </label>
                <div className="relative">
                  <LockKeyhole className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/70" />
                  <Input
                    type="password"
                    autoComplete="current-password"
                    placeholder="••••••••••••"
                    className="pl-9"
                    {...loginForm.register("password")}
                  />
                </div>
                {loginForm.formState.errors.password && (
                  <p className="text-[11px] text-destructive/90">
                    {loginForm.formState.errors.password.message}
                  </p>
                )}
              </div>

              {serverError && (
                <div className="rounded-md border border-destructive/40 bg-destructive/[0.06] px-3 py-2 text-xs text-destructive/90">
                  {serverError}
                </div>
              )}

              <Button
                type="submit"
                className="w-full"
                disabled={loginForm.formState.isSubmitting}
              >
                {loginForm.formState.isSubmitting ? "Signing in…" : "Sign in"}
              </Button>
            </form>
          ) : (
            <form onSubmit={onVerify} className="space-y-4" noValidate>
              <p className="text-sm text-muted-foreground">
                Enter the 6-digit code from your authenticator app, or a single-use
                recovery code.
              </p>
              <div className="space-y-1.5">
                <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
                  Code
                </label>
                <Input
                  autoFocus
                  inputMode="text"
                  autoComplete="one-time-code"
                  placeholder="123 456"
                  className="font-mono tracking-[0.2em]"
                  {...codeForm.register("code")}
                />
                {codeForm.formState.errors.code && (
                  <p className="text-[11px] text-destructive/90">
                    {codeForm.formState.errors.code.message}
                  </p>
                )}
              </div>
              {serverError && (
                <div className="rounded-md border border-destructive/40 bg-destructive/[0.06] px-3 py-2 text-xs text-destructive/90">
                  {serverError}
                </div>
              )}
              <Button type="submit" className="w-full" disabled={codeForm.formState.isSubmitting}>
                {codeForm.formState.isSubmitting ? "Verifying…" : "Verify"}
              </Button>
            </form>
          )}

          {config?.microsoftEnabled && stage === "credentials" && (
            <>
              <div className="relative py-1 text-center">
                <div className="absolute inset-x-0 top-1/2 h-px -translate-y-1/2 bg-white/5" />
                <span className="relative inline-block bg-transparent px-3 text-[10px] uppercase tracking-[0.22em] text-muted-foreground">
                  or
                </span>
              </div>

              <Button
                type="button"
                variant="secondary"
                className="w-full justify-center gap-2"
                onClick={onMicrosoft}
                data-testid="m365-signin"
              >
                <ShieldCheck className="h-4 w-4" />
                Sign in with Microsoft
              </Button>
            </>
          )}
        </div>
      </motion.div>
    </div>
  );
}

// Map the backend's `?error=<code>` values — emitted by
// MicrosoftAuthEndpoints.RedirectToLogin and MapRejection — to human
// copy. Unknown codes fall through to a generic message so we never
// render the raw token to the user.
function describeCallbackError(code: string): string {
  switch (code) {
    case "not_authorized":
      return "Your Microsoft account is not linked to this servicedesk. Ask an admin to add you via Settings → Users.";
    case "disabled":
      return "Your Microsoft account is disabled. Ask your tenant admin to re-enable it and try again.";
    case "inactive":
      return "Your servicedesk account has been deactivated. Ask an admin to reactivate it.";
    case "invalid_token":
      return "The sign-in token could not be validated. Try again; if the problem persists, contact support.";
    case "code_exchange_failed":
      return "Microsoft rejected the sign-in request. Try again or contact your admin.";
    case "state_mismatch":
    case "missing_intent":
    case "invalid_callback":
      return "The sign-in session expired. Please start again.";
    case "access_denied":
      return "Sign-in was cancelled at Microsoft.";
    case "consent_required":
    case "interaction_required":
      return "Your tenant requires additional consent or sign-in. Please try again.";
    default:
      return "Microsoft sign-in did not complete. Please try again or use your local account.";
  }
}

function describeAuthError(err: unknown, fallback: string): string {
  if (err instanceof ApiError) {
    if (err.status === 423) {
      return "This account is temporarily locked. Try again in a few minutes.";
    }
    if (err.status === 401) {
      return "Invalid credentials.";
    }
    if (err.status === 429) {
      return "Too many attempts. Wait a minute and try again.";
    }
  }
  return fallback;
}
