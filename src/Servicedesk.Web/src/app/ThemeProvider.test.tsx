import { describe, expect, it, vi, beforeEach } from "vitest";
import { act, render, waitFor } from "@testing-library/react";
import { useEffect } from "react";

const systemApi = vi.hoisted(() => ({
  defaultTheme: vi.fn(),
}));
const preferencesApi = vi.hoisted(() => ({
  setUiTheme: vi.fn(),
}));

vi.mock("@/lib/api", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api")>("@/lib/api");
  return { ...actual, systemApi, preferencesApi };
});

import { ThemeProvider, useTheme } from "./ThemeProvider";
import { authStore } from "@/auth/authStore";
import type { AuthUser } from "@/auth/authStore";

let ctx: ReturnType<typeof useTheme> | null = null;
function Probe() {
  const value = useTheme();
  useEffect(() => {
    ctx = value;
  });
  return null;
}

function fakeUser(effectiveTheme: AuthUser["effectiveTheme"]): AuthUser {
  return { id: "u1", email: "a@b.c", role: "Agent", effectiveTheme } as AuthUser;
}

/**
 * v0.0.108 — three themes. Steaan (flat, light-only) is the factory
 * default; the Nebula glass theme keeps its light/dark pair. The provider
 * stamps `.theme-steaan` / `.dark` on <html> exactly like the inline
 * bootstrap in index.html, so components and CSS see one vocabulary.
 */
describe("ThemeProvider", () => {
  beforeEach(() => {
    ctx = null;
    window.localStorage.clear();
    document.documentElement.className = "";
    systemApi.defaultTheme.mockReset();
    systemApi.defaultTheme.mockResolvedValue({ theme: "steaan" });
    preferencesApi.setUiTheme.mockReset();
    preferencesApi.setUiTheme.mockResolvedValue(undefined);
    authStore.set({ status: "ready", user: null, setupAvailable: false });
  });

  it("defaults to Steaan and stamps the root class", async () => {
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    await waitFor(() => expect(systemApi.defaultTheme).toHaveBeenCalled());
    expect(ctx?.theme).toBe("steaan");
    expect(ctx?.family).toBe("steaan");
    expect(ctx?.mode).toBe("light");
    expect(document.documentElement.classList.contains("theme-steaan")).toBe(true);
    expect(document.documentElement.classList.contains("dark")).toBe(false);
    expect(document.documentElement.style.colorScheme).toBe("light");
  });

  it("switching to Nebula dark swaps the classes; toggle flips only Nebula modes", async () => {
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    act(() => ctx!.setTheme("dark"));
    expect(ctx?.family).toBe("nebula");
    expect(ctx?.mode).toBe("dark");
    expect(document.documentElement.classList.contains("dark")).toBe(true);
    expect(document.documentElement.classList.contains("theme-steaan")).toBe(false);
    expect(window.localStorage.getItem("sd-theme")).toBe("dark");

    act(() => ctx!.toggle());
    expect(ctx?.theme).toBe("light");

    // Steaan is light-only: toggle is a no-op there.
    act(() => ctx!.setTheme("steaan"));
    act(() => ctx!.toggle());
    expect(ctx?.theme).toBe("steaan");
  });

  it("hydrates from the signed-in user's effective theme without writing it back", async () => {
    window.localStorage.setItem("sd-theme", "light");
    authStore.set({ status: "ready", user: fakeUser("dark"), setupAvailable: false });
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    await waitFor(() => expect(ctx?.theme).toBe("dark"));
    expect(systemApi.defaultTheme).not.toHaveBeenCalled();
    expect(preferencesApi.setUiTheme).not.toHaveBeenCalled();
  });

  it("persists an explicit change for a signed-in user — also when the server value matched the cache", async () => {
    window.localStorage.setItem("sd-theme", "steaan");
    authStore.set({ status: "ready", user: fakeUser("steaan"), setupAvailable: false });
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    expect(ctx?.theme).toBe("steaan");
    act(() => ctx!.setTheme("light"));
    await waitFor(() => expect(preferencesApi.setUiTheme).toHaveBeenCalledWith("light"));
    expect(preferencesApi.setUiTheme).toHaveBeenCalledTimes(1);
  });

  it("re-hydrates when a user signs in mid-session", async () => {
    systemApi.defaultTheme.mockResolvedValue({ theme: "steaan" });
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    await waitFor(() => expect(systemApi.defaultTheme).toHaveBeenCalled());
    expect(ctx?.theme).toBe("steaan");
    act(() => authStore.set({ status: "ready", user: fakeUser("dark"), setupAvailable: false }));
    await waitFor(() => expect(ctx?.theme).toBe("dark"));
    expect(preferencesApi.setUiTheme).not.toHaveBeenCalled();
  });

  it("ignores unknown stored values and falls back to the factory default", () => {
    window.localStorage.setItem("sd-theme", "system");
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    expect(ctx?.theme).toBe("steaan");
  });
});
