# server（ゲームサーバー）

仕様書 [`docs/whiteboard-brainstorm.md`](../docs/whiteboard-brainstorm.md) の Game Server。
TypeScript / Bun / Hono。REST と WebSocket を1プロセスで受ける。

```bash
cd server && bun install
bun run dev        # http://localhost:8080
bun test
bun run typecheck
```

## 環境変数

| 名前 | 既定 | 内容 |
|---|---|---|
| `PORT` | `8080` | 待ち受けポート |
| `JWT_SECRET` | 開発時は起動ごとに生成 | アクセストークンの署名鍵。`NODE_ENV=production` では必須 |
