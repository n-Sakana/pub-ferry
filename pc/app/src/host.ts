// The page's side of the bridge to the C# host.
//
// Two channels, matching the host: web messages for decisions, and fetch()
// against the service origin for bytes. Nothing here holds a path — the host
// answers with tokens and labels, and the page never learns where anything is
// except to print it.

// The host answers these itself, on the page's own origin — see
// WebViewSecurity.ServicePath. Same-origin so nothing here is a cross-origin
// request, which is what a second virtual host would have made it.
const SERVICE = "/__host";

interface Pending {
  resolve(value: unknown): void;
  reject(error: Error): void;
}

export class HostError extends Error {
  constructor(
    message: string,
    readonly code: string,
  ) {
    super(message);
    this.name = "HostError";
  }
}

type EventHandler = (data: Record<string, unknown>) => void;

const pending = new Map<number, Pending>();
const handlers = new Map<string, Set<EventHandler>>();
let nextId = 1;

interface Bridge {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
}

function bridge(): Bridge | null {
  const chrome = (window as unknown as { chrome?: { webview?: Bridge } }).chrome;
  return chrome?.webview ?? null;
}

export const isHosted = (): boolean => bridge() !== null;

bridge()?.addEventListener("message", (event: MessageEvent) => {
  const message = event.data as Record<string, unknown>;
  if (typeof message?.event === "string") {
    for (const handler of handlers.get(message.event) ?? []) {
      handler((message.data ?? {}) as Record<string, unknown>);
    }
    return;
  }
  const id = Number(message?.id);
  const waiting = pending.get(id);
  if (!waiting) return;
  pending.delete(id);
  if (message.ok === true) waiting.resolve(message.data);
  else {
    waiting.reject(
      new HostError(
        String(message.message ?? "処理できませんでした。"),
        String(message.error ?? "error"),
      ),
    );
  }
});

export function onHostEvent(name: string, handler: EventHandler): void {
  let set = handlers.get(name);
  if (!set) {
    set = new Set();
    handlers.set(name, set);
  }
  set.add(handler);
}

export function call<T>(action: string, params: Record<string, unknown> = {}): Promise<T> {
  const channel = bridge();
  if (!channel) {
    return Promise.reject(
      new HostError(
        "この画面はデスクトップアプリの中で動かす必要があります。decimen.bat から起動してください。",
        "not-hosted",
      ),
    );
  }
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject });
    channel.postMessage({ id, action, params });
  });
}

/** The bytes of a staged pick. One request, one buffer — a folder as a base64
 *  string in a web message would be built, copied and parsed several times. */
export async function readPickedBytes(token: string): Promise<Uint8Array> {
  const response = await fetch(`${SERVICE}/pick?token=${encodeURIComponent(token)}`);
  if (!response.ok) throw await hostError(response);
  return new Uint8Array(await response.arrayBuffer());
}

export interface SavedResult {
  savedAs: string;
  fileCount: number;
  totalSize: number;
  destinationLabel: string;
}

/** Hand verified files to the host to write. The host validates every path
 *  again before it creates anything — this side is not trusted to have. */
export async function saveVerified(
  label: string,
  files: { path: string; bytes: Uint8Array }[],
): Promise<SavedResult> {
  const header = new TextEncoder().encode(
    JSON.stringify({ label, files: files.map((file) => ({ path: file.path, size: file.bytes.length })) }),
  );
  let total = 0;
  for (const file of files) total += file.bytes.length;
  const block = new Uint8Array(8 + header.length + total);
  block.set([0x44, 0x43, 0x57, 0x31], 0); // "DCW1"
  new DataView(block.buffer).setUint32(4, header.length, true);
  block.set(header, 8);
  let offset = 8 + header.length;
  for (const file of files) {
    block.set(file.bytes, offset);
    offset += file.bytes.length;
  }
  const response = await fetch(`${SERVICE}/save`, { method: "POST", body: block });
  if (!response.ok) throw await hostError(response);
  return (await response.json()) as SavedResult;
}

async function hostError(response: Response): Promise<HostError> {
  try {
    const body = (await response.json()) as { error?: string; message?: string };
    return new HostError(body.message ?? "処理できませんでした。", body.error ?? "error");
  } catch {
    return new HostError(`処理できませんでした（${response.status}）。`, "error");
  }
}
