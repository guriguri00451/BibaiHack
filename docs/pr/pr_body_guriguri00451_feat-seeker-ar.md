## 概要

Closes #4

サーバー(WS)から受け取ったオブジェクトを AR 空間に表示し、Transform をなめらかに補間して追従させる（Unity）。

## 変更内容

- `seeker-unity/Assets/Scripts/Sync/`
  - `SeekerWsClient`: NativeWebSocket。接続 → `auth` 送信、切断時は指数バックオフで自動再接続（再接続のたびに snapshot で状態を作り直す）
  - `SyncedObjectStore` / `ServerMessage`: `session.snapshot` と `object.created/updated/deleted/transform` を適用し、変化(`SyncChange`)を返す純ロジック
  - `EventSequencer`: `(round, seq)` で古いイベントを破棄
  - `TransformSmoother`: 共有マップ座標で指数平滑（Lerp/Slerp、FPS非依存）。描画直前に `CoordinateAdapter.MapToLocal` でローカルへ変換
  - `GlbCache` / `IAssetLocator`: glTFast で GLB を読み込み、`assetKey` 単位でキャッシュ（進行中ロードも共有）
  - `SyncedObjectsController`: 上記の橋渡し。マーカー未検出の間は非表示。`SeekerArController` が実行時に追加
- `mock-server/`: キューブ3つが周回する簡易モック（WS 15Hz + 署名URL取得 + GLB配信）
- `AndroidBuilder`: glTFast のシェーダーを Always Included Shaders に追加（無いと実機でマテリアルエラー）、開発用に HTTP を許可
- パッケージ追加: glTFast 6.21.0 / NativeWebSocket / Newtonsoft.Json 3.0.2

## 動作確認

- [x] EditMode テスト 27 passed（うち今回分 18）
- [x] Pixel 9a + モックサーバーで、キューブがなめらかに動くことを目視確認

## 注意・仮置き

- payload の形（snapshot / created / updated）は仕様 §6 に記載が無いため仮定。`ServerMessage` / `SyncedObjectStore` に閉じ込めてある。`assetKey` と `assetUrl` の両方を受ける
- 署名付きDL URL の取得エンドポイント（現状 `GET /assets/download-url?key=` → `{"url"}`）は未合意。`RestAssetLocator` に `TODO(soma)` あり
- `insecureHttpOption` を Always Allowed にしている（モックが http のため）。**リリースビルド前に戻すこと**
- GLB キャッシュはメモリのみ（ディスクには残さない）
- `ProjectSetup.cs` が存在しない `cube` プロパティを参照していて既にずれている（本PRでは未対応）
