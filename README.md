# Ferry

> **このリポジトリは fork です。**
> 上流の [bashalarmistalt/decimen-optical-transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
> （v0.3.0 / MIT / Copyright (c) 2026 Evan Crawley (Bash Alarmist)）を基礎に、
> Ferry 固有の機能を追加しています。上流のワイヤーフォーマットには手を入れていません。

フォルダを、ネットワークを通さず画面とカメラの間で運ぶためのアプリです。光学転送、Office 文書の Markdown 化、Excel を起動しない VBA 抽出を一つの画面から使えます。

画面は C# のローカル HTTP サーバが配信します。Windows デスクトップでは WPF の窓に
WebView2 で表示し、モバイルでは同じ画面を PWA として使います。

## 起動（配布の本線）

Windows では `ferry.vbs` をダブルクリックします。黒いコンソールを出さずに Windows 標準の
Windows PowerShell 5.1 を起動し、C# ソースをその場でコンパイルして WPF + WebView2 の窓を
開きます。WebView2 Runtime が必要です。起動前に失敗した場合は
`%LOCALAPPDATA%\Ferry\logs` のログを案内します。

起動ログを見ながら使う場合は `ferry.cmd` を実行します。非 0 終了時にはコンソールが残ります。
生成された DLL はユーザー別キャッシュへ保存され、C# ソースが変わったときだけ作り直します。

Linux 用デスクトップ画面は未実装です。PowerShell 7 では HTTP サーバだけを起動できます。

```powershell
pwsh -NoProfile -File ./ferry.ps1 --no-browser
```

Windows で WPF の窓を出さず HTTP サーバだけを起動する場合は `--no-browser`、開始画面を
指定する場合は `--mode optical|markdown|vba` を使います。

## Tailnet からリモコンを開く

`your-ferry-host` では Tailscale Serve の HTTPS `10000` を Ferry の既定ポートへ転送します。

```powershell
tailscale serve --bg --yes --https=10000 http://127.0.0.1:18422
```

リモコンの URL は `https://your-ferry-host.your-tailnet.ts.net:10000/` です。ホスト名と
tailnet 名は、自分の環境の値へ置き換えてください。この経路だけを
元に戻す場合は、ほかの Serve 設定を残したまま次を実行します。

```powershell
tailscale serve --https=10000 off
```

## 実機で未確認のこと

PC 画面を QR 表示面にする往復転送と、実カメラでの受信は実機で成功しています。
一方、次の 3 点は未確認または未達です。

1. iPhone／iPad を QR 表示面にする経路
2. 「スマホに QR を出す」の継続表示（開始に成功するときと失敗するときがあります）
3. 1 枚に載せる量の設定を PC とリモコンの間で同期すること

## 開発用ビルド

`.csproj` は開発中のコンパイル確認と単一ファイル生成のために残しています。配布・通常起動には
.NET SDK も、生成済み exe の持ち込みも要りません。

```powershell
dotnet build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-x64
```

開発用ビルドに必要な SDK は .NET 9 です。
