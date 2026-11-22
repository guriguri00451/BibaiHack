## 概要

Closes #3

`GET /v1/maps/{mapId}` のマップ定義から美唄の3D(GLB)マップを読み込み、マーカーで合わせた共有マップ原点に表示する。

## 変更内容

- `MapDefinition`: マップ定義 JSON のパースと `MapTransform` への変換（EditMode テスト 6件）
- `MapService`: `UnityWebRequest` で取得。`apiBaseUrl` が空なら `StreamingAssets/mock/{mapId}.json` を読む（API 実装後は URL を入れるだけ）
- `MapPresenter`: glTFast で GLB を読み込み、`CoordinateAdapter.MapToLocal` で配置。マーカー再検出のたびに置き直し、原点が決まるまでは非表示
- `SeekerArController`: 上記を接続。`mapId` / `apiBaseUrl` を Inspector から変更可
- モック: `bibai-museum.json` と、地面(10m角)+柱3本の `bibai-museum.glb`
- `com.unity.cloud.gltfast 6.14.1` を追加

## レビュー観点

- **レスポンス形は仕様未定義**のため、`{mapId, name, modelUrl, transform{position,rotation,scale}}` の暫定スキーマにした（オブジェクト作成の transform に揃えた）。サーバー側と合わせる必要あり
- glTFast は GLB の頂点・階層を左手系へ変換済み。root の姿勢だけ `MapToLocal` で変換するので二重反転しない（`MapPresenter` にコメント）

## 動作確認（AI検証済み）

- [x] EditMode テスト 15 passed（既存9 + 追加6）
- [x] Android(IL2CPP) ビルド成功（41MB）。パッケージ解決・コンパイルも通過

## ⚠️ 目視確認が必要（人間がマージ前に実施）

> 実機(Pixel)で確認済みの項目にチェックを入れている。

- [x] マーカーを読んだ後、モックのマップ(地面+柱3本)がマーカー中心を原点に表示される
- [x] 柱の位置関係が左右反転していない（モック: 緑の柱1本が +X/+Z 側(2,2)、背の高い柱が -X 側(-2,1)、立方体が -Z 側(0,-3) ※マップ座標・右手系）
- [x] スケールが等倍（10m角の地面）
- [ ] 描画が重くない（FPS・発熱）。**実際の美唄マップ(GLB)のポリゴン数・テクスチャサイズは未計測**。モックは数十頂点で負荷の検証にならない
- [x] 実機で GLB が読める（Android の StreamingAssets は jar 内。`UnityWebRequest` 経由で読む実装）
- [x] マーカー未検出の問題（#2 の持ち越し）が解決していること。解決していないと原点が決まらずマップが出ない

## 関連

- issue #3: https://github.com/guriguri00451/BibaiHack/issues/3
- 依存: #2

🤖 Generated with [Claude Code](https://claude.com/claude-code) / dev-flow skill
