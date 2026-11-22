# mock-server

Seeker の同期オブジェクト表示を確認するためのモック（仕様 §6 の最小サブセット）。
マーカー周り半径0.5mを3つのキューブが15Hzの配信で周回する。
`PHASE_CYCLE=1 npm start` でフェーズ（waiting→preparing→playing→finished、finished 後に round++）を巡回する。

```bash
npm install && npm start      # ws://0.0.0.0:8787/v1/ws
```

実機から繋ぐときは、ゲーム進行側が `SyncConnection.FromBaseUrl("http://<PCのLAN IP>:8787", sessionId, token)` を `SeekerArController.StartSync` に渡す（`localhost` は端末自身を指す）。モックは任意のトークンを受け付ける。
GLB の URL は snapshot の `assetUrl` に載せる（実サーバーは期限付き署名URL、モックはこのサーバー自身の `/files/...`）。
