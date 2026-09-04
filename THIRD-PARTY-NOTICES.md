# 第三者ソフトウェアの表示 / Third-party notices

Ferry は、次の第三者ソフトウェアを基礎とするか、実行時に同梱しています。

## Decimen Optical Transfer

Ferry は [bashalarmistalt/decimen-optical-transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
v0.3.0 の fork です。上流のワイヤーフォーマットには手を入れていません。

- License: MIT
- Copyright (c) 2026 Evan Crawley (Bash Alarmist)
- ライセンス全文: [`LICENSE`](LICENSE)

## ZXing.Net

`lib/zxing.dll` を QR コードの生成と読み取りに同梱しています。

- Project: [micjahn/ZXing.Net](https://github.com/micjahn/ZXing.Net)
- License: Apache License 2.0
- ライセンス全文: [`lib/LICENSE-ZXing.Net.txt`](lib/LICENSE-ZXing.Net.txt)

## zxing-wasm

`web/zxing_reader-EOacYbLr.wasm` をブラウザでの QR コード読み取りに同梱しています。

- Project: [Sec-ant/zxing-wasm](https://github.com/Sec-ant/zxing-wasm)
- License: MIT
- Copyright (c) 2023 Ze-Zheng Wu
- ライセンス全文: [`lib/LICENSE-zxing-wasm.txt`](lib/LICENSE-zxing-wasm.txt)

## zxing-cpp

同梱する zxing-wasm の QR 読み取りエンジンです。

- Project: [zxing-cpp/zxing-cpp](https://github.com/zxing-cpp/zxing-cpp)
- License: Apache License 2.0
- ライセンス全文: [`lib/LICENSE-zxing-cpp.txt`](lib/LICENSE-zxing-cpp.txt)
