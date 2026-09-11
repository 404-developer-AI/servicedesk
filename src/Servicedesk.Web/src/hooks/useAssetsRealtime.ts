import * as React from "react";
import { useQueryClient } from "@tanstack/react-query";
import { getConnection } from "./usePresence";

/// v0.1.10 — live refresh for the Assets page. The TRMM sync worker pushes
/// `AssetsChanged` on the presence hub after every agent mirror run, every
/// Remote Desktop check sync and every client-note mutation (the payload
/// carries a `kind` discriminator). Mount once in <c>AssetsPage</c>; both
/// tabs share the `["assets"]` query prefix so one invalidation refreshes
/// whichever tab is open.
export function useAssetsRealtime() {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    const hub = getConnection();
    const onChanged = () => {
      queryClient.invalidateQueries({ queryKey: ["assets"] });
    };
    hub.on("AssetsChanged", onChanged);
    return () => {
      hub.off("AssetsChanged", onChanged);
    };
  }, [queryClient]);
}
