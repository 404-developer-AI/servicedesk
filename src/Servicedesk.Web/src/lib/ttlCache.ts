/// v0.1.4 — tiny in-memory TTL memoiser for read-only lookups a picker
/// re-requests on every open (compose templates, intake templates).
///
/// A hit within `ttlMs` returns the cached promise; concurrent callers share
/// one in-flight request; a rejected promise is evicted so the next call
/// retries. The cache lives for the page session — admins editing the
/// underlying lists see their change within one TTL, which is the accepted
/// trade-off for not refetching a static list on every keystroke.
export function memoizeTtl<TArgs extends unknown[], TResult>(
  fn: (...args: TArgs) => Promise<TResult>,
  ttlMs: number,
  keyOf: (...args: TArgs) => string = (...args) => JSON.stringify(args),
): ((...args: TArgs) => Promise<TResult>) & { invalidate: () => void } {
  const entries = new Map<string, { at: number; value: Promise<TResult> }>();

  const wrapped = (...args: TArgs): Promise<TResult> => {
    const key = keyOf(...args);
    const now = Date.now();
    const hit = entries.get(key);
    if (hit && now - hit.at < ttlMs) return hit.value;

    const value = fn(...args);
    entries.set(key, { at: now, value });
    value.catch(() => {
      if (entries.get(key)?.value === value) entries.delete(key);
    });
    return value;
  };

  return Object.assign(wrapped, { invalidate: () => entries.clear() });
}
