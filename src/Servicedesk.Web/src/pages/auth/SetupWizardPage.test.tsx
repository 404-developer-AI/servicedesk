import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";

const authApi = vi.hoisted(() => ({
  createAdmin: vi.fn(),
  me: vi.fn(),
  login: vi.fn(),
  verifyTwoFactor: vi.fn(),
  setupStatus: vi.fn(),
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

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), message: vi.fn(), error: vi.fn() },
}));

import { SetupWizardPage } from "./SetupWizardPage";
import { authStore } from "@/auth/authStore";

describe("SetupWizardPage", () => {
  beforeEach(() => {
    authApi.createAdmin.mockReset();
    authApi.me.mockReset();
    navigate.mockReset();
    authStore.set({ status: "ready", user: null, setupAvailable: true });
  });

  it("walks from welcome to credentials to the done screen", async () => {
    authApi.createAdmin.mockResolvedValue({ email: "admin@example.com", role: "Admin" });
    authApi.me.mockResolvedValue({
      user: { id: "u1", email: "admin@example.com", role: "Admin", amr: "pwd", twoFactorEnabled: false },
      serverTimeUtc: new Date().toISOString(),
    });

    render(<SetupWizardPage />);

    expect(screen.getByText(/let's get you set up/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /continue/i }));

    fireEvent.change(screen.getByPlaceholderText("you@example.com"), {
      target: { value: "admin@example.com" },
    });
    const passwords = screen.getAllByDisplayValue("");
    // two password fields + one email (email already filled) — use role-based lookups
    const pwInputs = document.querySelectorAll<HTMLInputElement>("input[type=password]");
    fireEvent.change(pwInputs[0], { target: { value: "correct-horse-battery-staple" } });
    fireEvent.change(pwInputs[1], { target: { value: "correct-horse-battery-staple" } });
    expect(passwords.length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole("button", { name: /create admin/i }));

    await waitFor(() => {
      expect(authApi.createAdmin).toHaveBeenCalledWith(
        "admin@example.com",
        "correct-horse-battery-staple",
      );
    });
    await waitFor(() => {
      expect(screen.getByText(/admin account created/i)).toBeInTheDocument();
    });
  });
});
