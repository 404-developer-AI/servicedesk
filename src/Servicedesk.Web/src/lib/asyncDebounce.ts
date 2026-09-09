/// v0.1.4 — latest-wins debounce for async lookups driven by typing.
///
/// Tiptap's suggestion plugin calls `items()` on every keystroke while a
/// picker is open; without this, typing `::templ` fired six identical
/// template fetches in under a second and helped exhaust the per-session
/// rate-limit budget (diagnosed from the audit log, Sept 2026).
///
/// Semantics: every call restarts the timer; when it fires, `fn` runs once
/// with the *latest* argument and every caller that queued in the meantime
/// receives that same result. Callers therefore never get a stale answer
/// for an older query, and the picker never flickers to "no results".
export function debounceAsync<TArg, TResult>(
  fn: (arg: TArg) => Promise<TResult>,
  waitMs: number,
): (arg: TArg) => Promise<TResult> {
  let timer: ReturnType<typeof setTimeout> | null = null;
  let latestArg: TArg;
  let pending: Array<{
    resolve: (value: TResult) => void;
    reject: (reason: unknown) => void;
  }> = [];

  return (arg: TArg) =>
    new Promise<TResult>((resolve, reject) => {
      latestArg = arg;
      pending.push({ resolve, reject });
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        timer = null;
        const waiters = pending;
        pending = [];
        fn(latestArg).then(
          (value) => waiters.forEach((w) => w.resolve(value)),
          (err: unknown) => waiters.forEach((w) => w.reject(err)),
        );
      }, waitMs);
    });
}
