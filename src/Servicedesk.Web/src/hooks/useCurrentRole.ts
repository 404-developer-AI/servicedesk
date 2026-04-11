import { useDevRole } from "@/stores/useDevRoleStore";
import type { Role } from "@/lib/roles";

// Until real auth lands in v0.0.4, the current role comes from the dev
// role switcher in the header (localStorage-backed). In a production build
// the switcher is not mounted, but the store still returns `Admin` so the
// shell remains usable by whoever is running the build.
//
// TODO(v0.0.4): replace with a role pulled from the authenticated session.
export function useCurrentRole(): Role {
  return useDevRole();
}
