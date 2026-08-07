# セットアップと起動

先生がそのまま実行できる手順です。開発者向けの補足は最後にまとめました。

---

## いちばん短い道: PC アプリを試す

**必要なもの**: Windows 10/11、Microsoft Edge WebView2 ランタイム（大抵すでに入っています）。

1. エクスプローラーで `pc` フォルダーを開く
2. **`pub-transfer.bat` をダブルクリック**

初回は画面の組み立てに 1 分ほどかかります（Node.js が必要です。無い場合は
その旨が表示されます）。二回目以降はすぐ開きます。

うまくいかないときは、黒い画面に日本語で理由が出ます。閉じずに読んでください。

| 出るもの | 意味 | 直し方 |
|---|---|---|
| WebView2 ランタイムが見つかりません | 画面を描く部品が無い | 表示された URL から入れる |
| 画面を組み立てるには Node.js が必要です | 初回ビルドができない | [nodejs.org](https://nodejs.org/) から入れる |
| WebView2 のアセンブリを取得できませんでした | 取得に失敗した | `tools\fetch-webview2.ps1 -From <DLL のあるフォルダー>` |

---

## 受信ホスト（内側の PC / Linux VPS）

スマホと内側の PC・VPS をつなぐ側です。**Tailscale の中でだけ待ち受けます。**

### 1. 前提

- Node.js 20 以上
- Tailscale が入っていて `tailscale status` が通ること
- 証明書（次項）

### 2. 設定を作る

```
npm install
npm run relay -- init
```

`%LOCALAPPDATA%\PubTransfer\relay\config.json`（Windows）または
`~/.config/pub-transfer/relay/config.json`（Linux）ができます。

### 3. 証明書を用意する

スマホのブラウザーは https でないとカメラを使えず、警告なしで使うには
本物の証明書が要ります。Tailscale が発行してくれます。

```
# Linux
sudo tailscale cert <ホスト名>.<tailnet 名>.ts.net

# Windows（管理者の PowerShell）
tailscale cert <ホスト名>.<tailnet 名>.ts.net
```

できた `.crt` と `.key` のパスを設定の `tls.certFile` / `tls.keyFile` に書きます。

**試すだけなら**自己署名でも動きます（スマホで初回に警告が出ます）:

```
npm run relay -- dev-cert
```

### 4. 受け取り先を登録する

**スマホに見えるのは、ここで登録した名前だけです。** スマホから PC の
フォルダーを一覧することはできません。

```
npm run relay -- route add "受信箱" "D:\pub-transfer\受信箱"
npm run relay -- route list
```

### 5. 画面を組み立てて、受信を始める

```
npm run build:relay-web
npm run relay -- serve
```

起動すると、スマホで開く URL が表示されます。

### 6. スマホを登録する

1. スマホで、表示された `https://<ホスト>.<tailnet>.ts.net:8842/` を開く
2. PC の別のターミナルで `npm run relay -- pair`
3. 表示された 10 文字のコードをスマホに入力する

コードは **10 分だけ**、**1 回だけ**使えます。間違いが 5 回続くと無効になります。
`serve` は動かしたままで構いません。

スマホでは「ホーム画面に追加」でアプリとして入ります。

### 7. Tailscale の ACL を絞る（推奨）

bind しただけでは「自分だけが触れる」ことにはなりません。Tailscale の既定 ACL は
全ノード全ポート許可です。管理コンソールでこの port を必要な端末に限ってください。

```jsonc
// 例: スマホのタグからだけ 8842 に届くようにする
{
  "acls": [
    { "action": "accept", "src": ["tag:phone"], "dst": ["tag:pub-transfer-host:8842"] }
  ]
}
```

Windows では初回に Defender ファイアウォールの確認が出ます。許可してください。

---

## Linux VPS で常駐させる

```
sudo cp relay/systemd/pub-transfer-relay.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now pub-transfer-relay
sudo systemctl status pub-transfer-relay
```

ユニットは `tailscaled` の後に起動し、失敗しても間を置いて再試行します
（`tailscaled` は起動直後には応答しないため）。**アドレスが取れないときに
0.0.0.0 へ落ちることはありません。**

---

## 送るものを内側の PC に用意する（流れ A）

```
npm run relay -- outbox add "経費精算 8月" "C:\work\経費精算"
npm run relay -- outbox list
```

スマホの「母艦から受け取って、向かいの画面へ流す」に出てきます。

---

## 開発者向け

```
npm install
npm test                    # 全ての自動テスト
npm run build:pc            # PC アプリの画面
npm run build:relay-web     # スマホの画面
npm run build:all           # 上流のサイト（元からある3ページ）
npm run relay -- status     # 設定と状態
```

画面の試験:

```
# 実アプリを起動してデバッグポートを開く
powershell -File pc\pub-transfer.ps1 -DebugPort 9333

# 別のターミナルから実ウィンドウを操作してキャプチャー
node --import tsx tools/drive-desktop.ts evidence/desktop

# スマホ画面（実 relay ホストを立てて操作）
$env:PUB_TRANSFER_CHROMIUM = "<chrome.exe のパス>"
node --import tsx tools/drive-phone.ts evidence/phone
```

`-FakeVideo <file.y4m>` を渡すと、カメラの代わりに録画を使います
（`node --import tsx tools/make-qr-video.ts out.y4m` で作れます）。
この環境の Chromium / WebView2 は擬似カメラを提供しなかったため、
実際に通っているかは [UNVERIFIED.md](UNVERIFIED.md) を参照してください。
