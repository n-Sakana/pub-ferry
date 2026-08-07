# 光学ファイル転送ツール — 文書一覧

上流の「画面とカメラだけで 1 ファイルを渡す」実験を、**フォルダー一式を運べる
実用品**にしたものです。同期ソフトではありません。指定したものを一回だけ運びます。

---

## 先生向け

| 文書 | 何が書いてあるか |
|---|---|
| **[USAGE.md](USAGE.md)** | 使い方。2 つの流れ、画面の読み方、受け取り方 2 種、送れる大きさ |
| **[SETUP.md](SETUP.md)** | 入れ方と起動。`pc\pub-transfer.bat` をダブルクリックが最短 |

## 中身

| 文書 | 何が書いてあるか |
|---|---|
| [PLAN.md](PLAN.md) | 実装計画（レビュー前の版。ここからどう変えたかは REVIEW-RESPONSE.md） |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 構成、データの形、**脅威境界**、守らないもの |
| [REVIEW-RESPONSE.md](REVIEW-RESPONSE.md) | レビュー指摘 47 件の採否と理由 |

## 検証

| 文書 | 何が書いてあるか |
|---|---|
| [TEST-MATRIX.md](TEST-MATRIX.md) | 何を、どのテストで守っているか |
| [RESULTS.md](RESULTS.md) | 実行結果。テスト件数、実 GUI で通した範囲、直した崩れ |
| [EVIDENCE.md](EVIDENCE.md) | キャプチャー 44 枚の一覧 |
| **[UNVERIFIED.md](UNVERIFIED.md)** | **確認できていないことと、確かめる手順** |

---

## いちばん短い説明

```
内側の PC / VPS  ──(Tailscale)──  スマホ  ──(画面 → カメラ)──  外側の PC
                                    中継
```

- **PC アプリ**は内側でも外側でも同じもの。`pc\pub-transfer.bat` から起動
- **スマホの PWA** は受信ホストが配信する。ホストの URL を開いてホーム画面に追加
- **受信ホスト**は Node で動き、**Tailscale のアドレスにだけ**待ち受ける。
  取れなければ起動しない
- 光の経路は**暗号化されていない**。与える性質は「間に network が無いこと」

## ライセンス

fork です。上流の [LICENSE](../../LICENSE)（MIT / Copyright (c) 2026
Evan Crawley (Bash Alarmist)）は**そのまま維持**しています。第三者表示は
[THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md)。
