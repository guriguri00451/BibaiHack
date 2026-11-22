## 概要

仕様書（`docs/whiteboard-brainstorm.md`）の Game Server の最初の段階と、デプロイ先を追加。
実装順序の 1〜3（契約・Backend 基盤・同期の検証）まで。ラウンド進行・オブジェクト・捕獲はまだ。

- 技術：TypeScript / Bun / Hono（`@hono/zod-openapi`）/ Drizzle / PostgreSQL 18
- 置き場所：wds-pve の VM `bibai-game` に docker compose。Cloudflare Tunnel で `bibai.t0waxx.com` に出す
- 環境：prod / stg / dev / a-kun-01〜03 / so-ma-01〜03 の9つをパスで分ける

## 変更内容

- `server/`
  - 契約：Transform・REST・WebSocket のメッセージを zod で定義（`src/contracts/`）。OpenAPI 3.1 を `/v1/openapi.json` で返す
  - DB：仕様書 3.1 の4テーブルを Drizzle で定義し、マイグレーション（`drizzle/`）を起動時に当てる。`DATABASE_URL` が無ければ PGlite で動くので、手元では Postgres なしで `bun run dev` できる
  - REST：`POST /v1/guests`、`POST /v1/sessions`、`POST /v1/sessions/{id}/join`、`GET /v1/sessions/{id}`
  - WebSocket（`/v1/ws`）：`auth` → `session.snapshot`、`player.transform` の中継（`clientSeq` で重複・逆順を捨て、ラウンド内の `seq` を振る）
  - `BASE_PATH`（例 `/a-kun-01`）で、1つのドメインの下に複数の環境を置ける
- `deploy/`
  - `envs.txt` に環境を並べ、`render.sh` で compose とトンネルの設定を生成する
  - `deploy.sh <環境> <ブランチ>`：コミット済みの `server/` だけを送り、その環境だけ作り直す。`status` / `logs` / `all`
  - `db.sh`：`reset` / `copy` / `dump` / `restore` / `psql` / `list`
  - prod を書き換える操作には `--yes-prod` が必要
  - 手順は `deploy/README.md`

## 仕様書から変えた点

- `POST /v1/sessions` の応答に、作成者自身の `playerId` を足した
- WebSocket の close code を決めた：4001 未認証・4003 参加者でない・4004 セッションなし・4009 別の接続で入り直した
- 環境ごとに URL の前置きが付く（例 `https://bibai.t0waxx.com/a-kun-01/v1/ws`）。prod は前置きなし
- ゲーム中、ケイの位置はドロに見せる（仕様書に記載なし。**仮ルール**）

## 動作確認（AI検証済み）

- [x] `bun test` 64 passed / `bun run typecheck` 成功
- [x] 9環境すべてで、公開 URL の `/healthz` が 200
- [x] 公開 URL で2人がセッションに入り、位置が相手に届く（片道の中継で約31ms。VM → Cloudflare → VM）
- [x] 環境が混ざらない：so-ma-01 のトークンは so-ma-02 で 401
- [x] `db.sh` の copy → dump → reset（0件）→ restore（1件に戻る）、`--yes-prod` なしの `reset prod` が止まる

## ⚠️ 未解決・目視確認が必要

- [ ] クライアント（`seeker` / `seeker-unity`）からはまだ繋いでいない。実機（スマホ・XREAL・Vision Pro）からの接続は未確認
- [ ] ラウンド進行・オブジェクト・捕獲・GLB アップロード・ログ／リプレイは未実装（仕様書の実装順序 4〜6）
- [ ] ケイの位置をドロに見せるかどうか（上の仮ルール）
- [ ] 開発環境も公開している。URL を知っていれば誰でも触れる
- [ ] wds-pve ごと再起動したときに自動で戻るかは未確認（VM 単体の再起動では約30秒で戻ることを確認済み）
- [ ] DB 名を素の `dev` にすると Bun.sql が `FailedToOpenSocket` で繋がらない。原因は未調査で、DB 名に `env_` を付けて回避している

## 使い方

接続先の例：`https://bibai.t0waxx.com/so-ma-01`（その下に `/v1/...`、`/v1/ws`、`/v1/openapi.json`）

    deploy/deploy.sh so-ma-01 <ブランチ>
    deploy/db.sh reset so-ma-01
