import { useEffect, useState } from "react";

/// `value`, but only after it stopped changing for `delayMs` — for search
/// inputs that should not fire a request per keystroke. `delayMs <= 0`
/// passes the value straight through (debounce switched off by a setting).
export function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    if (delayMs <= 0) return;
    const id = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(id);
  }, [value, delayMs]);
  return delayMs <= 0 ? value : debounced;
}
