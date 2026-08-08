# 第三者ソフトウェアの表示 / Third-party notices

このリポジトリは **fork** です。上流の著作権とライセンスはそのまま維持されて
います。

This repository is a **fork**. The upstream copyright and licence are retained
unchanged.

---

## この作品そのもの / This work

**Pub Ferry** は **Decimen Optical Transfer** を基礎に、独自の機能
（Windows デスクトップアプリ、スマートフォンの中継 PWA、Tailscale 越しの受信
ホスト、二次元コードリーダー経路）を追加した fork です。製品名は変更して
いますが、上流の著作権表示と MIT License はそのまま維持しています。

**Pub Ferry** is a fork built on **Decimen Optical Transfer**, adding its
own functionality (a Windows desktop app, a phone relay PWA, a receiving host
reachable over Tailscale, and a 2D-reader input path). The product name has
been changed; the upstream copyright notice and the MIT License are retained
unchanged.

```
MIT License

Copyright (c) 2026 Evan Crawley (Bash Alarmist)
```

全文は [`LICENSE`](LICENSE) にあります。このファイルは**変更していません**
（著作権表示の削除も差し替えもしていません）。

上流: https://github.com/bashalarmistalt/decimen-optical-transfer

この fork で足した部分も同じ MIT ライセンスの下に置かれます。MIT は
著作権表示と許諾表示を「複製物および重要な部分」に含めることを求めており、
`LICENSE` を保持することでその条件を満たしています。

The additions made in this fork are placed under the same MIT licence. The MIT
terms require the copyright and permission notice to travel with copies and
substantial portions of the software; retaining `LICENSE` unmodified is how
that condition is met here.

---

## 実行時に使うもの / Bundled at runtime

### zxing-wasm

QR の読み取り。`zxing-cpp` を WebAssembly にしたもの。

- https://github.com/Sec-ant/zxing-wasm — MIT License
- 元となる https://github.com/zxing-cpp/zxing-cpp — Apache License 2.0
- 由来 https://github.com/zxing/zxing — Apache License 2.0

`zxing_reader.wasm` は PC アプリとスマホページの両方に同梱されます。

### node-qrcode

QR の生成。

- https://github.com/soldair/node-qrcode — MIT License

### Microsoft Edge WebView2

Windows デスクトップアプリの画面を描く部品。

- ランタイム: Microsoft が配布するもの。このリポジトリには**含まれていません**。
  利用者の Windows に入っているものを使います
- .NET アセンブリ（`Microsoft.Web.WebView2.Core.dll` ほか）: NuGet パッケージ
  `Microsoft.Web.WebView2` に含まれるもの。**このリポジトリには含めず**、
  `tools/fetch-webview2.ps1` が取得します
- ライセンス: Microsoft Software License Terms (Microsoft Edge WebView2 SDK /
  Runtime Distributable Code)。パッケージに同梱される条項を参照してください
- https://developer.microsoft.com/microsoft-edge/webview2/

`pc/lib/` は `.gitignore` に入っており、バイナリはコミットされません。

---

## 組み立て・試験にだけ使うもの / Build and test only

配布物には入りません。

| もの | ライセンス |
|---|---|
| [Vite](https://github.com/vitejs/vite) | MIT |
| [@vitejs/plugin-basic-ssl](https://github.com/vitejs/vite-plugin-basic-ssl) | MIT |
| [vite-plugin-pwa](https://github.com/vite-pwa/vite-plugin-pwa) | MIT |
| [vite-plugin-singlefile](https://github.com/richardtallent/vite-plugin-singlefile) | MIT |
| [TypeScript](https://github.com/microsoft/TypeScript) | Apache-2.0 |
| [tsx](https://github.com/privatenumber/tsx) | MIT |
| [esbuild](https://github.com/evanw/esbuild)（tsx 経由） | MIT |
| [playwright-core](https://github.com/microsoft/playwright) | Apache-2.0 |
| [@types/node](https://github.com/DefinitelyTyped/DefinitelyTyped), [@types/qrcode](https://github.com/DefinitelyTyped/DefinitelyTyped) | MIT |

`playwright-core` はブラウザーを同梱しません。画面試験は**この機械にすでに
ある** Chromium を使います（`PUB_FERRY_CHROMIUM` で指定）。

---

## 参照しただけのもの / Referenced, not incorporated

デスクトップアプリの層構成と見た目の方向性は、同じ作者の 2 つの製品を
**読み取りのみ**で参照しました。コードも資産も取り込んでいません。両リポジトリは
一切変更していません。

- `C:\repos\pub\macrostudio`
- `C:\repos\pub\app-studio`

取り込んだのは方向性（BAT は薄い起動口／PowerShell が起動と構成／C# が OS 統合／
Web 側に共有資産、明るいキャンバスと 1 色のアクセント、3 段の文字、4px の余白格子）
であって、画面構成・コンポーネント・色の実値ではありません。この製品の design
token は [`shared/design/tokens.css`](shared/design/tokens.css) に独自に
書いてあります。

---

## フォントについて / Fonts

**フォントファイルは同梱していません。** スマホの PWA はオフラインで入って
動く必要があり、日本語フォントは同梱物のなかで最も大きくなるためです。各
プラットフォームにすでにある日本語 UI 書体を、プラットフォームが好む順に
指定しています（`shared/design/tokens.css` の `--font-ui`）。
