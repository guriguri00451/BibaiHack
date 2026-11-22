# seeker (ケイ側 AR クライアント)

three.js + WebXR (`immersive-ar` / `hit-test`)。

```bash
cd seeker && npm install && npm run dev
```

WebXR は HTTPS か localhost でしか動かない。実機 (Android Chrome) では USB 接続して
`chrome://inspect` を開き、ポートフォワード `5173 → localhost:5173` を設定するか、
`adb reverse tcp:5173 tcp:5173` のあと端末で `http://localhost:5173` を開く。

## マーカーによる原点合わせ

`public/marker.png`（QR）を**一辺 0.15m（余白込み）**で印刷し、床に置く。QR 中心が共有マップの原点、
法線（上向き）がマップの +Y。2台で同じマーカーを読めば同じマップ座標になる。

- 検出は WebXR `image-tracking`。Android Chrome で `chrome://flags/#webxr-incubations` を有効化する
- 印刷サイズを変えたら `src/ar/markerTracker.js` の `MARKER_WIDTH_METERS` も合わせる
- 「原点を再スキャン」ボタンで合わせ直せる
- テスト: `npm test`
