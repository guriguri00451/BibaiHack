## 概要

Closes #30

ケイ側（Unity）で、セッションのマップ（`payload.session.mapId`）をサーバーから読み込み、AR に置く。待機中・`finished` 中にマップが替わったら、再起動なしで置き直す。

issue では GLB だけが対象だったが、正式なゾーンのマップが SPZ（Gaussian Splat）だけで登録されたので、**SPZ の表示もこの PR に入れた**。そのために Android を Vulkan にしている（下記「⚠️ 影響の大きい変更」）。

## 変更内容

### マップの読み込みと切り替え（GLB・SPZ 共通）
- `MapService`: `SyncConnection.RestBaseUrl`（`/v1` 込み）と Bearer を付けて `GET /v1/maps/{mapId}` を叩く。接続情報が無いときだけ StreamingAssets のモックを使う。404 `MAP_NOT_FOUND` と通信失敗を分けて扱う
- `GameSessionState.ReadMapId` / `SyncedObjectsController.MapIdReceived`: snapshot の `mapId` を受けるたびに通知する
- `MapLoadTracker`（新規・純ロジック）: `mapId` / `modelUrl` / `splatUrl` が変わったかの判定と、最後の要求だけを反映するための世代管理
- `SeekerArController`: 固定の `mapId` / `apiBaseUrl` をやめて `fallbackMapId`（モック用。`FormerlySerializedAs("mapId")` で既存の値を引き継ぐ）にした。マップのエラーはマーカー状況とは別の枠に出す

### SPZ（Gaussian Splat）
- `MapDefinition` に `splatUrl` を足した。SPZ だけのマップも受け付ける
- `MapPresenter`: マップごとに GLB と SPZ（`GaussianSplatRenderer`）を持つ。新しいマップを読み終えてから差し替え、古いマップは glTFast の import ごと破棄する
  - `GaussianSplatRenderer` は無効にすると GPU のデータを捨て、もう一度有効にするとダウンロードからやり直す。そのため、原点が決まるまでは SPZ の読み込みを始めず、差し替えて破棄するまで無効にしない
- マップを差し替えたことと、SPZ の点の数・範囲をログに出す（管理画面で `transform` を合わせるときの手がかり）

### ⚠️ 影響の大きい変更
- **Android の描画 API を GLES3 から Vulkan に変えた**。Gaussian Splatting の並べ替えシェーダーが Wave 命令と DXC を必要とし、GLES では動かないため
- **minSdk を 26 から 29 に上げた**。ARCore 必須のアプリを Vulkan で動かすにはこれが必要（ビルド時に ARCore が弾く）。**Android 9 以下の端末には入らなくなる**
- URP の Renderer に `ARCommandBufferSupportRendererFeature`（Vulkan で ARCore を動かすのに必須。無いとカメラ映像が黒く、トラッキングも始まらない）と `GaussianSplatURPFeature` を足した。`ProjectSetup.AddRendererFeatures()` で足りないものだけ足す

### データ（deploy）
- `deploy/spz_decimate.py`: SPZ の点を間引いて SPZ v4 のまま書き出す。「不透明度 × 見かけの大きさ」が小さい点から捨て、残した点の値は変えない。SH の次数も下げられる
- `docs/stage-zones.md`: ゾーンの天地を直す `rotation` と、軽くする手順を書いた

## 動作確認

### AI 検証済み
- [x] EditMode テストが通る（132 passed / 0 failed）。足したテスト: `MapLoadTrackerTests`（mapId / modelUrl / splatUrl の変更判定、最後の要求だけ反映）、`ReadMapId`、`MapDefinition` の `splatUrl`
- [x] Vulkan で Development ビルドが通り、実機ではログで Vulkan で起動していることを確認した
- [x] `spz_decimate.py` の出力をもう一度読み直して、点の数・SH の次数・各ストリームの形が合っていることを確認した

### 実機で確認済み（Pixel 9a）
- [x] ローカルサーバーで登録した GLB マップを選ぶと、マーカーの位置に出る。マップを替えると、アプリを再起動せずに置き換わる
- [x] Vulkan でカメラ映像とマーカー追従が動く
- [x] `so-ma-01` でゾーンを替えると SPZ が読み込まれる（`rest` → `h`）。ゾーンH（119.7万点・22.6MB）は切り替えから約9秒で表示された
- [x] ゾーンH は `rotation [1,0,0,0]` で天井と床が正しい向きになる
- [x] 半分に間引いたゾーンH（59.8万点・11.2MB）で、見た目を保ったまま軽くなった

### ⚠️ 未確認（マージ前に見てほしい）
- [ ] 鏡写しになっていないか。SPZ は右手系のまま描いていて、GLB と違ってクライアント側で左手系に変換していない。部屋の文字などが左右反転していないかを見る
- [ ] 存在しない mapId のときに「マップ「…」がサーバーにありません」が出て、前のマップが残る（※以前「残った」と書いたのは、マーカーの目印のピンクの箱を見間違えていた）
- [ ] 同じ mapId で GLB / SPZ を差し替えたとき、次の snapshot で新しいものに替わる
- [ ] 接続情報が無いとき、今までどおりモックのマップが出る
- [ ] 原点が決まる前にマップが来て、あとから SPZ の読み込みに失敗した場合は、パッケージのログに出るだけで画面には出ない

## マージ後にやること
- 各環境のゾーンの `rotation` を `[1,0,0,0]` にする（管理画面。管理用トークンが要る）
- 重いゾーンを `deploy/spz_decimate.py` で間引いて差し替える

## 関連

- issue #30: https://github.com/guriguri00451/BibaiHack/issues/30
- サーバー側: #28（マップの登録・切り替え）、SPZ 対応（`splatUrl`）、ゾーン: `docs/stage-zones.md`

🤖 Generated with [Claude Code](https://claude.com/claude-code)
