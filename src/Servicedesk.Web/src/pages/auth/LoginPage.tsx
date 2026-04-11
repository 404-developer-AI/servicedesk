import { useEffect, useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { motion } from "framer-motion";
import { Sparkles, LockKeyhole, Mail, ShieldCheck } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authApi, ApiError } from "@/lib/api";
import { authStore } from "@/auth/authStore";
import { refreshAuth } from "@/auth/bootstrap";

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
    toast.message("Microsoft sign-in is not yet available", {
      description: "Local admin sign-in only in this release.",
    });
  };

  return (
    <div className="app-background relative flex min-h-screen items-center justify-center px-4 py-10">
      <motion.div
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.35, ease: "easeOut" }}
        className="glass-card w-full max-w-[420px] overflow-hidden"
      >
        <div className="flex items-center gap-3 border-b border-white/5 px-7 pt-6 pb-5">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-[calc(var(--radius)-4px)] bg-gradient-to-br from-accent-purple to-accent-blue shadow-[0_0_22px_-4px_hsl(var(--primary)/0.6)]">
            <Sparkles className="h-5 w-5 text-white" />
          </div>
          <div className="min-w-0">
            <div className="truncate text-[11px] uppercase tracking-[0.22em] text-muted-foreground">
              Servicedesk
            </div>
            <h1 className="truncate font-display text-display-sm font-semibold">
              {stage === "credentials" ? "Welcome back" : "Two-factor check"}
            </h1>
          </div>
        </div>

        <div className="space-y-5 px-7 py-6">
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
        </div>
      </motion.div>
    </div>
  );
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
