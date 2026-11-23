# ステージ：美唄のゾーン（Gaussian Splat）

美唄の会場を Gaussian Splat で撮ったものを、8つのゾーンと残り（`rest`）に分けた。元の解像度のものと、点を半分にしたもの（1/2）の両方を、サーバーのマップとして **全9環境**に登録してある（合わせて21マップ）。
登録するものの一覧は [`deploy/stage-maps.tsv`](../deploy/stage-maps.tsv)。

## 元の解像度

wds-pc-029 の `~/bibai-editor/zones_wide/`。区分けは広め（隣のゾーンと少し重なる）。

| mapId | 名前 | 点の数 | 大きさ |
|---|---|---:|---:|
| `bibai-zone-a` | 美唄 西側（A） | 1,221,440 | 23.0MB |
| `bibai-zone-b` | 美唄 南西側（B） | 692,560 | 13.5MB |
| `bibai-zone-c` | 美唄 北西側（C） | 575,915 | 11.0MB |
| `bibai-zone-d` | 美唄 中央北側（D） | 827,889 | 16.1MB |
| `bibai-zone-e` | 美唄 北東側（E） | 591,349 | 10.5MB |
| `bibai-zone-f` | 美唄 東側（F） | 879,539 | 17.6MB |
| `bibai-zone-g` | 美唄 南東側（G） | 1,807,781 | 34.8MB |
| `bibai-zone-h` | 美唄 中央小室（H） | 1,196,592 | 22.6MB |
| `bibai-zone-rest` | 美唄 その他（rest） | 37,030 | 0.6MB |

どれも SPZ v4（zstd）・SH 3次。seeker-unity の UnityGaussianSplatting（2.0.0）が読める形。

## 1/2（スマホ向け）

点を半分・SH を 1次に落としたもの。wds-pgx のデータセット（`http://wds-pgx:8780/datasets/bibai/`）にあり、KaguyaAR（`~/workspace/bibai/ios/KaguyaAR`）が読んでいるものと同じ。**スマホではまずこちらを使う**。

| mapId | 名前 | 点の数 | 大きさ | 元のファイル |
|---|---|---:|---:|---|
| `bibai-all-half` | 美唄 全体（1/2） | 2,599,901 | 42.7MB | `export/bibai-v1_half.spz` |
| `bibai-west-half` | 美唄 西 A+B+C（1/2） | 1,090,220 | 18.0MB | `zones3/W_half.spz` |
| `bibai-center-half` | 美唄 中央 D+G+H（1/2） | 1,111,260 | 18.2MB | `zones3/C_half.spz` |
| `bibai-east-half` | 美唄 東 E+F（1/2） | 627,139 | 10.4MB | `zones3/E_half.spz` |
| `bibai-zone-a-half` | 美唄 西側（A・1/2） | 610,720 | 10.0MB | `zones_wide/A_half.spz` |
| `bibai-zone-b-half` | 美唄 南西側（B・1/2） | 346,280 | 5.8MB | `zones_wide/B_half.spz` |
| `bibai-zone-c-half` | 美唄 北西側（C・1/2） | 287,958 | 4.8MB | `zones_wide/C_half.spz` |
| `bibai-zone-d-half` | 美唄 中央北側（D・1/2） | 413,945 | 6.9MB | `zones_wide/D_half.spz` |
| `bibai-zone-e-half` | 美唄 北東側（E・1/2） | 295,675 | 4.8MB | `zones_wide/E_half.spz` |
| `bibai-zone-f-half` | 美唄 東側（F・1/2） | 439,770 | 7.3MB | `zones_wide/F_half.spz` |
| `bibai-zone-g-half` | 美唄 南東側（G・1/2） | 903,891 | 14.7MB | `zones_wide/G_half.spz` |
| `bibai-zone-h-half` | 美唄 中央小室（H・1/2） | 598,296 | 9.6MB | `zones_wide/H_half.spz` |

`rest` の 1/2 は無い（元が 0.6MB と小さい）。

## URL

SPZ は認証なしで取れる（GLB と同じ扱い）。`<環境>` は `dev`・`stg`・`a-kun-01`〜`03`・`so-ma-01`〜`03`。prod は前置きなし。

```
https://bibai.t0waxx.com/<環境>/v1/maps/<mapId>/splat.spz
https://bibai.t0waxx.com/v1/maps/<mapId>/splat.spz          # prod
```

例：`https://bibai.t0waxx.com/so-ma-01/v1/maps/bibai-zone-h/splat.spz`

- クライアントは `GET /v1/maps/{mapId}` の `splatUrl` を使うのがよい。末尾に `?v=…` が付き、差し替えると変わるのでキャッシュを外せる（上の URL は `?v=` なしでも同じものが返る）
- ゾーンのマップは SPZ だけなので、`modelUrl`（`model.glb`）は何も無いシーンを返す
- 置き方の `transform` は全マップ共通で `{"position":[0,0,0],"rotation":[1,0,0,0],"scale":[1,1,1]}`（`deploy/zones.sh` の `STAGE_TRANSFORM`）
  - ゾーンは Y が下向きのまま入っていて、無変換だと天井と床が逆に見える。`rotation` を `[1,0,0,0]`（X 軸まわり 180°）にすると直る（ゾーンH で実機確認）
  - マーカーの原点にどう重ねるか（位置・大きさ）はまだ決めていない。決まったら `STAGE_TRANSFORM` を直して `register all` で全環境に入れる。管理画面（`/<環境>/admin` のマップ）で1つの環境だけ直してもよいが、次の `register` で戻る
- 部屋のマップにするには、ホストが待機中に `PUT /v1/sessions/{id}/map`（`{"mapId":"bibai-zone-h"}`）を送るか、管理画面のセッション一覧で選ぶ

## スマホ向けに軽くする

点が100万を超えるゾーンはスマホで重い。上の 1/2 はもう登録してある。ほかの割合が要るときは、`deploy/spz_decimate.py` で薄くて小さい点から捨てた SPZ を作り、`stage-maps.tsv` に足して登録する。ゾーンH は半分（59.8万点・11.2MB）で見た目を保てた。

```sh
uv run --with numpy --with zstandard deploy/spz_decimate.py H.spz H-half.spz --keep 0.5          # 点を半分に
uv run --with numpy --with zstandard deploy/spz_decimate.py H.spz H-half.spz --keep 0.5 --sh 1   # SH も 1 次に落とす
```

## 登録し直す

マップは環境ごとの DB とストレージに入っている。`deploy/db.sh reset`・`copy` で DB を作り直すと、その環境のゾーンは消えるので登録し直す。

```sh
deploy/zones.sh list so-ma-01                    # その環境のステージのマップと URL
deploy/zones.sh register so-ma-01                # 1つの環境に stage-maps.tsv の全部を登録
deploy/zones.sh register all --yes-prod          # 全環境（prod を含むので --yes-prod が要る）
```

`register` は何度流してもよい。マップごとに次のどれかをする。

- まだ無い・SPZ の大きさが違う → SPZ ごと送る（新しいキーに置かれ、`splatUrl` の `?v=` が変わる）
- 名前か置き方だけ違う → 定義だけ直す
- 同じ → 何もしない

大きさが同じまま中身だけ変えたときは、管理画面から SPZ を差し替える。登録の処理（`deploy/stage_register.py`）は VM の上で動き、`ADMIN_TOKEN` を VM の外に出さない。

SPZ の置き場は VM の `/opt/bibai/incoming/` で、`stage-maps.tsv` の2列目はここからのパス。作り直したときは先に VM へ送る。

```sh
mkdir -p /tmp/half
rsync -a wds-pc-029-wsl:bibai-editor/zones_wide/ /tmp/zones_wide/
deploy/zones.sh push /tmp/zones_wide zones_wide          # → /opt/bibai/incoming/zones_wide/
# 1/2 は wds-pgx から HTTP で取り、パスの / を - にした名前（stage-maps.tsv の2列目）で half/ に置く
for p in export/bibai-v1_half zones3/{W,C,E}_half zones_wide/{A,B,C,D,E,F,G,H}_half; do
  curl -sf -o "/tmp/half/$(echo $p | tr / -).spz" "http://wds-pgx:8780/datasets/bibai/$p.spz"; done
deploy/zones.sh push /tmp/half half                      # → /opt/bibai/incoming/half/
deploy/zones.sh register all --yes-prod
```

マップを足す・消すときは `stage-maps.tsv` に行を足す（消すときは行を消したうえで、管理画面か `DELETE /v1/admin/maps/<mapId>` で消す。`register` は消さない）。

SPZ の受け付け条件（v1〜4・SH 0〜3・100MB まで）は `server/README.md` のマップの節を参照。
