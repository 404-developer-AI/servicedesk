import * as React from "react";

/// v0.1.18 — the active Columns-mode search tokens (folded), provided by the
/// ticket list so text cells can mark what matched without threading props
/// through TanStack column defs. Empty = no highlighting.
export const SearchHighlightContext = React.createContext<readonly string[]>([]);
