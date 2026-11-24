import { expect, type APIRequestContext } from "@playwright/test";

/**
 * JSON calls against the app's API the way the SPA makes them: the staff
 * CSRF cookie (XSRF-TOKEN) mirrored into X-XSRF-TOKEN on unsafe verbs.
 * Used to seed data a spec does not exercise itself.
 */
export async function api<T>(
  ctx: APIRequestContext,
  method: "GET" | "POST" | "PATCH" | "PUT" | "DELETE",
  url: string,
  body?: unknown,
): Promise<T> {
  const headers: Record<string, string> = {};
  if (method !== "GET") {
    const cookies = (await ctx.storageState()).cookies;
    const csrf = cookies.find((c) => c.name === "XSRF-TOKEN")?.value;
    if (csrf) headers["X-XSRF-TOKEN"] = decodeURIComponent(csrf);
  }
  const res = await ctx.fetch(url, { method, data: body, headers });
  expect(res.ok(), `${method} ${url} → ${res.status()} ${await res.text()}`).toBeTruthy();
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}
