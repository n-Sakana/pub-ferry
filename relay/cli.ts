// The relay's command line. This is the whole interface on the VPS, and it is
// what the Windows app calls to put a transfer in the outbox.
//
//   init                       write a starting configuration
//   dev-cert                   make a self-signed certificate for trying it out
//   route add <name> <folder>  register a folder this host will receive into
//   route list                 what is registered, and whether it is writable
//   route remove <id>
//   outbox add <name> <path…>  stage a file or folder for the phone to collect
//   outbox list / remove <id>
//   device list / revoke <id>
//   pair                       print a registration code
//   serve                      run the receiver
//   status                     what is configured and what is wrong with it

import { mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { basename, join, relative, resolve } from "node:path";
import { hostname } from "node:os";
import { formatPairCode, PAIR_CODE_TTL_SECONDS } from "../shared/relay-auth";
import { packBundle, PERMISSIVE_BUNDLE_LIMITS, type BundleFile } from "../shared/bundle";
import { BUNDLE_MEDIA_TYPE } from "../shared/bundle";
import { packFile } from "../shared/protocol";
import { checkRelativePath, toBundlePath } from "../shared/relative-path";
import { sanitizeLabel, RELAY_MAX_ROUTE_LABEL } from "../shared/relay-contract";
import { ConfigError, defaultConfig, loadConfig, saveConfig, tlsHint, type RelayConfig } from "./config";
import { certDir, configPath, logDir, relayHome } from "./paths";
import { RelayStore } from "./store";
import { createRelayServer } from "./server";
import { TailscaleError, magicDnsName, resolveBind } from "./net";
import { ensureDirectory, isWritable } from "./writer";

const out = (line = "") => process.stdout.write(`${line}\n`);
const problem = (line: string) => process.stderr.write(`${line}\n`);

function slug(label: string): string {
  const base = label
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 40);
  return base.length > 0 ? base : `route-${Date.now().toString(36)}`;
}

/** Every ordinary file under `root`, as bundle entries. Links are counted and
 *  left out — a link is a pointer into a filesystem the receiver does not have,
 *  and following one is how a "folder" transfer walks off somewhere else. */
function collectFolder(root: string): { files: BundleFile[]; skipped: number } {
  const files: BundleFile[] = [];
  let skipped = 0;
  const walk = (directory: string): void => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const full = join(directory, entry.name);
      if (entry.isSymbolicLink()) {
        skipped++;
        continue;
      }
      if (entry.isDirectory()) {
        walk(full);
        continue;
      }
      if (!entry.isFile()) {
        skipped++;
        continue;
      }
      const path = toBundlePath(relative(root, full));
      const check = checkRelativePath(path);
      if (!check.ok) {
        problem(`  除外: ${entry.name} — ${check.message}`);
        skipped++;
        continue;
      }
      files.push({ path, bytes: new Uint8Array(readFileSync(full)) });
    }
  };
  walk(root);
  return { files, skipped };
}

function timestampedLogger() {
  const directory = logDir();
  mkdirSync(directory, { recursive: true });
  const file = join(directory, `relay-${new Date().toISOString().slice(0, 10)}.log`);
  return (level: "INFO" | "WARN" | "ERROR", message: string) => {
    const line = `[${new Date().toISOString()}] [${level}] ${message}\n`;
    process.stdout.write(line);
    try {
      writeFileSync(file, line, { flag: "a", encoding: "utf8" });
    } catch {
      // A log that cannot be written must not stop a transfer.
    }
  };
}

async function main(argv: string[]): Promise<number> {
  const [command, sub, ...rest] = argv;

  if (!command || command === "help" || command === "--help") {
    out("decimen relay — 光学転送の受信ホスト");
    out("");
    out("  init                            設定を作る");
    out("  dev-cert                        試験用の自己署名証明書を作る");
    out("  route add <呼び名> <フォルダー>   受け取り先を登録する");
    out("  route list | remove <id>");
    out("  outbox add <呼び名> <パス…>       スマホが取りに来る転送を用意する");
    out("  outbox list | remove <id>");
    out("  pair                            登録コードを表示する");
    out("  device list | revoke <id>");
    out("  serve                           受信を開始する");
    out("  status                          設定と状態を表示する");
    return 0;
  }

  if (command === "init") {
    const config = defaultConfig();
    mkdirSync(relayHome(), { recursive: true });
    mkdirSync(certDir(), { recursive: true });
    saveConfig(config);
    out(`設定を作りました: ${configPath()}`);
    out(`受信ホストの呼び名: ${config.hostLabel}（${hostname()} から）`);
    out("");
    out("次にすること:");
    out("  1. 証明書を用意する");
    out(`     ${tlsHint().split("\n").join("\n     ")}`);
    out("  2. 受け取り先を登録する");
    out('     npm run relay -- route add "受信箱" /path/to/folder');
    out("  3. 受信を開始する");
    out("     npm run relay -- serve");
    return 0;
  }

  if (command === "dev-cert") {
    // Only ever a convenience for trying the tool out. The real path is
    // `tailscale cert`, which gives a certificate a phone trusts without
    // anybody tapping through a warning.
    const { getCertificate } = (await import("@vitejs/plugin-basic-ssl")) as {
      getCertificate: (dir: string) => Promise<string>;
    };
    const directory = certDir();
    mkdirSync(directory, { recursive: true });
    const pem = await getCertificate(join(directory, "cache"));
    // getCertificate() returns ONE pem holding the key and the certificate.
    // Writing it to server.crt with a default mode publishes the private key
    // in the file whose whole job is to be public.
    writeFileSync(join(directory, "server.crt"), pem, { encoding: "utf8", mode: 0o600 });
    writeFileSync(join(directory, "server.key"), pem, { encoding: "utf8", mode: 0o600 });
    out(`自己署名の証明書を作りました: ${directory}`);
    out("スマホで初回に警告が出ます。試験用です。本番では tailscale cert を使ってください。");
    return 0;
  }

  let config: RelayConfig;
  try {
    config = loadConfig();
  } catch (error) {
    if (error instanceof ConfigError) {
      problem(error.message);
      if (error.fix) problem(error.fix);
      return 2;
    }
    throw error;
  }
  const store = new RelayStore();

  if (command === "route") {
    if (sub === "add") {
      const [label, folder] = rest;
      if (!label || !folder) {
        problem('使い方: route add "呼び名" <フォルダー>');
        return 2;
      }
      const cleanLabel = sanitizeLabel(label, RELAY_MAX_ROUTE_LABEL);
      if (cleanLabel.length === 0) {
        problem("呼び名が空です。");
        return 2;
      }
      const target = resolve(folder);
      ensureDirectory(target);
      if (!isWritable(target)) {
        problem(`そのフォルダーには書き込めません: ${target}`);
        return 2;
      }
      let id = slug(cleanLabel);
      let n = 2;
      while (config.routes.some((route) => route.id === id)) id = `${slug(cleanLabel)}-${n++}`;
      config.routes.push({ id, label: cleanLabel, path: target });
      saveConfig(config);
      out(`受け取り先を登録しました: ${cleanLabel}（${id}）`);
      out(`  保存先: ${target}`);
      return 0;
    }
    if (sub === "list") {
      if (config.routes.length === 0) {
        out("受け取り先はまだ登録されていません。");
        out('  npm run relay -- route add "受信箱" <フォルダー>');
        return 0;
      }
      for (const route of config.routes) {
        out(`${route.id}\t${route.label}\t${isWritable(route.path) ? "書き込めます" : "書き込めません"}\t${route.path}`);
      }
      return 0;
    }
    if (sub === "remove") {
      const [id] = rest;
      const before = config.routes.length;
      config.routes = config.routes.filter((route) => route.id !== id);
      if (config.routes.length === before) {
        problem(`そのような受け取り先はありません: ${String(id)}`);
        return 2;
      }
      saveConfig(config);
      out(`受け取り先を削除しました: ${id}`);
      return 0;
    }
    problem("route add | list | remove");
    return 2;
  }

  if (command === "outbox") {
    if (sub === "add") {
      const [label, ...targets] = rest;
      if (!label || targets.length === 0) {
        problem('使い方: outbox add "呼び名" <ファイルまたはフォルダー…>');
        return 2;
      }
      const files: BundleFile[] = [];
      let skipped = 0;
      for (const target of targets) {
        const full = resolve(target);
        const info = statSync(full);
        if (info.isDirectory()) {
          const collected = collectFolder(full);
          for (const file of collected.files) {
            files.push({ path: `${toBundlePath(basename(full))}/${file.path}`, bytes: file.bytes });
          }
          skipped += collected.skipped;
        } else {
          files.push({ path: toBundlePath(basename(full)), bytes: new Uint8Array(readFileSync(full)) });
        }
      }
      const bundle = await packBundle(sanitizeLabel(label, 64), files, {
        skipped,
        limits: { ...PERMISSIVE_BUNDLE_LIMITS, maxTotalBytes: config.maxBundleBytes },
      });
      // Staged already wrapped in the optical container, so the phone can put
      // it straight on screen without repacking — and so the bytes the phone
      // displays are the bytes this host hashed.
      const container = await packFile(`${sanitizeLabel(label, 64)}.dcb1`, BUNDLE_MEDIA_TYPE, bundle.bytes);
      const id = store.addToOutbox(sanitizeLabel(label, 64), container.container, files.length);
      out(`送信待ちに入れました: ${label}（${id}）`);
      out(`  ${files.length} 個のファイル / ${(bundle.bytes.length / 1024).toFixed(1)} KB`);
      if (skipped > 0) out(`  ${skipped} 個は対象外でした（リンクなど）`);
      return 0;
    }
    if (sub === "list") {
      const items = store.listOutbox();
      if (items.length === 0) {
        out("送信待ちはありません。");
        return 0;
      }
      for (const item of items) {
        out(`${item.id}\t${item.label}\t${item.fileCount} 個\t${(item.totalSize / 1024).toFixed(1)} KB`);
      }
      return 0;
    }
    if (sub === "remove") {
      const [id] = rest;
      if (!id || !store.removeFromOutbox(id)) {
        problem(`そのような送信待ちはありません: ${String(id)}`);
        return 2;
      }
      out(`削除しました: ${id}`);
      return 0;
    }
    problem("outbox add | list | remove");
    return 2;
  }

  if (command === "pair") {
    const { code, expiresAt } = store.startPairing();
    out("スマホの画面にこのコードを入力してください。");
    out("");
    out(`    ${formatPairCode(code)}`);
    out("");
    out(`有効期限: ${Math.round(PAIR_CODE_TTL_SECONDS / 60)} 分（${new Date(expiresAt * 1000).toLocaleTimeString()} まで）`);
    out("このコードは 1 回だけ使えます。間違いが 5 回続くと無効になります。");
    out("serve を別のターミナルで動かしたままで構いません。");
    return 0;
  }

  if (command === "device") {
    if (sub === "list") {
      const devices = store.listDevices();
      if (devices.length === 0) {
        out("登録された端末はありません。");
        return 0;
      }
      for (const device of devices) {
        out(`${device.id}\t${device.label}\t登録 ${new Date(device.pairedAt * 1000).toLocaleString()}`);
      }
      return 0;
    }
    if (sub === "revoke") {
      const [id] = rest;
      if (!id || !store.revokeDevice(id)) {
        problem(`そのような端末はありません: ${String(id)}`);
        return 2;
      }
      out(`端末の登録を取り消しました: ${id}`);
      return 0;
    }
    problem("device list | revoke");
    return 2;
  }

  if (command === "status") {
    out(`受信ホスト: ${config.hostLabel}`);
    out(`設定: ${configPath()}`);
    out(`ポート: ${config.port}`);
    try {
      const bind = resolveBind(config.bind);
      out(`待ち受け: ${bind.addresses.join(", ")}`);
      if (bind.explicit) {
        out("  警告: Tailscale 以外のアドレスが明示指定されています。");
      }
      const dns = magicDnsName();
      if (dns) out(`  スマホから開く URL: https://${dns}:${config.port}/`);
    } catch (error) {
      out(`待ち受け: 未確定 — ${error instanceof Error ? error.message : String(error)}`);
    }
    out(`受け取り先: ${config.routes.length} 件`);
    for (const route of config.routes) {
      out(`  ${route.label}（${route.id}）— ${isWritable(route.path) ? "書き込めます" : "書き込めません"}`);
    }
    out(`登録済みの端末: ${store.listDevices().length} 台`);
    out(`送信待ち: ${store.listOutbox().length} 件`);
    const pairing = store.pairingStatus();
    out(pairing.active ? `登録コード: 有効（あと ${pairing.expiresIn} 秒）` : "登録コード: なし");
    return 0;
  }

  if (command === "serve") {
    const log = timestampedLogger();
    let bind;
    try {
      bind = resolveBind(config.bind);
    } catch (error) {
      if (error instanceof TailscaleError) {
        problem(error.message);
        problem(error.fix);
        problem("");
        problem("Tailscale 以外で待ち受けたい場合は、設定の bind にアドレスを明示してください。");
        return 3;
      }
      throw error;
    }
    let server;
    try {
      server = createRelayServer({ config, store, log });
    } catch (error) {
      problem(`証明書を読み込めません: ${error instanceof Error ? error.message : String(error)}`);
      problem(tlsHint());
      return 3;
    }
    if (config.routes.length === 0) {
      problem("受け取り先が 1 つも登録されていません。");
      problem('  npm run relay -- route add "受信箱" <フォルダー>');
      return 3;
    }

    // One server per address.
    //
    // A single Server cannot listen twice. Tailscale gives this node both an
    // IPv4 and an IPv6 address, and the second listen() threw
    // ERR_SERVER_ALREADY_LISTEN — so the host came up on one address while
    // printing an error instead of the URL, and a phone whose resolver picked
    // the other family got a connection refused. Seen in the real serve log,
    // not reasoned about.
    let listening = 0;
    for (let index = 0; index < bind.addresses.length; index++) {
      const address = bind.addresses[index]!;
      const target = index === 0 ? server : createRelayServer({ config, store, log });
      const ok = await new Promise<boolean>((done) => {
        target.once("error", (error: NodeJS.ErrnoException) => {
          problem(`${address}:${config.port} で待ち受けられません — ${error.message}`);
          done(false);
        });
        target.listen(config.port, address, () => {
          log("INFO", `listening on ${address}:${config.port}`);
          done(true);
        });
      });
      if (ok) listening++;
    }
    if (listening === 0) {
      problem("どのアドレスでも待ち受けられませんでした。");
      return 3;
    }
    const dns = magicDnsName();
    out("");
    out(`受信を開始しました（${config.hostLabel}）`);
    if (dns) out(`スマホで開く URL: https://${dns}:${config.port}/`);
    out("止めるには Ctrl+C。");
    // Kept alive by the listening handle.
    return await new Promise<number>(() => undefined);
  }

  problem(`不明なコマンド: ${command}`);
  return 2;
}

main(process.argv.slice(2))
  .then((code) => {
    if (code !== 0) process.exitCode = code;
  })
  .catch((error: unknown) => {
    problem(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  });
