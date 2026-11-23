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
| `S3_ENDPOINT` `S3_BUCKET` `S3_ACCESS_KEY_ID` `S3_SECRET_ACCESS_KEY` | なし | GLB の置き場（S3 互換。R2 など）。4つそろえる。未設定だと `POST /v1/assets/uploads` は 503、一部だけだと起動失敗 |
| `PUBLIC_URL` | `http://localhost:<PORT>` | 外から見た URL。`BASE_PATH` を足して OpenAPI の `servers` に載せる |

## いま動くもの

本番：`https://bibai.t0waxx.com`（OpenAPI は `/v1/openapi.json`）。他の環境は `https://bibai.t0waxx.com/<環境>/v1/...`

| 種類 | エンドポイント |
|---|---|
| REST | `POST /v1/guests`、`POST /v1/sessions`、`POST /v1/sessions/{id}/join`、`GET /v1/sessions/{id}` |
| REST（オブジェクト） | `POST /v1/sessions/{id}/objects`、`PATCH`・`DELETE …/objects/{objectId}`（所有者のみ）、`POST /v1/assets/uploads` |
| WebSocket | `wss://…/v1/ws`：`auth` → `session.snapshot`、`player.transform` の中継、`object.created` / `updated` / `deleted` / `transform` |

オブジェクトの payload は `{objectId, assetKey, assetUrl, transform}`（`snapshot.payload.objects` も同じ形）。所有者と擬態フラグは載せない。
`assetUrl` は 1 時間有効の署名付き GET URL（S3 未設定の環境では付かない）。クライアントはこれで GLB を直接取得し、`assetKey`（永続キー）をキャッシュ・差し替え判定に使う。期限が切れたら再接続（snapshot）か `object.updated` で新しい URL が届く。
準備中のケイには snapshot の `objects` が空で、`object.*` も送らない。`object.transform`（C→S）は所有者だけが送れ、DB へは `PATCH` で確定する。
`assetKey` は `sessions/<sessionId>/assets/<name>.glb` の形で、そのセッション自身のものだけ指せる。
`POST /v1/assets/uploads` は `{assetKey, uploadUrl}` を返す（URL は 5分有効、GLB のみ・20MBまで）。仕様書 §5.1 のうち、擬態切替・アップロード済みの確認（サーバーが存在・サイズを検証する部分）はまだ無い。

型は `src/contracts/` の zod が正。仕様書から変えた点：

- `POST /v1/sessions` の応答に作成者の `playerId` を足した
- WS の close code：4001 未認証・4003 参加者でない・4004 セッションなし・4009 別の接続で入り直した
- ゲーム中、ケイの位置はドロに見せる（仕様書に記載なし。仮ルール）

## デプロイ

環境（prod / stg / dev / a-kun-01〜03 / so-ma-01〜03）、デプロイ、DB のリセット・コピーは [`../deploy/README.md`](../deploy/README.md)。
環境は `BASE_PATH`（例 `/a-kun-01`）で分かれ、クライアントの接続先は `https://bibai.t0waxx.com/a-kun-01` のようになる。
