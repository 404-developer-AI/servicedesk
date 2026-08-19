import { useEffect, useImperativeHandle, useRef, useState, forwardRef } from "react";
import { useTheme } from "@/app/ThemeProvider";

// Cloudflare Turnstile, explicit-render mode. Loaded on demand on the
// registration page only (the /portal documents carry the CSP allowance for
// challenges.cloudflare.com). Tokens are single-use: after a failed submit
// the parent calls reset() so the next attempt gets a fresh token.

type TurnstileApi = {
  render: (
    container: HTMLElement,
    options: {
      sitekey: string;
      action?: string;
      theme?: "light" | "dark" | "auto";
      size?: "normal" | "compact" | "flexible";
      callback?: (token: string) => void;
      "expired-callback"?: () => void;
      "error-callback"?: (code?: string) => void;
      "timeout-callback"?: () => void;
    },
  ) => string;
  reset: (widgetId?: string) => void;
  remove: (widgetId?: string) => void;
};

declare global {
  interface Window {
    turnstile?: TurnstileApi;
    __sdTurnstileLoaded?: () => void;
  }
}

const SCRIPT_SRC = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit&onload=__sdTurnstileLoaded";

let loader: Promise<TurnstileApi> | null = null;

function loadTurnstile(): Promise<TurnstileApi> {
  if (typeof window === "undefined") return Promise.reject(new Error("no window"));
  if (window.turnstile) return Promise.resolve(window.turnstile);
  if (loader) return loader;
  loader = new Promise<TurnstileApi>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      loader = null;
      reject(new Error("turnstile_load_timeout"));
    }, 15_000);
    window.__sdTurnstileLoaded = () => {
      window.clearTimeout(timer);
      if (window.turnstile) resolve(window.turnstile);
      else reject(new Error("turnstile_missing"));
    };
    const script = document.createElement("script");
    script.src = SCRIPT_SRC;
    script.async = true;
    script.defer = true;
    script.onerror = () => {
      window.clearTimeout(timer);
      loader = null;
      reject(new Error("turnstile_load_failed"));
    };
    document.head.appendChild(script);
  });
  return loader;
}

export type TurnstileHandle = { reset: () => void };

type Props = {
  siteKey: string;
  action: string;
  onToken: (token: string | null) => void;
  /// Called when the script cannot load (typically a CSP block when the
  /// document was not served under /portal) so the form can explain.
  onUnavailable?: (reason: string) => void;
};

export const TurnstileWidget = forwardRef<TurnstileHandle, Props>(function TurnstileWidget(
  { siteKey, action, onToken, onUnavailable },
  ref,
) {
  const container = useRef<HTMLDivElement | null>(null);
  const widgetId = useRef<string | null>(null);
  const [state, setState] = useState<"loading" | "ready" | "failed">("loading");
  const { mode } = useTheme();
  // Latest callbacks without re-rendering the widget (the Cloudflare
  // iframe must survive parent re-renders).
  const onTokenRef = useRef(onToken);
  const onUnavailableRef = useRef(onUnavailable);
  useEffect(() => {
    onTokenRef.current = onToken;
    onUnavailableRef.current = onUnavailable;
  }, [onToken, onUnavailable]);

  useImperativeHandle(ref, () => ({
    reset: () => {
      try {
        if (widgetId.current && window.turnstile) window.turnstile.reset(widgetId.current);
      } catch {
        // widget may have been removed
      }
      onTokenRef.current(null);
    },
  }));

  useEffect(() => {
    let disposed = false;
    loadTurnstile()
      .then((api) => {
        if (disposed || !container.current) return;
        widgetId.current = api.render(container.current, {
          sitekey: siteKey,
          action,
          theme: mode === "dark" ? "dark" : "light",
          size: "flexible",
          callback: (token) => onTokenRef.current(token),
          "expired-callback": () => onTokenRef.current(null),
          "error-callback": () => onTokenRef.current(null),
          "timeout-callback": () => onTokenRef.current(null),
        });
        setState("ready");
      })
      .catch((err: Error) => {
        if (disposed) return;
        setState("failed");
        onUnavailableRef.current?.(err.message);
      });
    return () => {
      disposed = true;
      try {
        if (widgetId.current && window.turnstile) window.turnstile.remove(widgetId.current);
      } catch {
        // ignore
      }
      widgetId.current = null;
    };
    // The widget is re-rendered when the site key / action / theme change.
  }, [siteKey, action, mode]);

  return (
    <div className="space-y-1.5" data-testid="turnstile">
      <div ref={container} className="min-h-[65px]" />
      {state === "loading" ? (
        <p className="text-[11px] text-muted-foreground">Loading anti-bot check…</p>
      ) : state === "failed" ? (
        <p className="text-[11px] text-destructive/90">
          The anti-bot check could not be loaded. Reload this page and try again.
        </p>
      ) : null}
    </div>
  );
});
