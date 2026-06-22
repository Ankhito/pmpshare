export function jsonResponse(body: unknown, init: ResponseInit = {}): Response {
  const headers = new Headers(init.headers);
  headers.set("content-type", "application/json; charset=utf-8");
  return new Response(JSON.stringify(body, null, 2), { ...init, headers });
}

export function errorResponse(
  status: number,
  message: string,
  code = "error",
): Response {
  return jsonResponse(
    {
      error: {
        code,
        message,
      },
    },
    { status },
  );
}
