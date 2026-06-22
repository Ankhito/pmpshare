import type { Env } from "./types";
import { errorResponse } from "./responses";

export function requireTesterKey(request: Request, env: Env): Response | null {
  const expectedKey = env.PMPSHARE_TESTER_KEY;
  const providedKey = request.headers.get("X-PmpShare-Key");

  if (!expectedKey) {
    return errorResponse(
      500,
      "Tester key secret is not configured.",
      "server_misconfigured",
    );
  }

  if (!providedKey || providedKey !== expectedKey) {
    return errorResponse(401, "Missing or invalid tester key.", "unauthorized");
  }

  return null;
}
