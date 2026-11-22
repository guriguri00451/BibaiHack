## 概要

Closes #2

QR(画像)マーカーで共有マップの原点を合わせ、端末ローカル座標⇔マップ座標を変換するAdapterを追加。検出は WebXR `image-tracking`。

## 変更内容

- `seeker/src/ar/transform.js`: Transform型(§3.2)と three.js Matrix4 の相互変換（Quaternion正規化）
- `seeker/src/ar/coordinateAdapter.js`: `calibrate / reset / localToMap / mapToLocal`。床置きマーカーの法線(+Z)をマップ+Yへ合わせる（X軸まわり90°）
- `seeker/src/ar/markerTracker.js`: `trackedImages` 生成と、`tracked` 状態のときだけ原点を合わせる処理（`emulated` は無視）
- `seeker/src/main.js` / `index.html`: マップ原点に10cmキューブ、「原点を再スキャン」ボタン
- `seeker/public/marker.png`: マーカー画像（一辺0.15m・余白込みで印刷する前提）

前提・割り切り: マーカーは床置き。`image-tracking` は Chrome フラグ必須（`camera-access`+JS検出は今回見送り）。

## 動作確認（AI検証済み）

- [x] `npm test` グリーン（10 passed。2端末が同一マップ座標になることの数値検証を含む）
- [x] `npm run build` 成功

## ⚠️ 目視確認が必要（人間がマージ前に実施）

> AIは実機ARを動かせていません。チェックを入れるのは動かして確認した人です。

- [ ] `marker.png` を一辺0.15mで印刷して床に置く
- [ ] 2台のAndroid Chrome（`chrome://flags/#webxr-incubations` 有効）で ARを開始 → マーカーを映すと赤いキューブが出る
- [ ] 2台のキューブが実空間で同じ位置（マーカー中心の上）に見える
- [ ] 「原点を再スキャン」で合わせ直せる
- [ ] フラグ無効時にステータスへ案内が出てAR自体は起動する

## 関連

- issue #2: https://github.com/guriguri00451/BibaiHack/issues/2
