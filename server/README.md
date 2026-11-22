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
| `DATABASE_URL` | なし | PostgreSQL の接続先。無ければ PGlite（プロセス内の Postgres）で動く |
| `PGLITE_DIR` | なし（メモリ） | PGlite の保存先 |
| `BASE_PATH` | なし | この環境の URL の前置き（例 `/a-kun-01`）。前置きの無いリクエストは 404 |
| `PUBLIC_URL` | `http://localhost:<PORT>` | 外から見た URL。`BASE_PATH` を足して OpenAPI の `servers` に載せる |

## いま動くもの

本番：`https://bibai.t0waxx.com`（OpenAPI は `/v1/openapi.json`）。他の環境は `https://bibai.t0waxx.com/<環境>/v1/...`

| 種類 | エンドポイント |
|---|---|
| REST | `POST /v1/guests`、`POST /v1/sessions`、`POST /v1/sessions/{id}/join`、`GET /v1/sessions/{id}` |
| WebSocket | `wss://…/v1/ws`：`auth` → `session.snapshot`、`player.transform` の中継 |

型は `src/contracts/` の zod が正。仕様書から変えた点：

- `POST /v1/sessions` の応答に作成者の `playerId` を足した
- WS の close code：4001 未認証・4003 参加者でない・4004 セッションなし・4009 別の接続で入り直した
- ゲーム中、ケイの位置はドロに見せる（仕様書に記載なし。仮ルール）

## デプロイ

環境（prod / stg / dev / a-kun-01〜03 / so-ma-01〜03）、デプロイ、DB のリセット・コピーは [`../deploy/README.md`](../deploy/README.md)。
環境は `BASE_PATH`（例 `/a-kun-01`）で分かれ、クライアントの接続先は `https://bibai.t0waxx.com/a-kun-01` のようになる。
