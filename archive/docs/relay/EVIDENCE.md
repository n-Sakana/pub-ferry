# キャプチャー一覧

全て **実際に動いているアプリ**から撮ったものです。デスクトップは実 WPF +
WebView2 ウィンドウの実コントロールを操作し、そのページを撮影。スマホは実 relay
ホスト（HTTPS・実証明書・実署名）に接続した実ページの撮影です。

撮っているのは**ページであってウィンドウの画面矩形ではありません**。矩形の画面
キャプチャーはウィンドウの後ろや隣にあるものまで記録してしまい、公開する証跡に
製品以外のものが混ざるためです。

この表は `tools/drive-desktop.ts` と `tools/drive-phone.ts` が書き出した
`shots.json` から `tools/make-evidence-index.ts` が生成しています。

---

## デスクトップアプリ（`evidence/desktop/`）

表の寸法は CSS ピクセル相当です。画像は高 DPI 環境で撮っているため、実ピクセル
はその 1.5 倍になります。

| ファイル | 何の状態か | 大きさ |
|---|---|---|
| `evidence/desktop/01-home.png` | 入口（送る／受け取るの二択） | 1180x800 相当 |
| `evidence/desktop/02-send-pick-empty.png` | 送る・まだ何も選んでいない（主操作は操作不能＋理由） | 1180x800 相当 |
| `evidence/desktop/02b-send-pick-chosen.png` | 送る・フォルダーを選んだ（件数・合計・所要目安） | 1180x800 相当 |
| `evidence/desktop/02c-send-showing.png` | 送る・表示中（コードが主役、右は細いレール） | 1180x800 相当 |
| `evidence/desktop/02d-send-done.png` | 送る・表示を終えた（完了とは言わない） | 1180x800 相当 |
| `evidence/desktop/03-settings.png` | 設定（保存先・呼び名・トグル・読み取り機） | 1180x800 相当 |
| `evidence/desktop/04-receive-choose.png` | 受け取り方の選択（このPCはカメラなし） | 1180x800 相当 |
| `evidence/desktop/05-camera-none.png` | カメラなし（警告色にしない／リーダーへ誘導） | 1180x800 相当 |
| `evidence/desktop/06-camera-denied.png` | カメラ拒否（復旧手順つき） | 1180x800 相当 |
| `evidence/desktop/07-reader-empty.png` | リーダー受信・まだ 1 枚も読んでいない | 1180x800 相当 |
| `evidence/desktop/08-reader-partial.png` | リーダー受信・欠番あり（番号を名指し） | 1180x800 相当 |
| `evidence/desktop/09-confirm.png` | 中身を確かめて保存 | 1180x800 相当 |
| `evidence/desktop/10-narrow-receive-choose.png` | 狭いウィンドウ（最小幅） | 900x680 相当（狭幅） |
| `evidence/desktop/11-narrow-send-pick.png` | 狭いウィンドウ・送る（2 カラムが 1 カラムに落ちる） | 900x680 相当（狭幅） |
| `evidence/desktop/12-scale125-send-pick.png` | 送る・選ぶ（表示倍率 125% 相当） | 125% 相当（1366x768 の実機） |
| `evidence/desktop/12-scale125-receive-choose.png` | 受け取り方の選択（表示倍率 125% 相当） | 125% 相当（1366x768 の実機） |
| `evidence/desktop/12-scale125-settings.png` | 設定（表示倍率 125% 相当） | 125% 相当（1366x768 の実機） |

ページのコンソールに出た問題: なし

---

## スマホ PWA（`evidence/phone/`）

3 つの実機相当幅で、同じ流れを最初から最後まで通しています。

| ファイル | 何の状態か | 大きさ |
|---|---|---|
| `evidence/phone/iphone-se-01-pair-empty.png` | 登録前（コード入力） | 375×667（iPhone SE 相当） |
| `evidence/phone/iphone-se-02-pair-filled.png` | 登録コードを入力した状態 | 375×667（iPhone SE 相当） |
| `evidence/phone/iphone-se-03-home.png` | 中継トップ（どちらへ渡すか） | 375×667（iPhone SE 相当） |
| `evidence/phone/iphone-se-04-outbox.png` | 母艦の送信待ち一覧 | 375×667（iPhone SE 相当） |
| `evidence/phone/iphone-se-05-display.png` | 連続コードを表示中（内→外） | 375×667（iPhone SE 相当） |
| `evidence/phone/iphone-se-06-display-done.png` | 表示を終えた（完了とは言わない） | 375×667（iPhone SE 相当） |
| `evidence/phone/iphone-se-07-capture.png` | カメラ経路の実状態（この環境には擬似カメラが無いため、カメラなしの表示になります） | 375×667（iPhone SE 相当） |
| `evidence/phone/iphone-se-08-camera-denied.png` | カメラ拒否（復旧手順つき） | 375×667（iPhone SE 相当） |
| `evidence/phone/pixel-01-pair-empty.png` | 登録前（コード入力） | 412×915（Android 標準相当） |
| `evidence/phone/pixel-02-pair-filled.png` | 登録コードを入力した状態 | 412×915（Android 標準相当） |
| `evidence/phone/pixel-03-home.png` | 中継トップ（どちらへ渡すか） | 412×915（Android 標準相当） |
| `evidence/phone/pixel-04-outbox.png` | 母艦の送信待ち一覧 | 412×915（Android 標準相当） |
| `evidence/phone/pixel-05-display.png` | 連続コードを表示中（内→外） | 412×915（Android 標準相当） |
| `evidence/phone/pixel-06-display-done.png` | 表示を終えた（完了とは言わない） | 412×915（Android 標準相当） |
| `evidence/phone/pixel-07-capture.png` | カメラ経路の実状態（この環境には擬似カメラが無いため、カメラなしの表示になります） | 412×915（Android 標準相当） |
| `evidence/phone/pixel-08-camera-denied.png` | カメラ拒否（復旧手順つき） | 412×915（Android 標準相当） |
| `evidence/phone/narrow-01-pair-empty.png` | 登録前（コード入力） | 320×568（いちばん狭い実機相当） |
| `evidence/phone/narrow-02-pair-filled.png` | 登録コードを入力した状態 | 320×568（いちばん狭い実機相当） |
| `evidence/phone/narrow-03-home.png` | 中継トップ（どちらへ渡すか） | 320×568（いちばん狭い実機相当） |
| `evidence/phone/narrow-04-outbox.png` | 母艦の送信待ち一覧 | 320×568（いちばん狭い実機相当） |
| `evidence/phone/narrow-05-display.png` | 連続コードを表示中（内→外） | 320×568（いちばん狭い実機相当） |
| `evidence/phone/narrow-06-display-done.png` | 表示を終えた（完了とは言わない） | 320×568（いちばん狭い実機相当） |
| `evidence/phone/narrow-07-capture.png` | カメラ経路の実状態（この環境には擬似カメラが無いため、カメラなしの表示になります） | 320×568（いちばん狭い実機相当） |
| `evidence/phone/narrow-08-camera-denied.png` | カメラ拒否（復旧手順つき） | 320×568（いちばん狭い実機相当） |

---

## 撮り直し

画面を直したら、両方とも撮り直してからこの索引を作り直してください。

```
powershell -File pc\pub-ferry.ps1 -DebugPort 9333
node --import tsx tools/drive-desktop.ts evidence/desktop
node --import tsx tools/drive-phone.ts evidence/phone
node --import tsx tools/make-evidence-index.ts
```
