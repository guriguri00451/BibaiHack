# seeker (ケイ側 AR クライアント)

three.js + WebXR (`immersive-ar` / `hit-test`)。

```bash
cd seeker && npm install && npm run dev
```

WebXR は HTTPS か localhost でしか動かない。実機 (Android Chrome) では USB 接続して
`chrome://inspect` を開き、ポートフォワード `5173 → localhost:5173` を設定するか、
`adb reverse tcp:5173 tcp:5173` のあと端末で `http://localhost:5173` を開く。
