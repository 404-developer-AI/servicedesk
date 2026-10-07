import { lazy, Suspense, useEffect, useState } from "react";
import { cn } from "@/lib/utils";
import { useTheme } from "@/app/ThemeProvider";

// Animated purple/blue "mesh gradient" on a full-screen quad. Cheap fragment
// shader, no post-processing. Scoped to "low-work" surfaces only (stub pages,
// login, 404) — inside the working app the cheaper CSS `.app-background` is
// used instead. Only rendered in dark mode; light mode falls back to the
// static CSS `.app-background` because the shader palette is tuned for a
// near-black canvas. See ARCHITECTURE.md § UI shell & navigation.

function usePrefersReducedMotion(): boolean {
  const [reduced, setReduced] = useState<boolean>(() => {
    if (typeof window === "undefined" || !window.matchMedia) return false;
    return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  });

  useEffect(() => {
    const mql = window.matchMedia("(prefers-reduced-motion: reduce)");
    const handler = (e: MediaQueryListEvent) => setReduced(e.matches);
    mql.addEventListener("change", handler);
    return () => mql.removeEventListener("change", handler);
  }, []);

  return reduced;
}

function useDocumentVisible(): boolean {
  const [visible, setVisible] = useState<boolean>(() => {
    if (typeof document === "undefined") return true;
    return document.visibilityState !== "hidden";
  });
  useEffect(() => {
    const handler = () => setVisible(document.visibilityState !== "hidden");
    document.addEventListener("visibilitychange", handler);
    return () => document.removeEventListener("visibilitychange", handler);
  }, []);
  return visible;
}

const MeshCanvas = lazy(() => import("./MeshCanvas").then((m) => ({ default: m.MeshCanvas })));

type MeshSurfaceProps = {
  className?: string;
};

/**
 * WebGL mesh background restricted to "low-work" surfaces (stub pages, login,
 * 404). Falls back to the static CSS gradient when the user prefers reduced
 * motion, and pauses the render loop when the tab is hidden.
 */
export function MeshSurface({ className }: MeshSurfaceProps) {
  const reduced = usePrefersReducedMotion();
  const visible = useDocumentVisible();
  const { mode } = useTheme();

  // The shader palette is calibrated for a dark canvas — running it in
  // light mode produces muddy purple haze on a near-white background. Fall
  // back to the static CSS gradient, which already has a light-mode look.
  // Steaan is light-only (and flat by design), so it takes this branch too.
  if (reduced || mode === "light") {
    return <div className={cn("app-background", className)} aria-hidden />;
  }

  return (
    <div className={cn("pointer-events-none", className)} aria-hidden>
      <Suspense fallback={<div className="app-background h-full w-full" />}>
        <MeshCanvas visible={visible} />
      </Suspense>
    </div>
  );
}
