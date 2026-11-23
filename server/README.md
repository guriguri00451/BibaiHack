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
| `PUBLIC_URL` | `http://localhost:<PORT>` | 外から見た URL。`BASE_PATH` を足して OpenAPI の `servers` に載せる。明示したときは署名付き URL・`modelUrl` もこの URL で作る（無ければリクエストの Host から作る） |
| `STORAGE_DIR` | なし（メモリ） | ログ・Snapshot の置き場。S3 を設定していなければ GLB もここに置く。デプロイ先では環境ごとのボリューム `/data/storage` |
| `ADMIN_TOKEN` | なし | 管理画面（`/admin`）のトークン（16文字以上）。未設定だと、開発時は誰でも入れ、`NODE_ENV=production` では管理画面を閉じる |
| `S3_ENDPOINT` `S3_BUCKET` `S3_ACCESS_KEY_ID` `S3_SECRET_ACCESS_KEY` | なし | GLB の置き場（S3 互換。R2 など）。4つそろえる。未設定ならサーバー自身が受ける。一部だけだと起動失敗 |

## いま動くもの

本番：`https://bibai.t0waxx.com`（OpenAPI は `/v1/openapi.json`）。他の環境は `https://bibai.t0waxx.com/<環境>/v1/...`

仕様書 5章の REST と 6章の WebSocket はすべてある。7章のログ・Snapshot・リプレイも入っている。

| 種類 | エンドポイント |
|---|---|
| 参加 | `POST /v1/guests`、`POST /v1/sessions`、`POST /v1/sessions/{id}/join`、`GET /v1/sessions/{id}`、`GET /v1/sessions/{id}/state` |
| ラウンド（ホスト） | `POST /v1/sessions/{id}/rounds/start` · `play` · `finish` · `stop-cycle`、`PUT /v1/sessions/{id}/map` |
| アセット | `POST /v1/assets/uploads` → 返った `uploadUrl` に `PUT`（GLB のみ・20MB まで・URL は5分有効） |
| オブジェクト（ドロ） | `POST /v1/sessions/{id}/objects`、`PATCH`・`DELETE …/objects/{objectId}`、`POST …/objects/{objectId}/disguise` |
| 捕獲（ケイ） | `POST /v1/sessions/{id}/captures` |
| マップ | `GET /v1/maps`（一覧）、`GET /v1/maps/{mapId}`、GLB は `GET /v1/maps/{mapId}/model.glb` で認証なし |
| 管理画面 | `GET /admin`（画面）、`/v1/admin/*`（その API。OpenAPI には載せない） |
| リプレイ | `GET /v1/sessions/{id}/rounds/{round}/replay`（終わったラウンドだけ） |
| WebSocket | `wss://…/v1/ws`：`auth` → `session.snapshot`。C→S は `player.transform`・`object.transform`。S→C は `session.phase_changed`・`object.created/updated/deleted/transform`・`player.caught`・`command.ack`・`error` |

### ゲームの進み方

- `rounds/start`：`waiting` か `finished` から。`finished` からなら `round + 1`、`seq` は 0 に戻る。`roles` に載せなかった人は役割なし。ドロが1人以上必要
- 準備の期限が来るとサーバーが自分で本番へ進める（長さは `rounds/start` の `playingSeconds`、無ければ 180 秒）。本番の期限か全ドロの捕獲で自分で終える
- `rounds/start` に `resultSeconds` を付けると、ラウンドが終わってその秒数で次のラウンドを同じ設定・同じ役割で自分で始める（サイクル。後から入った人はケイ）。そのあいだ `finished` の `phaseEndsAt` は次のラウンドが始まる時刻。設定は `game_sessions.cycle` に置くので再起動しても続く
- サイクルは `rounds/stop-cycle`（ホスト）か管理画面で止まる。`resultSeconds` 無しで `rounds/start` しても外れる。次のラウンドに入るときに誰も繋いでいなければ、そこで止まる
- マップは待機中と `finished` の間だけ `PUT /v1/sessions/{id}/map` で替えられる（ホスト）。繋いでいる全員に `session.snapshot`（`session.mapId`）を送り直す
- ホストの `play`・`finish` と期限の自動遷移が重なったら、後のほうは 409
- フェーズが変わるたびに `session.phase_changed` を送り、見せてよい範囲が変わるので全員に `session.snapshot` も送り直す
- 再起動すると、準備中・本番中のセッションは起動時に読み込んでタイマーを再開する。`seq` は書けたログの続きから数える

### 誰に何を見せるか（`src/game/visibility.ts`）

| | 待機中 | 準備中 | 本番中 | 終了後 |
|---|---|---|---|---|
| オブジェクト（ドロに） | なし | 全部＋作成者・擬態先 | 全部＋作成者・擬態先 | 全部＋作成者・擬態先 |
| オブジェクト（ケイ・役割なしに） | なし | なし | `objectId`・`assetKey`・`transform` だけ | 全部＋作成者・擬態先 |
| ドロの位置（ケイに） | 見せる | 見せない | 見せない | 見せる |

擬態先の切り替えはケイには知らせない（`object.updated` を送らない）。

### 点数（`src/game/rules.ts`、仮の値）

捕獲したケイに +100、本番の終わりまで見つからなかったドロに +100、外れは 0。捕獲は1人 1.5 秒に1回まで（超えると 429 と `Retry-After`）。

型は `src/contracts/` の zod が正。仕様書から変えた点・決めた点：

- `POST /v1/sessions` の応答に作成者の `playerId` を足した
- WS の close code：4001 未認証・4003 参加者でない・4004 セッションなし・4009 別の接続で入り直した
- ゲーム中、ケイの位置はドロに見せる（仕様書に記載なし。仮ルール）
- `rounds/start`・`play`・`finish` は `GET /v1/sessions/{id}` と同じ形を返す。`rounds/start` に `playingSeconds`（任意）を足した
- オブジェクト API は `{ id, round, assetKey, transform, isDisguise, createdAt, updatedAt }` を返す（仕様書の `{id, round}` に足した）。`DELETE` は 204
- 捕獲の応答に `score`（ケイの累計）と、当たりなら `caughtPlayerId` を足した
- 仕様書に無いものを足した：`GET /v1/maps/{mapId}/model.glb`、リプレイ API
- オブジェクトの payload（snapshot の `objects` も同じ）は `{objectId, assetKey, assetUrl, transform}`。ドロと終了後の全員には `ownerPlayerId`・`isDisguise` も付く。`assetUrl` は1時間有効の署名付き GET URL で、クライアントはこれで GLB を直接取る。`assetKey`（永続キー）はキャッシュ・差し替え判定に使う。期限が切れたら再接続（snapshot）か `object.updated` で新しい URL が届く
- GLB の置き場は S3 を設定していればそちら。無ければサーバー自身が署名付き URL（`/v1/storage/...`）を配って受け、大きさ・形式（先頭バイト）・所有者を確かめ、同じキーには書き直させない。オブジェクト登録のときは、アップロード済みか・申告どおりの大きさか・本人が上げたものかを確かめる
- エラーコード：役割が違う `FORBIDDEN_ROLE`、他人のオブジェクト `NOT_OBJECT_OWNER`、他のセッションの assetKey `ASSET_NOT_IN_SESSION`、未アップロード `ASSET_NOT_UPLOADED`、フェーズ違い `INVALID_GAME_PHASE`、見つかったドロ `PLAYER_CAUGHT`
- ログは `sessions/{id}/rounds/{round}/events/{first}-{last}.jsonl`（2秒ごと・ラウンド終了時に書く）、Snapshot は `…/snapshots/{seq}.json`（ラウンド開始・30秒ごと・終了時）。Transform は 1 秒に1件まで間引く
- WS の C→S メッセージは `payload.requestId` を付けると `command.ack`（失敗なら同じ `requestId` 付きの `error`）が返る
- 後から参加した人がいると、繋いでいる全員に `session.snapshot` を送り直す（ホストが役割を割り当てられるように）
- 404 の `error.code` は `SESSION_NOT_FOUND`・`OBJECT_NOT_FOUND`・`MAP_NOT_FOUND` など。`NOT_FOUND` は経路が無いときだけ（a-kun-unity はこれで未実装を見分けている）

### マップ

- 組み込み：`maps/<mapId>.json` と GLB。起動時に読む。変えるにはデプロイ
- 登録：管理画面から上げたもの。定義は DB の `maps`、GLB は `STORAGE_DIR`（S3 を設定していても）の `maps/<mapId>/<uuid>.glb`。100MB まで。差し替えると新しいキーに置き、`modelUrl` に `?v=` が付いて変わる
- 組み込みの mapId では登録できない。消しても GLB は残す（そのマップで遊んだ部屋のため）

### 管理画面（`/admin`）

- セッション：新しい順に 50 件。状態・残り時間・参加者（繋いでいるか・役割・点）。ホストでなくても、ラウンドを始める（サイクルがあればその設定、無ければホストがドロ・ほかはケイ・準備 120 秒）・本番へ・終える・サイクルを止める・マップを替える
- マップ：登録・差し替え・名前と置き方（位置・回転・大きさ）の変更・削除・3D で確認
- トークンは `ADMIN_TOKEN`。デプロイ先では全環境で同じ値（[`../deploy/README.md`](../deploy/README.md)）

## デプロイ

環境（prod / stg / dev / a-kun-01〜03 / so-ma-01〜03）、デプロイ、DB のリセット・コピーは [`../deploy/README.md`](../deploy/README.md)。
環境は `BASE_PATH`（例 `/a-kun-01`）で分かれ、クライアントの接続先は `https://bibai.t0waxx.com/a-kun-01` のようになる。

GLB・ログは環境ごとのボリューム `storage-<環境>` に置く。`deploy/db.sh` の `reset`・`copy` は DB だけを扱い、このボリュームには触らない。
