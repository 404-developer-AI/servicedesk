import { serverTodayIso } from "@/lib/dateFormat";
import { serverNowMs } from "@/hooks/useServerTime";

/** The server's calendar date as "yyyy-MM-dd" in the server display zone (one-off read, does not tick). */
export function serverTodayNow(): string {
  return serverTodayIso(undefined, serverNowMs()) ?? "";
}
