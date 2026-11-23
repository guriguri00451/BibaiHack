# deploy（サーバーの環境とデプロイ）

ゲームサーバーは wds-pve 上の VM `bibai-game`（VMID 100、Debian 13）で動いている。
外へは Cloudflare Tunnel `bibai-game` で出していて、ドメインは `bibai.t0waxx.com` の1つだけ。
環境はパスで分ける。

## 環境

| 環境 | URL（クライアントに設定する接続先） | 入れるもの | DB |
|---|---|---|---|
| prod | `https://bibai.t0waxx.com` | `main` | `env_prod` |
| stg | `https://bibai.t0waxx.com/stg` | 次に本番へ出すもの | `env_stg` |
| dev | `https://bibai.t0waxx.com/dev` | 統合中のブランチ | `env_dev` |
| a-kun-01〜03 | `https://bibai.t0waxx.com/a-kun-01` など | a-kun の作業ブランチ | `env_a_kun_01` など |
| so-ma-01〜03 | `https://bibai.t0waxx.com/so-ma-01` など | so-ma の作業ブランチ | `env_so_ma_01` など |

接続先の下は全環境で同じ形になる。

```
<接続先>/healthz             死活確認
<接続先>/v1/...               REST
<接続先>/v1/ws                WebSocket（wss://）
<接続先>/v1/openapi.json      その環境の OpenAPI
```

- 環境ごとにサーバーのプロセス・DB・`JWT_SECRET` が別。ある環境で発行したトークンは別の環境では使えない
- Postgres は1つ（`postgres:18-alpine`）で、その中に環境ごとのデータベースがある
- 開発環境も公開している。URL を知っていれば誰でも触れるので、大事なデータは入れない

## デプロイ

towa-mini（`wds-pve` に root で SSH できる Mac）から実行する。**コミット済みの `server/` だけ**が送られる（作業中の変更は入らない）。
ブランチはローカルに無ければ `origin/<ブランチ>` を使う。

```bash
deploy/deploy.sh a-kun-01 feature/foo        # a-kun-01 に feature/foo を入れる
deploy/deploy.sh dev t0waxx/feat-server       # dev に入れる
deploy/deploy.sh prod main --yes-prod         # 本番（--yes-prod が無いと止まる）
deploy/deploy.sh all main --yes-prod          # 全環境に同じものを入れる
deploy/deploy.sh status                       # 環境ごとの状態・コミット・日時・ブランチ
deploy/deploy.sh logs so-ma-02 200            # サーバーのログ（末尾200行）
```

デプロイすると、その環境のサーバーだけが作り直される（他の環境は止まらない）。そのとき次のことが起きる。

- 起動時にマイグレーションが当たる（`server/drizzle/`）
- メモリ上のゲーム状態（接続・位置）は消える。クライアントは繋ぎ直す
- 公開 URL の `/healthz` を叩いて、結果を表示する

### 本番へ出す流れ

```bash
deploy/db.sh copy prod stg                  # 本番のデータを stg に複製
deploy/deploy.sh stg <出すブランチ>           # stg でマイグレーションと動作を確かめる
# main にマージしたら
deploy/deploy.sh prod main --yes-prod
```

## DB の操作

```bash
deploy/db.sh list                            # DB と大きさの一覧
deploy/db.sh reset a-kun-01                  # 空にして作り直す
deploy/db.sh copy prod a-kun-01              # 本番の中身で a-kun-01 を置き換える
deploy/db.sh copy so-ma-01 so-ma-02          # 開発環境どうしの複製
deploy/db.sh dump prod                       # deploy/backups/prod-<日時>.dump に書き出す
deploy/db.sh restore stg deploy/backups/prod-20261123-030000.dump
deploy/db.sh psql dev                        # psql で入る
deploy/db.sh psql dev 'select count(*) from users'
```

- `reset` / `copy` / `restore` は、書き換える先の環境のサーバーを止めてから行い、終わったら起動し直す。マイグレーションは起動時に当たる
- 書き換える先が prod のときは `--yes-prod` が要る（`db.sh reset prod --yes-prod` など）
- `copy` の元は止めない（`pg_dump` は一貫したスナップショットを取る）
- 複製した先では、元の環境のトークンは使えない（`JWT_SECRET` が違う）。ゲスト作成からやり直す
- `deploy/backups/` は git に入らない。書き出しは towa-mini に残る
- 古いブランチの DB を新しいブランチに複製した場合、デプロイ（または `reset` 後の起動）で足りないマイグレーションが当たる。逆に、新しいマイグレーションが当たった DB を古いブランチで動かすと壊れることがある

## 環境を増やす・減らす

1. `deploy/envs.txt` に名前を足す（英小文字・数字・ハイフン）
2. `deploy/render.sh` で `compose.yaml` と `cloudflared/config.yml` を作り直してコミット
3. `deploy/deploy.sh <新しい環境> <ブランチ>`

足りない `JWT_SECRET_*`・`ADMIN_TOKEN` はデプロイ時に VM 上で作られる。トンネルの設定が変わると cloudflared が再起動する（全環境が数秒切れる）。
減らすときは `envs.txt` から消して render → 残っている環境のどれかをデプロイし、VM で `docker compose up -d --remove-orphans` を実行する。DB は残るので、要らなければ `db.sh psql` で `DROP DATABASE`。

## VM の中

```
/opt/bibai/
  compose.yaml               deploy/compose.yaml の写し（生成物）
  cloudflared/config.yml     トンネルの振り分け（生成物）
  cloudflared/credentials.json  トンネルの秘密鍵（リポジトリに入れない）
  .env                       POSTGRES_PASSWORD・JWT_SECRET_*・ADMIN_TOKEN（VM 上でだけ生成。mode 600）
  envs/<環境>/server/          デプロイしたソース
  envs/<環境>/DEPLOYED         入っているブランチ・コミット・日時・実行者
```

管理画面（`https://bibai.t0waxx.com/<環境>/admin`、prod は `/admin`）のトークンは全環境で共通の `ADMIN_TOKEN`。見るには `ssh -J root@wds-pve towa@192.168.11.2 grep ADMIN_TOKEN /opt/bibai/.env`。

VM に入る：`ssh -J root@wds-pve towa@192.168.11.2`（VM は tailnet に居ない。IP は DHCP なので変わることがある。変わったら `BIBAI_VM=towa@<新しいIP>` を付けて実行する）

## 困ったとき

| 症状 | 見るところ |
|---|---|
| `deploy.sh` が「healthcheck が通りません」で止まる | 一緒に出るログ。`deploy/deploy.sh logs <環境>` |
| 公開 URL が 502 / 530 | cloudflared が繋がっていない：VM で `docker compose logs --tail 50 cloudflared` |
| 1つの環境だけ 404 | `envs.txt` に名前があるか、render 後にデプロイしたか |
| `Permission denied` / 繋がらない | `ssh root@wds-pve` が通るか。VM の IP が変わっていないか |
| VM ごと再起動したい | `ssh root@wds-pve qm reboot 100`。全部自動で起動する（約30秒） |

注意：DB 名を素の `dev` にすると、Bun の Postgres クライアント（Bun 1.3.14）が `FailedToOpenSocket` で繋がらない。DB 名に `env_` を付けているのはそのため。
