import { requireTesterKey } from "./auth";
import {
  completeTransfer,
  createTransfer,
  deleteTransfer,
  downloadBlob,
  getMetadata,
  health,
  uploadBlob,
} from "./handlers";
import { errorResponse } from "./responses";
import type { Env } from "./types";

export async function route(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, "") || "/";

  if (request.method === "GET" && path === "/health") {
    return health();
  }

  if (request.method === "POST" && path === "/v1/transfers") {
    const authError = requireTesterKey(request, env);
    if (authError) return authError;
    return createTransfer(request, env);
  }

  const match = path.match(
    /^\/v1\/transfers\/([^/]+)(?:\/(blob|metadata|complete))?$/,
  );
  if (!match) {
    return errorResponse(404, "Endpoint was not found.", "not_found");
  }

  const [, transferId, action] = match;

  if (request.method === "PUT" && action === "blob") {
    const authError = requireTesterKey(request, env);
    if (authError) return authError;
    return uploadBlob(request, env, transferId);
  }

  if (request.method === "GET" && action === "metadata") {
    return getMetadata(env, transferId);
  }

  if (request.method === "GET" && action === "blob") {
    return downloadBlob(env, transferId);
  }

  if (request.method === "POST" && action === "complete") {
    const authError = requireTesterKey(request, env);
    if (authError) return authError;
    return completeTransfer(env, transferId);
  }

  if (request.method === "DELETE" && !action) {
    const authError = requireTesterKey(request, env);
    if (authError) return authError;
    return deleteTransfer(env, transferId);
  }

  return errorResponse(
    405,
    "Method is not allowed for this endpoint.",
    "method_not_allowed",
  );
}
