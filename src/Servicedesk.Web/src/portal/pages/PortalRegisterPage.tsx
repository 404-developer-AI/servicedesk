import { useRef, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { MailCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ApiError, apiErrorMessage } from "@/lib/api";
import { portalAuthApi } from "@/lib/portal-api";
import { SafeHtml } from "@/components/SafeHtml";
import { TurnstileWidget, type TurnstileHandle } from "@/portal/TurnstileWidget";
import { FieldError, FieldLabel, FormError, FormNotice, PortalAuthLayout, usePortalConfig } from "@/portal/PortalAuthLayout";

export function PortalRegisterPage() {
  const config = usePortalConfig();
  const minLength = config.data?.enabled ? config.data.passwordMinimumLength : 12;
  const registrationEnabled = config.data?.enabled ? config.data.registrationEnabled : true;
  const turnstile = config.data?.enabled ? config.data.turnstile : null;
  const turnstileRef = useRef<TurnstileHandle | null>(null);
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [turnstileUnavailable, setTurnstileUnavailable] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const schema = z
    .object({
      displayName: z.string().trim().min(2, "Enter your name").max(120, "Keep it under 120 characters"),
      email: z.string().min(1, "Email is required").email("Enter a valid email"),
      password: z.string().min(minLength, `Use at least ${minLength} characters`),
      confirm: z.string(),
    })
    .refine((v) => v.password === v.confirm, { message: "Passwords do not match", path: ["confirm"] });
  type Values = z.infer<typeof schema>;

  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { displayName: "", email: "", password: "", confirm: "" },
  });

  const onSubmit = form.handleSubmit(async (values) => {
    setServerError(null);
    if (turnstile?.enabled && turnstile.siteKey && !turnstileToken) {
      setServerError("Please complete the anti-bot check first.");
      return;
    }
    try {
      await portalAuthApi.register(values.email, values.password, values.displayName, turnstileToken);
      setDone(values.email);
    } catch (e) {
      turnstileRef.current?.reset();
      if (e instanceof ApiError && e.status === 429) {
        setServerError("Too many attempts. Wait a few minutes and try again.");
        return;
      }
      setServerError(apiErrorMessage(e) ?? "Registration failed. Please try again.");
    }
  });

  const intro = config.data?.enabled ? config.data.registrationIntroHtml : "";

  return (
    <PortalAuthLayout
      title={done ? "Check your inbox" : "Create your account"}
      subtitle={
        done
          ? undefined
          : "Register with your work email address. After you confirm it, the service desk reviews and approves your account."
      }
      footer={
        <span>
          Already have an account?{" "}
          <Link to="/portal/login" className="font-medium text-primary hover:underline">
            Sign in
          </Link>
        </span>
      }
      wide
    >
      {done ? (
        <div className="space-y-4">
          <FormNotice tone="success">
            <div className="flex items-start gap-2.5">
              <MailCheck className="mt-0.5 h-4 w-4 shrink-0 text-emerald-500" />
              <div>
                <p className="font-medium">We sent a confirmation link to {done}.</p>
                <p className="mt-1 text-muted-foreground">
                  Open it to confirm your email address. Your registration is then reviewed by the service desk; you
                  receive another mail as soon as your account is approved. If you do not see the mail, check your spam
                  folder or submit the form again to receive a new link.
                </p>
              </div>
            </div>
          </FormNotice>
        </div>
      ) : !registrationEnabled ? (
        <FormNotice>
          Self-registration is not available. Ask your contact at the service desk for an invitation.
        </FormNotice>
      ) : (
        <form onSubmit={onSubmit} className="space-y-4" noValidate data-testid="portal-register-form">
          {intro ? <SafeHtml html={intro} className="prose prose-sm max-w-none text-sm text-muted-foreground" /> : null}
          <div className="space-y-1.5">
            <FieldLabel>Your name</FieldLabel>
            <Input autoComplete="name" placeholder="First and last name" {...form.register("displayName")} />
            <FieldError message={form.formState.errors.displayName?.message} />
          </div>
          <div className="space-y-1.5">
            <FieldLabel>Work email</FieldLabel>
            <Input type="email" autoComplete="email" placeholder="you@company.com" {...form.register("email")} />
            <FieldError message={form.formState.errors.email?.message} />
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <FieldLabel>Password</FieldLabel>
              <Input type="password" autoComplete="new-password" placeholder={`At least ${minLength} characters`} {...form.register("password")} />
              <FieldError message={form.formState.errors.password?.message} />
            </div>
            <div className="space-y-1.5">
              <FieldLabel>Repeat password</FieldLabel>
              <Input type="password" autoComplete="new-password" {...form.register("confirm")} />
              <FieldError message={form.formState.errors.confirm?.message} />
            </div>
          </div>
          {turnstile?.enabled && turnstile.siteKey ? (
            <TurnstileWidget
              ref={turnstileRef}
              siteKey={turnstile.siteKey}
              action={turnstile.action || "portal-register"}
              onToken={setTurnstileToken}
              onUnavailable={() => setTurnstileUnavailable(true)}
            />
          ) : null}
          <FormError message={serverError} />
          <Button
            type="submit"
            className="w-full"
            disabled={form.formState.isSubmitting || turnstileUnavailable || (turnstile?.enabled && !!turnstile.siteKey && !turnstileToken)}
          >
            {form.formState.isSubmitting ? "Creating account…" : "Create account"}
          </Button>
          <p className="text-[11px] text-muted-foreground">
            Two-factor authentication with an authenticator app is mandatory; you set it up at your first sign-in.
          </p>
        </form>
      )}
    </PortalAuthLayout>
  );
}
