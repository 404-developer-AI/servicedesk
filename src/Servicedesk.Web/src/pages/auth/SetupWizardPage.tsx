import { useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { motion } from "framer-motion";
import { Sparkles, ShieldCheck, ArrowRight, LockKeyhole, Mail, CheckCircle2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authApi, ApiError } from "@/lib/api";
import { authStore } from "@/auth/authStore";
import { refreshAuth } from "@/auth/bootstrap";

const schema = z
  .object({
    email: z.string().min(1).email("Enter a valid email"),
    password: z.string().min(12, "At least 12 characters"),
    confirm: z.string(),
  })
  .refine((v) => v.password === v.confirm, {
    message: "Passwords do not match",
    path: ["confirm"],
  });

type Values = z.infer<typeof schema>;

type Step = "welcome" | "credentials" | "done";

export function SetupWizardPage() {
  const navigate = useNavigate();
  const [step, setStep] = useState<Step>("welcome");
  const [serverError, setServerError] = useState<string | null>(null);

  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { email: "", password: "", confirm: "" },
  });

  const onSubmit = form.handleSubmit(async (values) => {
    setServerError(null);
    try {
      await authApi.createAdmin(values.email.trim(), values.password);
      authStore.patch({ setupAvailable: false });
      await refreshAuth();
      setStep("done");
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        setServerError("Setup is no longer available. Reload to continue.");
      } else {
        setServerError("Could not create the admin account. Check the server logs.");
      }
    }
  });

  return (
    <div className="app-background relative flex min-h-screen items-center justify-center px-4 py-10">
      <motion.div
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.35, ease: "easeOut" }}
        className="glass-card w-full max-w-[520px] overflow-hidden"
      >
        <div className="flex items-center gap-3 border-b border-white/5 px-7 pt-6 pb-5">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-[calc(var(--radius)-4px)] bg-gradient-to-br from-accent-purple to-accent-blue shadow-[0_0_22px_-4px_hsl(var(--primary)/0.6)]">
            <Sparkles className="h-5 w-5 text-white" />
          </div>
          <div className="min-w-0">
            <div className="truncate text-[11px] uppercase tracking-[0.22em] text-muted-foreground">
              First-run setup
            </div>
            <h1 className="truncate font-display text-display-sm font-semibold">
              {step === "welcome" && "Let's get you set up"}
              {step === "credentials" && "Create the first admin"}
              {step === "done" && "You're all set"}
            </h1>
          </div>
        </div>

        <div className="space-y-5 px-7 py-6">
          {step === "welcome" && (
            <>
              <p className="text-sm text-muted-foreground">
                This install doesn't have any users yet. We'll create a single
                admin account in the next step — you can invite more people and
                tweak policies from the Settings page afterwards.
              </p>
              <div className="space-y-2 text-xs text-muted-foreground">
                <div className="flex items-center gap-2">
                  <ShieldCheck className="h-3.5 w-3.5 text-primary" />
                  Passwords are hashed with Argon2id.
                </div>
                <div className="flex items-center gap-2">
                  <ShieldCheck className="h-3.5 w-3.5 text-primary" />
                  Optional TOTP two-factor is available on your profile page.
                </div>
                <div className="flex items-center gap-2">
                  <ShieldCheck className="h-3.5 w-3.5 text-primary" />
                  All auth events are audit-logged with a tamper-evident hash chain.
                </div>
              </div>
              <Button
                className="w-full justify-center gap-2"
                onClick={() => setStep("credentials")}
              >
                Continue <ArrowRight className="h-4 w-4" />
              </Button>
            </>
          )}

          {step === "credentials" && (
            <form onSubmit={onSubmit} className="space-y-4" noValidate>
              <div className="space-y-1.5">
                <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
                  Admin email
                </label>
                <div className="relative">
                  <Mail className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/70" />
                  <Input
                    type="email"
                    autoComplete="username"
                    placeholder="you@example.com"
                    className="pl-9"
                    {...form.register("email")}
                  />
                </div>
                {form.formState.errors.email && (
                  <p className="text-[11px] text-destructive/90">
                    {form.formState.errors.email.message}
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
                    autoComplete="new-password"
                    className="pl-9"
                    {...form.register("password")}
                  />
                </div>
                {form.formState.errors.password && (
                  <p className="text-[11px] text-destructive/90">
                    {form.formState.errors.password.message}
                  </p>
                )}
              </div>
              <div className="space-y-1.5">
                <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
                  Confirm password
                </label>
                <Input type="password" autoComplete="new-password" {...form.register("confirm")} />
                {form.formState.errors.confirm && (
                  <p className="text-[11px] text-destructive/90">
                    {form.formState.errors.confirm.message}
                  </p>
                )}
              </div>

              {serverError && (
                <div className="rounded-md border border-destructive/40 bg-destructive/[0.06] px-3 py-2 text-xs text-destructive/90">
                  {serverError}
                </div>
              )}

              <Button type="submit" className="w-full" disabled={form.formState.isSubmitting}>
                {form.formState.isSubmitting ? "Creating…" : "Create admin"}
              </Button>
            </form>
          )}

          {step === "done" && (
            <>
              <div className="flex items-center gap-3 rounded-md border border-emerald-400/20 bg-emerald-400/[0.04] px-4 py-3 text-sm text-emerald-200/90">
                <CheckCircle2 className="h-5 w-5 text-emerald-400" />
                Admin account created. You're signed in.
              </div>
              <Button
                className="w-full"
                onClick={() => {
                  toast.success("Welcome to Servicedesk");
                  navigate({ to: "/" });
                }}
              >
                Go to dashboard
              </Button>
            </>
          )}
        </div>
      </motion.div>
    </div>
  );
}
