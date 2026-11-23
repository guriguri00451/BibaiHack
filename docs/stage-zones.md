# ステージ：美唄のゾーン（Gaussian Splat）

美唄の会場を Gaussian Splat で撮ったものを、8つのゾーンと残り（`rest`）に分けた。各ゾーンはサーバーのマップとして、**全9環境**に登録してある。
元のファイルは wds-pc-029 の `~/bibai-editor/zones_wide/`。区分けは広め（隣のゾーンと少し重なる）。

## ゾーン

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

## URL

SPZ は認証なしで取れる（GLB と同じ扱い）。`<環境>` は `dev`・`stg`・`a-kun-01`〜`03`・`so-ma-01`〜`03`。prod は前置きなし。

```
https://bibai.t0waxx.com/<環境>/v1/maps/<mapId>/splat.spz
https://bibai.t0waxx.com/v1/maps/<mapId>/splat.spz          # prod
```

例：`https://bibai.t0waxx.com/so-ma-01/v1/maps/bibai-zone-h/splat.spz`

- クライアントは `GET /v1/maps/{mapId}` の `splatUrl` を使うのがよい。末尾に `?v=…` が付き、差し替えると変わるのでキャッシュを外せる（上の URL は `?v=` なしでも同じものが返る）
- ゾーンのマップは SPZ だけなので、`modelUrl`（`model.glb`）は何も無いシーンを返す
- 置き方の `transform` は無変換（原点・回転なし・大きさ 1）。マーカーの原点にどう重ねるかは、実機で見て管理画面（`/<環境>/admin` のマップ）から直す。環境ごとに別の値を持つ
  - 今のゾーンは Y が下向きのまま入っていて、無変換だと天井と床が逆に見える。`rotation` を `[1,0,0,0]`（X 軸まわり 180°）にすると直る（ゾーンH で実機確認）
- 部屋のマップにするには、ホストが待機中に `PUT /v1/sessions/{id}/map`（`{"mapId":"bibai-zone-h"}`）を送るか、管理画面のセッション一覧で選ぶ

## スマホ向けに軽くする

点が100万を超えるゾーンはスマホで重い。`deploy/spz_decimate.py` で、薄くて小さい点から捨てた SPZ を作り、管理画面から差し替える。ゾーンH は半分（59.8万点・11.2MB）で見た目を保てた。

```sh
uv run --with numpy --with zstandard deploy/spz_decimate.py H.spz H-half.spz --keep 0.5          # 点を半分に
uv run --with numpy --with zstandard deploy/spz_decimate.py H.spz H-half.spz --keep 0.5 --sh 1   # SH も 1 次に落とす
```

## 登録し直す

マップは環境ごとの DB とストレージに入っている。`deploy/db.sh reset`・`copy` で DB を作り直すと、その環境のゾーンは消えるので登録し直す。

```sh
deploy/zones.sh list so-ma-01                    # その環境のゾーンと URL
deploy/zones.sh register so-ma-01                # 1つの環境に登録（同じ大きさのものは飛ばす）
deploy/zones.sh register all --yes-prod          # 全環境（prod を含むので --yes-prod が要る）
```

ゾーンを作り直したときは、先に VM へ送る（VM の `/opt/bibai/incoming/zones_wide/`）。

```sh
rsync -a wds-pc-029-wsl:bibai-editor/zones_wide/ /tmp/zones_wide/
deploy/zones.sh push /tmp/zones_wide
deploy/zones.sh register all --yes-prod
```

大きさが変わったゾーンだけ差し替わる（新しいキーに置かれ、`splatUrl` の `?v=` が変わる）。大きさが同じまま中身だけ変えたときは、管理画面から SPZ を差し替える。

SPZ の受け付け条件（v1〜4・SH 0〜3・100MB まで）は `server/README.md` のマップの節を参照。
