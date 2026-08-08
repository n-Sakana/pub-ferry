// Configuration, and the rule that a misconfigured host does not start.
//
// The temptation with a tool like this is to fall back: no certificate, serve
// plain HTTP; no Tailscale address, bind 0.0.0.0; no destination folder, use
// the home directory. Every one of those turns a setup mistake into a security
// property nobody chose. So there are no fallbacks here. Something missing is
// an error with the command that fixes it in the message.

import { mkdirSync, readFileSync, writeFileSync, chmodSync } from "node:fs";
import { dirname, isAbsolute, resolve } from "node:path";
import { hostname, platform } from "node:os";
import {
  RELAY_MAX_HOST_LABEL,
  RELAY_MAX_ROUTE_LABEL,
  isRelayId,
  sanitizeLabel,
} from "../shared/relay-contract";
import { DEFAULT_MAX_BUNDLE_BYTES, HARD_MAX_BUNDLE_BYTES } from "../shared/bundle";
import { certDir, configPath } from "./paths";

export const CONFIG_VERSION = 1;
export const DEFAULT_PORT = 8842;

export interface RouteConfig {
  id: string;
  label: string;
  /** Absolute path to the folder this route writes into. */
  path: string;
}

export interface RelayConfig {
  v: number;
  hostLabel: string;
  port: number;
  /**
   * "tailscale" — bind only to the addresses tailscaled reports for this node.
   * An explicit list is allowed but is a deliberate act: it is echoed at
   * startup and shown by `status` as a warning, because binding anywhere else
   * exposes this to whatever else can reach that address.
   */
  bind: "tailscale" | string[];
  tls: { certFile: string; keyFile: string };
  routes: RouteConfig[];
  maxBundleBytes: number;
  /**
   * Origins other than this host's own that may call its API from a browser.
   *
   * Empty by default, which means same-origin only: the phone loads its page
   * FROM the host it is talking to, so nothing cross-origin is needed and no
   * CORS header is ever sent. Filling this in is how one phone page reaches
   * several hosts, and every entry is an origin the operator typed.
   */
  allowedAppOrigins: string[];
}

export class ConfigError extends Error {
  constructor(
    message: string,
    readonly fix?: string,
  ) {
    super(message);
    this.name = "ConfigError";
  }
}

export function defaultConfig(): RelayConfig {
  return {
    v: CONFIG_VERSION,
    hostLabel: sanitizeLabel(hostname(), RELAY_MAX_HOST_LABEL) || "pub-ferry-host",
    port: DEFAULT_PORT,
    bind: "tailscale",
    tls: {
      certFile: resolve(certDir(), "server.crt"),
      keyFile: resolve(certDir(), "server.key"),
    },
    routes: [],
    maxBundleBytes: DEFAULT_MAX_BUNDLE_BYTES,
    allowedAppOrigins: [],
  };
}

export function loadConfig(): RelayConfig {
  const file = configPath();
  let raw: string;
  try {
    raw = readFileSync(file, "utf8");
  } catch {
    throw new ConfigError(
      `設定ファイルがありません: ${file}`,
      "npm run relay -- init   を実行して作成してください。",
    );
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    throw new ConfigError(`設定ファイルを読み取れません: ${file}`, "JSON として不正です。");
  }
  return validateConfig(parsed);
}

export function validateConfig(input: unknown): RelayConfig {
  if (input === null || typeof input !== "object") {
    throw new ConfigError("設定の形式が不正です。");
  }
  const value = input as Partial<RelayConfig>;
  if (value.v !== CONFIG_VERSION) {
    throw new ConfigError(
      `設定の版が違います（${String(value.v)}、想定 ${CONFIG_VERSION}）。`,
      "設定ファイルを作り直してください。",
    );
  }

  const hostLabel = sanitizeLabel(value.hostLabel, RELAY_MAX_HOST_LABEL);
  if (hostLabel.length === 0) {
    throw new ConfigError("hostLabel が空です。", "この受信ホストの呼び名を設定してください。");
  }
  if (
    typeof value.port !== "number" ||
    !Number.isInteger(value.port) ||
    value.port < 1024 ||
    value.port > 65535
  ) {
    throw new ConfigError("port は 1024〜65535 の整数で指定してください。");
  }
  if (value.bind !== "tailscale") {
    if (!Array.isArray(value.bind) || value.bind.length === 0) {
      throw new ConfigError(
        'bind は "tailscale" か、明示的なアドレスの配列で指定してください。',
        'Tailscale の中だけで待ち受けるなら "tailscale" のままにしてください。',
      );
    }
    for (const address of value.bind) {
      if (typeof address !== "string" || address.trim().length === 0) {
        throw new ConfigError("bind のアドレスが不正です。");
      }
    }
  }
  if (
    typeof value.tls !== "object" ||
    value.tls === null ||
    typeof value.tls.certFile !== "string" ||
    typeof value.tls.keyFile !== "string" ||
    !isAbsolute(value.tls.certFile) ||
    !isAbsolute(value.tls.keyFile)
  ) {
    throw new ConfigError(
      "tls.certFile と tls.keyFile を絶対パスで指定してください。",
      tlsHint(),
    );
  }
  if (
    typeof value.maxBundleBytes !== "number" ||
    !Number.isSafeInteger(value.maxBundleBytes) ||
    value.maxBundleBytes < 1024 ||
    value.maxBundleBytes > HARD_MAX_BUNDLE_BYTES
  ) {
    throw new ConfigError(
      `maxBundleBytes は 1024〜${HARD_MAX_BUNDLE_BYTES} の範囲で指定してください。`,
    );
  }

  const routes: RouteConfig[] = [];
  const seenIds = new Set<string>();
  for (const route of Array.isArray(value.routes) ? value.routes : []) {
    if (!isRelayId(route?.id)) {
      throw new ConfigError("受信先の id は英数字・ハイフン・下線のみで指定してください。");
    }
    if (seenIds.has(route.id)) {
      throw new ConfigError(`受信先の id が重複しています: ${route.id}`);
    }
    seenIds.add(route.id);
    const label = sanitizeLabel(route.label, RELAY_MAX_ROUTE_LABEL);
    if (label.length === 0) {
      throw new ConfigError(`受信先 ${route.id} の呼び名が空です。`);
    }
    if (typeof route.path !== "string" || !isAbsolute(route.path)) {
      throw new ConfigError(
        `受信先 ${route.id} の保存先は絶対パスで指定してください。`,
      );
    }
    routes.push({ id: route.id, label, path: resolve(route.path) });
  }

  const allowedAppOrigins: string[] = [];
  for (const origin of Array.isArray(value.allowedAppOrigins) ? value.allowedAppOrigins : []) {
    if (typeof origin !== "string") {
      throw new ConfigError("allowedAppOrigins は文字列の配列です。");
    }
    let parsedOrigin: URL;
    try {
      parsedOrigin = new URL(origin);
    } catch {
      throw new ConfigError(`allowedAppOrigins の値が URL ではありません: ${origin}`);
    }
    if (parsedOrigin.protocol !== "https:") {
      throw new ConfigError(
        `allowedAppOrigins は https のみです: ${origin}`,
        "ブラウザは https のページから http を呼べません。",
      );
    }
    if (parsedOrigin.origin !== origin.replace(/\/$/, "")) {
      throw new ConfigError(
        `allowedAppOrigins にはパスを含めないでください: ${origin}`,
        `例: ${parsedOrigin.origin}`,
      );
    }
    allowedAppOrigins.push(parsedOrigin.origin);
  }

  return {
    v: CONFIG_VERSION,
    hostLabel,
    port: value.port,
    bind: value.bind === "tailscale" ? "tailscale" : [...(value.bind as string[])],
    tls: { certFile: resolve(value.tls.certFile), keyFile: resolve(value.tls.keyFile) },
    routes,
    maxBundleBytes: value.maxBundleBytes,
    allowedAppOrigins,
  };
}

export function saveConfig(config: RelayConfig): void {
  const file = configPath();
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, `${JSON.stringify(config, null, 2)}\n`, "utf8");
  if (platform() !== "win32") {
    try {
      chmodSync(file, 0o600);
    } catch {
      // A config that cannot be tightened is still a config; the state file is
      // the one holding secrets and it refuses to be written loose.
    }
  }
}

export function tlsHint(): string {
  return platform() === "win32"
    ? "本番: tailscale cert <ホスト名>.<tailnet>.ts.net で証明書を作り、そのパスを設定してください。\n" +
        "試験用: npm run relay -- dev-cert で自己署名の証明書を作れます。"
    : "本番: sudo tailscale cert <ホスト名>.<tailnet>.ts.net で証明書を作り、そのパスを設定してください。\n" +
        "試験用: npm run relay -- dev-cert で自己署名の証明書を作れます。";
}
