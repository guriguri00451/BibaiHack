## 概要

Closes #5

`session.snapshot` / `session.phase_changed` の `status` に合わせて、ケイ側の画面（uGUI）をフェーズごとに出し分ける。

## 変更内容

- `Sync/GameSessionState.cs`（新規）: フェーズ・ラウンド・参加者・結果を持つ純ロジック。`round` が進んだら結果と参加者をリセットし、古いラウンドの `phase_changed` は無視する
- `App/PhasePanelsView.cs`（新規）: waiting / preparing / finished の全画面パネルと、playing の HUD を出し分ける
- `SyncedObjectsController`: `GameSessionState` を保持し `SessionChanged` を通知。playing 以外は同期オブジェクトを描画しない（preparing は何も描かない）
- `SeekerArController`: パネルを組み込み
- `mock-server`: `PHASE_CYCLE=1 npm start` でフェーズを巡回（finished 後に round++）。preparing 中は objects を空で返す

## 動作確認（AI検証済み）

- [x] Unity EditMode テスト グリーン（43 passed、うち `GameSessionStateTests` 10件）

## ⚠️ 目視確認が必要（人間がマージ前に実施）

- [ ] `cd mock-server && PHASE_CYCLE=1 npm start` を起動し、実機から接続して waiting → preparing → playing → finished と画面が切り替わる
- [ ] preparing 中は同期オブジェクトが何も出ない
- [ ] finished の次の round で waiting に戻り、前ラウンドの結果が残っていない
- [ ] playing でマーカー状況ラベルと AR 映像が見える

## 仕様の仮置き（§4 が repo に無いため）

payload の形は想定で実装しており、`GameSessionState` に閉じ込めている。実サーバーと要合意:

- `status` は `waiting|preparing|playing|finished`
- `round` はメッセージ直下を優先、無ければ payload
- `participants` は文字列配列、または `{name|displayName|userId}` の配列
- `result` は `{ captured: bool, score: int }`

## 関連

- issue #5: https://github.com/guriguri00451/BibaiHack/issues/5
