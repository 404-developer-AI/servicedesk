import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, MailCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ApiError, apiErrorMessage } from "@/lib/api";
import { portalAuthApi } from "@/lib/portal-api";
import { FieldError, FieldLabel, FormError, FormNotice, PortalAuthLayout, usePortalConfig } from "@/portal/PortalAuthLayout";

// The four token-driven pages share one file: email verification,
// forgot/reset password, invitation acceptance. Each reads ?token=… from the
// URL (pages are outside the router's typed-search zone, like /login).

function useUrlToken(): string {
  return useMemo(() => {
    if (typeof window === "undefined") return "";
    return new URLSearchParams(window.location.search).get("token") ?? "";
  }, []);
}

// ---- verify email ----------------------------------------------------------

export function PortalVerifyEmailPage() {
  const token = useUrlToken();
  const [state, setState] = useState<"working" | "verified" | "already" | "expired" | "invalid">(() =>
    token ? "working" : "invalid",
  );

  useEffect(() => {
    if (!token) return;
    let disposed = false;
    portalAuthApi
      .verifyEmail(token)
      .then((r) => {
        if (disposed) return;
        setState(r.status === "already_verified" ? "already" : "verified");
      })
      .catch((e: unknown) => {
        if (disposed) return;
        setState(e instanceof ApiError && e.status === 410 ? "expired" : "invalid");
      });
    return () => {
      disposed = true;
    };
  }, [token]);

  return (
    <PortalAuthLayout
      title="Confirm your email address"
      footer={
        <Link to="/portal/login" className="font-medium text-primary hover:underline">
          Go to sign in
        </Link>
      }
    >
      {state === "working" && <p className="text-sm text-muted-foreground">Confirming…</p>}
      {(state === "verified" || state === "already") && (
        <FormNotice tone="success">
          <div className="flex items-start gap-2.5">
            <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-500" />
            <div>
              <p className="font-medium">
                {state === "verified" ? "Your email address is confirmed." : "This email address was already confirmed."}
              </p>
              <p className="mt-1 text-muted-foreground">
                The service desk now reviews your registration. You receive a mail as soon as your account is approved —
                signing in is possible from then on.
              </p>
            </div>
          </div>
        </FormNotice>
      )}
      {state === "expired" && (
        <FormError message="This confirmation link has expired. Register again with the same email address to receive a new one." />
      )}
      {state === "invalid" && <FormError message="This confirmation link is not valid." />}
      {state === "expired" && (
        <Link to="/portal/register" className="block text-center text-xs text-muted-foreground hover:text-foreground">
          Back to registration
        </Link>
      )}
    </PortalAuthLayout>
  );
}

// ---- forgot password ------------------------------------------------------

const emailSchema = z.object({ email: z.string().min(1, "Email is required").email("Enter a valid email") });

export function PortalForgotPasswordPage() {
  const [sent, setSent] = useState<string | null>(null);
  const [serverError, setServerError] = useState<string | null>(null);
  const form = useForm<z.infer<typeof emailSchema>>({ resolver: zodResolver(emailSchema), defaultValues: { email: "" } });

  const onSubmit = form.handleSubmit(async (values) => {
    setServerError(null);
    try {
      await portalAuthApi.forgotPassword(values.email);
      setSent(values.email);
    } catch (e) {
      setServerError(e instanceof ApiError && e.status === 429 ? "Too many attempts. Wait a few minutes and try again." : "Something went wrong. Please try again.");
    }
  });

  return (
    <PortalAuthLayout
      title="Reset your password"
      subtitle={sent ? undefined : "Enter the email address of your portal account and we send you a reset link."}
      footer={
        <Link to="/portal/login" className="hover:text-foreground">
          Back to sign in
        </Link>
      }
    >
      {sent ? (
        <FormNotice tone="success">
          <div className="flex items-start gap-2.5">
            <MailCheck className="mt-0.5 h-4 w-4 shrink-0 text-emerald-500" />
            <div>
              <p className="font-medium">If an active portal account exists for {sent}, a reset link is on its way.</p>
              <p className="mt-1 text-muted-foreground">The link is valid for a limited time. Check your spam folder if it does not arrive.</p>
            </div>
          </div>
        </FormNotice>
      ) : (
        <form onSubmit={onSubmit} className="space-y-4" noValidate>
          <div className="space-y-1.5">
            <FieldLabel>Email</FieldLabel>
            <Input type="email" autoComplete="username" placeholder="you@company.com" {...form.register("email")} />
            <FieldError message={form.formState.errors.email?.message} />
          </div>
          <FormError message={serverError} />
          <Button type="submit" className="w-full" disabled={form.formState.isSubmitting}>
            {form.formState.isSubmitting ? "Sending…" : "Send reset link"}
          </Button>
        </form>
      )}
    </PortalAuthLayout>
  );
}

// ---- reset password -------------------------------------------------------

function usePasswordSchema() {
  const config = usePortalConfig();
  const minLength = config.data?.enabled ? config.data.passwordMinimumLength : 12;
  return useMemo(
    () =>
      z
        .object({
          password: z.string().min(minLength, `Use at least ${minLength} characters`),
          confirm: z.string(),
        })
        .refine((v) => v.password === v.confirm, { message: "Passwords do not match", path: ["confirm"] }),
    [minLength],
  );
}

export function PortalResetPasswordPage() {
  const token = useUrlToken();
  const navigate = useNavigate();
  const schema = usePasswordSchema();
  const [serverError, setServerError] = useState<string | null>(null);
  const form = useForm<z.infer<typeof schema>>({ resolver: zodResolver(schema), defaultValues: { password: "", confirm: "" } });

  const onSubmit = form.handleSubmit(async (values) => {
    setServerError(null);
    try {
      await portalAuthApi.resetPassword(token, values.password);
      navigate({ to: "/portal/login", search: { notice: "reset" } as never });
    } catch (e) {
      if (e instanceof ApiError && e.status === 410) setServerError("This reset link has expired. Request a new one.");
      else if (e instanceof ApiError && e.status === 404) setServerError("This reset link is not valid. Request a new one.");
      else setServerError(apiErrorMessage(e) ?? "Could not reset the password.");
    }
  });

  return (
    <PortalAuthLayout
      title="Choose a new password"
      footer={
        <Link to="/portal/forgot-password" className="hover:text-foreground">
          Request a new link
        </Link>
      }
    >
      {!token ? (
        <FormError message="This reset link is not valid." />
      ) : (
        <form onSubmit={onSubmit} className="space-y-4" noValidate>
          <div className="space-y-1.5">
            <FieldLabel>New password</FieldLabel>
            <Input type="password" autoComplete="new-password" {...form.register("password")} />
            <FieldError message={form.formState.errors.password?.message} />
          </div>
          <div className="space-y-1.5">
            <FieldLabel>Repeat password</FieldLabel>
            <Input type="password" autoComplete="new-password" {...form.register("confirm")} />
            <FieldError message={form.formState.errors.confirm?.message} />
          </div>
          <FormError message={serverError} />
          <Button type="submit" className="w-full" disabled={form.formState.isSubmitting}>
            {form.formState.isSubmitting ? "Saving…" : "Set new password"}
          </Button>
          <p className="text-[11px] text-muted-foreground">All your open portal sessions are signed out after the change.</p>
        </form>
      )}
    </PortalAuthLayout>
  );
}

// ---- invitation -----------------------------------------------------------

export function PortalInvitationPage() {
  const token = useUrlToken();
  const navigate = useNavigate();
  const schema = usePasswordSchema();
  const [serverError, setServerError] = useState<string | null>(null);
  const info = useQuery({
    queryKey: ["portal", "invitation", token],
    queryFn: () => portalAuthApi.describeInvitation(token),
    enabled: token.length > 0,
    retry: false,
  });
  const form = useForm<z.infer<typeof schema>>({ resolver: zodResolver(schema), defaultValues: { password: "", confirm: "" } });

  const onSubmit = form.handleSubmit(async (values) => {
    setServerError(null);
    try {
      await portalAuthApi.acceptInvitation(token, values.password);
      navigate({ to: "/portal/login", search: { notice: "activated" } as never });
    } catch (e) {
      setServerError(apiErrorMessage(e) ?? "Could not activate the account.");
    }
  });

  const infoError = info.error instanceof ApiError ? info.error : null;

  return (
    <PortalAuthLayout
      title="Activate your portal account"
      subtitle={info.data ? `You were invited as ${info.data.displayName || info.data.email}${info.data.companyName ? ` (${info.data.companyName})` : ""}. Choose a password to finish.` : undefined}
      footer={
        <Link to="/portal/login" className="hover:text-foreground">
          Already activated? Sign in
        </Link>
      }
    >
      {!token ? (
        <FormError message="This invitation link is not valid." />
      ) : info.isLoading ? (
        <p className="text-sm text-muted-foreground">Checking your invitation…</p>
      ) : infoError ? (
        <FormError
          message={
            infoError.status === 410
              ? "This invitation has expired. Ask the service desk to send a new one."
              : infoError.status === 409
                ? "This invitation was already used. Sign in instead."
                : "This invitation link is not valid."
          }
        />
      ) : (
        <form onSubmit={onSubmit} className="space-y-4" noValidate>
          <div className="space-y-1.5">
            <FieldLabel>Email</FieldLabel>
            <Input value={info.data?.email ?? ""} readOnly className="bg-glass" />
          </div>
          <div className="space-y-1.5">
            <FieldLabel>Password</FieldLabel>
            <Input type="password" autoComplete="new-password" {...form.register("password")} />
            <FieldError message={form.formState.errors.password?.message} />
          </div>
          <div className="space-y-1.5">
            <FieldLabel>Repeat password</FieldLabel>
            <Input type="password" autoComplete="new-password" {...form.register("confirm")} />
            <FieldError message={form.formState.errors.confirm?.message} />
          </div>
          <FormError message={serverError} />
          <Button type="submit" className="w-full" disabled={form.formState.isSubmitting}>
            {form.formState.isSubmitting ? "Activating…" : "Activate account"}
          </Button>
          <p className="text-[11px] text-muted-foreground">
            At your first sign-in you set up an authenticator app for two-factor authentication.
          </p>
        </form>
      )}
    </PortalAuthLayout>
  );
}
