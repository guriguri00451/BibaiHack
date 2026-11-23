## 概要

Closes #16

ケイ（スマホ）の同期処理の接続先を、モックから実サーバーに移行する。サーバーにはオブジェクト同期が無かったので、仕様書 §5・§6 に沿って足した（`server/` と `seeker-unity/` の両方を触る）。

## 変更内容

### サーバー（`server/`）

- REST: `POST /v1/sessions/{id}/objects`、`PATCH` / `DELETE /v1/sessions/{id}/objects/{objectId}`（所有者のみ）、`POST /v1/assets/uploads`（S3互換の署名付きPUT URL。5分有効・GLBのみ・20MBまで）
- WS: `session.snapshot` に `objects` を追加。`object.created` / `object.updated` / `object.deleted` の配信と、所有者からの `object.transform` の中継
- 見せ方のルール（§6.2）: 所有者・擬態フラグは載せない。準備中のケイには `objects` を空にして `object.*` も送らない。ケイはオブジェクトを編集できない（403）
- payload は `{objectId, assetKey, assetUrl, transform}`。`assetUrl` は1時間有効の署名付きGET URL（S3未設定の環境では付かない）
- WS 接続の管理を `Connections` に切り出し、REST の変更も WS に配れるようにした
- `server/scripts/dev-local.ts`: S3 の代わりに GLB を自前で配り、オブジェクトを動かす実機確認用サーバー

### Unity（`seeker-unity/`）

- 接続情報を `SyncConnection` で受け取る（`Configure(adapter, connection)`）。`wsUrl` / `restBaseUrl` / `sessionId` / `accessToken` のハードコードを廃止。`FromBaseUrl("https://…/so-ma-01", …)` で wss / https を組み立てる
- GLB は payload の `assetUrl` から直接取得する（署名URL解決の `RestAssetLocator` を削除）。キャッシュと差し替え判定は `assetKey`
- サーバーに拒否された（認証失敗など）場合は再接続せず、画面に表示する
- `AndroidBuilder.BuildRelease()` を追加（平文HTTP禁止）。`Build()` は開発ビルドになり、`DevConnectionFile` が `persistentDataPath/dev-connection.json` を読んで同期を始める（トークンをビルドに埋め込まない）
- `mock-server` も `assetUrl` を返す形に合わせた

## レビュー観点

- **仕様に明記が無い点**: `object.created` / `updated` の payload と `snapshot.objects` の形（上記）。§6.3 は `assetUrl` だけだが、署名URLは毎回変わるためキャッシュキーに使えず、`assetKey` も併せて送っている。仕様書への追記が要る
- **`assetUrl` の期限**: 1時間。切れたら再接続（snapshot）か `object.updated` で新しいURLが届く。取得に失敗したあと、同じキーのままでは再試行の契機が無い
- 実装していない仕様: アップロード済みの確認（存在・サイズ）、擬態切替、ラウンド開始・終了 API。役割は DB の `role` を見るだけ
- 変更が約930行で目安の800行を超えている。サーバーと Unity は別コミットに分けてある

## デプロイ前に要ること（チーム側）

- `deploy/render.sh` が `S3_ENDPOINT` / `S3_BUCKET` / `S3_ACCESS_KEY_ID` / `S3_SECRET_ACCESS_KEY` をサーバーに渡していない。渡して VM の `.env` に値を入れるまで、`assetUrl` は付かず `POST /v1/assets/uploads` は 503
- GLB の置き場（R2 等）のバケット

## 動作確認

- [x] サーバー: `bun test` 81 passed、`tsc --noEmit` 通過
- [x] Unity: EditMode テスト 40 passed
- [x] Android(IL2CPP) 開発ビルド成功
- [x] 実機(Pixel 9a)で確認済み: ローカルの `dev-local.ts` に WebSocket で接続し、サーバーから配信されたオブジェクトが表示・追従する
- [ ] 実サーバー（`so-ma-01`）での確認: S3 とデプロイ待ち
- [ ] リリースビルド（`BuildRelease`、平文HTTP不許可）での `https` / `wss` 接続: 実サーバー待ち

## 関連

- issue #16: https://github.com/guriguri00451/BibaiHack/issues/16
- 依存: #15
