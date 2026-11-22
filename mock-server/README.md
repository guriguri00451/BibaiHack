# mock-server

Seeker の同期オブジェクト表示を確認するためのモック（仕様 §6 の最小サブセット）。
マーカー周り半径0.5mを3つのキューブが15Hzの配信で周回する。
`PHASE_CYCLE=1 npm start` でフェーズ（waiting→preparing→playing→finished、finished 後に round++）を巡回する。

```bash
npm install && npm start      # ws://0.0.0.0:8787/v1/ws
```

実機から繋ぐときは `SyncedObjectsController` の `wsUrl` / `restBaseUrl` を `localhost` ではなく PC の LAN IP にする。
`/v1/assets/download-url` の応答形は仮置き（実サーバーと未合意）。
