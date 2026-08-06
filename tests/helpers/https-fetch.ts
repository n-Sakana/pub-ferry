// A `fetch` over node:https that accepts the throwaway certificate the tests
// generate, and nothing else about the request is special.
//
// Node's global fetch has no way to relax certificate checking for one call,
// and turning it off process-wide (NODE_TLS_REJECT_UNAUTHORIZED=0) would relax
// it for everything including the code under test. This does exactly one thing
// instead, and keeps the tests dependency-free.

import { request } from "node:https";

export interface FetchLikeResponse {
  status: number;
  ok: boolean;
  headers: Map<string, string>;
  json(): Promise<unknown>;
  text(): Promise<string>;
  arrayBuffer(): Promise<ArrayBuffer>;
}

export function insecureFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const target = new URL(url);
  const headers: Record<string, string> = {};
  for (const [name, value] of Object.entries((init.headers ?? {}) as Record<string, string>)) {
    headers[name] = value;
  }
  const body =
    init.body === undefined || init.body === null
      ? undefined
      : typeof init.body === "string"
        ? Buffer.from(init.body, "utf8")
        : Buffer.from(init.body as unknown as Uint8Array);
  if (body && headers["content-length"] === undefined) {
    headers["content-length"] = String(body.length);
  }

  return new Promise((resolveResponse, rejectResponse) => {
    const outgoing = request(
      {
        protocol: target.protocol,
        hostname: target.hostname,
        port: target.port,
        path: `${target.pathname}${target.search}`,
        method: init.method ?? "GET",
        headers,
        rejectUnauthorized: false,
      },
      (incoming) => {
        const chunks: Buffer[] = [];
        incoming.on("data", (chunk: Buffer) => chunks.push(chunk));
        incoming.on("end", () => {
          const payload = Buffer.concat(chunks);
          const status = incoming.statusCode ?? 0;
          const response: FetchLikeResponse = {
            status,
            ok: status >= 200 && status < 300,
            headers: new Map(
              Object.entries(incoming.headers).map(([name, value]) => [name, String(value)]),
            ),
            json: async () => JSON.parse(payload.toString("utf8")),
            text: async () => payload.toString("utf8"),
            arrayBuffer: async () =>
              payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.byteLength),
          };
          resolveResponse(response as unknown as Response);
        });
      },
    );
    outgoing.on("error", rejectResponse);
    if (body) outgoing.write(body);
    outgoing.end();
  });
}
