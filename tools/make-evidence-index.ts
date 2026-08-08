// Builds docs/relay/EVIDENCE.md from what the two screen drivers actually
// captured, so the index cannot drift from the folder.

import { readFileSync, writeFileSync } from "node:fs";

interface Shot {
  name: string;
  what: string;
  size: string;
  file: string;
}

function read(path: string): { shots: Shot[]; consoleProblems?: string[] } {
  return JSON.parse(readFileSync(path, "utf8")) as { shots: Shot[]; consoleProblems?: string[] };
}

function rows(shots: Shot[], dir: string): string {
  return shots.map((shot) => `| \`${dir}/${shot.file}\` | ${shot.what} | ${shot.size} |`).join("\n");
}

const desktop = read("evidence/desktop/shots.json");
const phone = read("evidence/phone/shots.json");
const problems = desktop.consoleProblems ?? [];

const document = `# キャプチャー一覧

全て **実際に動いているアプリ**から撮ったものです。デスクトップは実 WPF +
WebView2 ウィンドウの実コントロールを操作し、そのページを撮影。スマホは実 relay
ホスト（HTTPS・実証明書・実署名）に接続した実ページの撮影です。

撮っているのは**ページであってウィンドウの画面矩形ではありません**。矩形の画面
キャプチャーはウィンドウの後ろや隣にあるものまで記録してしまい、公開する証跡に
製品以外のものが混ざるためです。

この表は \`tools/drive-desktop.ts\` と \`tools/drive-phone.ts\` が書き出した
\`shots.json\` から \`tools/make-evidence-index.ts\` が生成しています。

---

## デスクトップアプリ（\`evidence/desktop/\`）

表の寸法は CSS ピクセル相当です。画像は高 DPI 環境で撮っているため、実ピクセル
はその 1.5 倍になります。

| ファイル | 何の状態か | 大きさ |
|---|---|---|
${rows(desktop.shots, "evidence/desktop")}

ページのコンソールに出た問題: ${problems.length > 0 ? problems.join(" / ") : "なし"}

---

## スマホ PWA（\`evidence/phone/\`）

3 つの実機相当幅で、同じ流れを最初から最後まで通しています。

| ファイル | 何の状態か | 大きさ |
|---|---|---|
${rows(phone.shots, "evidence/phone")}

---

## 撮り直し

画面を直したら、両方とも撮り直してからこの索引を作り直してください。

\`\`\`
powershell -File pc\\pub-ferry.ps1 -DebugPort 9333
node --import tsx tools/drive-desktop.ts evidence/desktop
node --import tsx tools/drive-phone.ts evidence/phone
node --import tsx tools/make-evidence-index.ts
\`\`\`
`;

writeFileSync("docs/relay/EVIDENCE.md", document, "utf8");
console.log(
  `desktop ${desktop.shots.length} 枚 / phone ${phone.shots.length} 枚 を索引にしました。`,
);
