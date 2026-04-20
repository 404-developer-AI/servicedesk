import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

const authApi = vi.hoisted(() => ({
  login: vi.fn(),
  verifyTwoFactor: vi.fn(),
  setupStatus: vi.fn(),
  config: vi.fn(),
  me: vi.fn(),
  createAdmin: vi.fn(),
  logout: vi.fn(),
  beginTotpEnroll: vi.fn(),
  confirmTotpEnroll: vi.fn(),
  disableTotp: vi.fn(),
}));

vi.mock("@/lib/api", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api")>("@/lib/api");
  return { ...actual, authApi };
});

const navigate = vi.fn();
vi.mock("@tanstack/react-router", () => ({
  useNavigate: () => navigate,
}));

const toastApi = vi.hoisted(() => ({
  message: vi.fn(),
  success: vi.fn(),
  error: vi.fn(),
}));
vi.mock("sonner", () => ({
  toast: toastApi,
}));

import { LoginPage } from "./LoginPage";
import { authStore } from "@/auth/authStore";

function renderLogin(ui: ReactNode = <LoginPage />) {
  // Fresh QueryClient per test so one test's cached config doesn't
  // leak into the next. LoginPage calls useQuery(["auth","config"]).
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

describe("LoginPage", () => {
  beforeEach(() => {
    authApi.login.mockReset();
    authApi.verifyTwoFactor.mockReset();
    authApi.me.mockReset();
    authApi.config.mockReset();
    navigate.mockReset();
    toastApi.message.mockReset();
    toastApi.success.mockReset();
    authStore.set({ status: "ready", user: null, setupAvailable: false });
  });

  it("renders the credentials form and hides the Microsoft button when M365 is off", async () => {
    authApi.config.mockResolvedValue({ microsoftEnabled: false, setupAvailable: false });
    renderLogin();

    expect(screen.getByPlaceholderText("you@example.com")).toBeInTheDocument();

    // Give the config-query one tick to resolve; the button must stay hidden.
    await waitFor(() => {
      expect(authApi.config).toHaveBeenCalled();
    });
    expect(screen.queryByTestId("m365-signin")).not.toBeInTheDocument();
  });

  it("shows the Microsoft button when M365 is enabled", async () => {
    authApi.config.mockResolvedValue({ microsoftEnabled: true, setupAvailable: false });
    renderLogin();

    await waitFor(() => {
      expect(screen.getByTestId("m365-signin")).toBeInTheDocument();
    });
    // The button is a top-level redirect trigger — no local auth API call
    // should fire as a side-effect of rendering it.
    expect(authApi.login).not.toHaveBeenCalled();
  });

  it("calls the login API and navigates home on success without 2FA", async () => {
    authApi.config.mockResolvedValue({ microsoftEnabled: false, setupAvailable: false });
    authApi.login.mockResolvedValue({
      email: "admin@example.com",
      role: "Admin",
      twoFactorRequired: false,
    });
    authApi.me.mockResolvedValue({
      user: { id: "u1", email: "admin@example.com", role: "Admin", amr: "pwd", twoFactorEnabled: false },
      serverTimeUtc: new Date().toISOString(),
    });

    renderLogin();

    fireEvent.change(screen.getByPlaceholderText("you@example.com"), {
      target: { value: "admin@example.com" },
    });
    fireEvent.change(screen.getByPlaceholderText("••••••••••••"), {
      target: { value: "hunter22hunter22" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^sign in$/i }));

    await waitFor(() => {
      expect(authApi.login).toHaveBeenCalledWith("admin@example.com", "hunter22hunter22");
    });
    await waitFor(() => {
      expect(navigate).toHaveBeenCalledWith({ to: "/" });
    });
  });
});
