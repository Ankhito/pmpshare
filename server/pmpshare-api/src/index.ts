import { cleanupExpiredTransfers } from "./cleanup";
import { errorResponse } from "./responses";
import { route } from "./router";
import type { Env } from "./types";

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      return await route(request, env);
    } catch (error) {
      console.error(error);
      return errorResponse(500, "Unexpected server error.", "internal_error");
    }
  },

  async scheduled(
    _event: ScheduledEvent,
    env: Env,
    ctx: ExecutionContext,
  ): Promise<void> {
    ctx.waitUntil(cleanupExpiredTransfers(env));
  },
};
