# Ferry

フォルダを、ネットワークを通さず画面とカメラの間で運ぶためのアプリです。光学転送、Office 文書の Markdown 化、Excel を起動しない VBA 抽出を一つの画面から使えます。

画面は C# のローカル HTTP サーバが配信し、デスクトップとモバイルで同じ実装を使います。旧 TypeScript / Node / WebView2 実装は [`archive/`](archive/) にそのまま保存しています。

## 起動（配布の本線）

Windows では `ferry.cmd` をダブルクリックします。Windows 標準の Windows PowerShell 5.1 が
C# ソースをその場でコンパイルし、ローカル HTTP サーバを立てて既定ブラウザを開きます。
生成された DLL はユーザー別キャッシュへ保存され、C# ソースが変わったときだけ作り直します。

Linux では PowerShell 7 から同じソースを起動できます。

```powershell
pwsh -NoProfile -File ./ferry.ps1
```

既定では `http://localhost:18422/` を開きます。ブラウザを自動で開かない場合は
`--no-browser`、開始画面を指定する場合は `--mode optical|markdown|vba` を使います。

## Tailnet からリモコンを開く

`your-ferry-host` では Tailscale Serve の HTTPS `10000` を Ferry の既定ポートへ転送します。

```powershell
tailscale serve --bg --yes --https=10000 http://127.0.0.1:18422
```

リモコンの URL は `https://your-ferry-host.your-tailnet.ts.net:10000/` です。この経路だけを
元に戻す場合は、ほかの Serve 設定を残したまま次を実行します。

```powershell
tailscale serve --https=10000 off
```

## 開発用ビルド

`.csproj` は開発中のコンパイル確認と単一ファイル生成のために残しています。配布・通常起動には
.NET SDK も、生成済み exe の持ち込みも要りません。

```powershell
dotnet build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-x64
```

開発用ビルドに必要な SDK は .NET 9 です。
