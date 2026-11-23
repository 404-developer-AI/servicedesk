import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiErrorMessage, apiFieldErrors, errorMessage, request, requestResponse } from "@/lib/http";

type Call = { url: string; init: RequestInit };

function mockFetch(response: () => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
    calls.push({ url, init });
    return response();
  }));
  return calls;
}

function headersOf(call: Call): Record<string, string> {
  return call.init.headers as Record<string, string>;
}

describe("http", () => {
  beforeEach(() => {
    document.cookie = "XSRF-TOKEN=staff-token";
    document.cookie = "XSRF-TOKEN-PORTAL=portal-token";
  });
  afterEach(() => vi.unstubAllGlobals());

  it("sends JSON with the staff CSRF header on unsafe verbs", async () => {
    const calls = mockFetch(() => new Response(JSON.stringify({ ok: 1 }), { status: 200 }));
    const out = await request<{ ok: number }>("post", "/api/tickets", { a: 1 });
    expect(out).toEqual({ ok: 1 });
    expect(calls[0].init.method).toBe("POST");
    expect(calls[0].init.body).toBe('{"a":1}');
    expect(headersOf(calls[0])["Content-Type"]).toBe("application/json");
    expect(headersOf(calls[0])["X-XSRF-TOKEN"]).toBe("staff-token");
  });

  it("uses the portal cookie for portal-realm paths", async () => {
    const calls = mockFetch(() => new Response(null, { status: 204 }));
    await request("POST", "/api/portal/tickets/1/messages", { body: "x" });
    expect(headersOf(calls[0])["X-XSRF-TOKEN"]).toBe("portal-token");
  });

  it("sends no CSRF header on safe verbs", async () => {
    const calls = mockFetch(() => new Response("[]", { status: 200 }));
    await request("GET", "/api/queues");
    expect(headersOf(calls[0])["X-XSRF-TOKEN"]).toBeUndefined();
  });

  it("never lets caller headers drop the CSRF header", async () => {
    const calls = mockFetch(() => new Response(null, { status: 204 }));
    await request("DELETE", "/api/x", undefined, { headers: { "X-XSRF-TOKEN": "", "X-Extra": "1" } });
    expect(headersOf(calls[0])["X-XSRF-TOKEN"]).toBe("staff-token");
    expect(headersOf(calls[0])["X-Extra"]).toBe("1");
  });

  it("anonymous requests send no cookies and no CSRF header", async () => {
    const calls = mockFetch(() => new Response("{}", { status: 200 }));
    await request("POST", "/api/public/surveys/t/submit", { a: 1 }, { credentials: "omit" });
    expect(calls[0].init.credentials).toBe("omit");
    expect(headersOf(calls[0])["X-XSRF-TOKEN"]).toBeUndefined();
  });

  it("passes FormData untouched without a JSON content type", async () => {
    const calls = mockFetch(() => new Response("{}", { status: 200 }));
    const fd = new FormData();
    fd.append("file", new Blob(["x"]), "a.txt");
    await request("POST", "/api/upload", fd);
    expect(calls[0].init.body).toBe(fd);
    expect(headersOf(calls[0])["Content-Type"]).toBeUndefined();
  });

  it("returns undefined for 204 and empty bodies", async () => {
    mockFetch(() => new Response(null, { status: 204 }));
    expect(await request("PUT", "/api/x", {})).toBeUndefined();
    mockFetch(() => new Response("", { status: 200 }));
    expect(await request("GET", "/api/x")).toBeUndefined();
  });

  it("throws a readable ApiError for a non-JSON 2xx body instead of a SyntaxError", async () => {
    mockFetch(() => new Response("<html>proxy page</html>", { status: 200 }));
    await expect(request("GET", "/api/x")).rejects.toMatchObject({
      name: "ApiError",
      status: 200,
      message: expect.stringContaining("unexpected response"),
    });
  });

  it("throws ApiError with the parsed body; helpers read message and field errors", async () => {
    mockFetch(() => new Response(
      JSON.stringify({ error: "invalid", message: "Some fields are invalid.", errors: [{ field: "start", message: "Required" }] }),
      { status: 400, statusText: "Bad Request" },
    ));
    const err = await request("POST", "/api/timesheet/entries", {}).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(400);
    expect(apiErrorMessage(err)).toBe("Some fields are invalid.");
    expect(apiFieldErrors(err)).toEqual({ start: "Required" });
  });

  it("reads ProblemDetails detail/title as the message", () => {
    expect(apiErrorMessage(new ApiError(409, "/u", "x", { title: "Conflict", detail: "Cancel is disabled." }))).toBe("Cancel is disabled.");
    expect(apiErrorMessage(new ApiError(500, "/u", "x", { title: "An error occurred" }))).toBe("An error occurred");
  });

  it("tolerates a non-JSON error body", async () => {
    mockFetch(() => new Response("<html>oops</html>", { status: 502 }));
    const err = await requestResponse("GET", "/api/x").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).body).toBeNull();
    expect(apiErrorMessage(err)).toBeNull();
    expect(apiFieldErrors(err)).toBeNull();
  });
  it("errorMessage prefers the server message, then a client Error, then the fallback", () => {
    expect(errorMessage(new ApiError(409, "/u", "x", { message: "Locked" }), "fb")).toBe("Locked");
    expect(errorMessage(new ApiError(500, "/u", "x"), "fb")).toBe("fb");
    expect(errorMessage(new Error("Day start must be HH:MM"), "fb")).toBe("Day start must be HH:MM");
    expect(errorMessage("weird", "fb")).toBe("fb");
  });
});
