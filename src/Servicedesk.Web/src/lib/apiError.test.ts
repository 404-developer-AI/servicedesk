import { describe, expect, it } from "vitest";
import { ApiError as CoreApiError, apiErrorMessage } from "@/lib/api";
import { ApiError as TicketApiError } from "@/lib/ticket-api";

describe("ApiError", () => {
  it("is one class across lib/api and lib/ticket-api", () => {
    expect(TicketApiError).toBe(CoreApiError);
  });

  it("surfaces the server message for errors thrown by ticket-api calls", () => {
    const err = new TicketApiError(422, "/api/contacts", "422", { message: "Email already in use" });
    expect(apiErrorMessage(err)).toBe("Email already in use");
  });
});
