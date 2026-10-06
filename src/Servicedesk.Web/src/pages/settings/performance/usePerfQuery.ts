import { keepPreviousData, useQuery } from "@tanstack/react-query";

export const PERF_KEY = ["admin", "performance"] as const;
export const STATUS_KEY = [...PERF_KEY, "status"] as const;

/** Shared query options for the Performance tabs: keep showing the previous
 *  period while the next one loads, refresh while auto-refresh is on. */
export function useTabQuery<T>(key: readonly unknown[], fn: () => Promise<T>, refetchMs: number | false = false) {
  return useQuery({
    queryKey: [...PERF_KEY, ...key],
    queryFn: fn,
    placeholderData: keepPreviousData,
    refetchInterval: refetchMs,
    staleTime: 15_000,
    retry: false,
  });
}
