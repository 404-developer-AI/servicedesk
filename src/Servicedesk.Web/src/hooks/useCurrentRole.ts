import { useAuth } from "@/auth/authStore";
import type { Role } from "@/lib/roles";

/// Returns the role of the authenticated user, or `Customer` when no session
/// is active. Routes and navigation are role-gated on top of this value; the
/// server-side authorization policies remain the actual source of truth.
export function useCurrentRole(): Role {
  const { user } = useAuth();
  return user?.role ?? "Customer";
}
