## 概要

マップに Gaussian Splat（SPZ）を持たせられるようにする。wds-pc-029 で作った美唄のゾーン（`zones_wide/A〜H.spz`、SPZ v4・SH 3次）をステージとして配るため。#27 で seeker-unity に入った UnityGaussianSplatting（2.0.0）は SPZ v1〜4 を実行時に読めるが、サーバーのマップは GLB しか受けていなかった。

## 変更内容

- **DB**：`maps` に `splat_key`・`splat_size` を足し、`model_key` を任意にした（`0002_maps_splat.sql`。起動時に自動で流れる）
- **API**（足しただけで、既存の形は変えていない）
  - `GET /v1/maps`・`GET /v1/maps/{mapId}` に `splatUrl` を足した。SPZ が無ければ `null`
  - `GET /v1/maps/{mapId}/splat.spz`：SPZ の本体。GLB と同じく認証なし、`?v=` で差し替えを見分ける
- **SPZ だけのマップ**：登録できる。`model.glb` は何も無いシーンの GLB を返す（GLB を読むクライアントが失敗しないように）
- **検査**：登録時にヘッダを読み、SPZ v1〜4（v1〜3 は gzip、v4 は生のヘッダ）・点が1つ以上・SH 次数 0〜3 だけを受ける。組み込みのマップ（`server/maps/*.json` の `splat`）も起動時に同じ検査をする
- **管理画面**：SPZ の登録・差し替え・外す。GLB が無いマップからは外せない
- **README**：マップの節に SPZ のことを書いた

## レビュー観点

- 上限は GLB と同じ 100MB。Cloudflare（無料プラン）が1リクエスト 100MB までなので、GLB と SPZ を同時に送るなら合計で 100MB まで。`zones_wide` で一番大きい `G.spz` は 35MB
- SPZ の Content-Type は決まったものが無いので `application/octet-stream`
- 実機（Pixel 9a）で UnityGaussianSplatting が動くかはまだ見ていない（README では mobile は未検証とされている）。クライアント側の対応は別

## 動作確認

- [x] `bun test` 120 passed、`tsc --noEmit` 通過
- [x] 本物の `zones_wide/*.spz` のヘッダを読めることを確認（v4・SH 3次・点の数が `zones.json` と一致）
- [ ] デプロイ後、管理画面の API から `zones_wide` を登録し、`splatUrl` から取れることを確認
