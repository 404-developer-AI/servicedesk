import * as React from "react";

/// Title (and optional back target) of the mobile header. Pages set it for
/// as long as they are mounted; MobileShell renders it.
export type HeaderState = { title: string; back?: string | null };

export const HeaderContext = React.createContext<(state: HeaderState) => void>(() => {});

export function useMobileHeader(title: string, back?: string | null) {
  const set = React.useContext(HeaderContext);
  React.useEffect(() => {
    set({ title, back });
  }, [set, title, back]);
}
