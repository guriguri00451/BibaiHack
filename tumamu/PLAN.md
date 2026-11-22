# PLAN — tumamu MVP

仕様は [docs/spec.md](./docs/spec.md)、思想は [docs/philosophy.md](./docs/philosophy.md)。統合用ブランチは `orch/tumamu-mvp`。

## 構成

```
tumamu/
  TumamuCore/            SwiftPM パッケージ。純粋なロジックだけを置き、RealityKit / ARKit には依存しない。macOS で `swift test` を回す
  TumamuApp/             visionOS アプリ。TumamuCore をローカルパッケージとして参照する
  project.yml            XcodeGen の定義。生成した .xcodeproj もコミットするので、実機担当は XcodeGen を入れなくてよい
  Config/Local.xcconfig  DEVELOPMENT_TEAM を書く。コミットしない（Local.xcconfig.example だけコミットする）
```

共通の確認コマンド:
- Core: `cd tumamu/TumamuCore && swift test`
- App: `cd tumamu && xcodebuild -project tumamu.xcodeproj -scheme TumamuApp -destination 'platform=visionOS Simulator,name=Apple Vision Pro' build`

## 依存関係

```
T-001 ─┬─ T-002 ─┐
       ├─ T-003 ─┴─ T-004 ─┐
       ├─ T-005 ─ T-006 ───┼─ T-010
       ├─ T-007 ───────────┤
       └─ T-008 ───────────┘
```

## タスク

### T-001 プロジェクトの雛形と共通の型
- 種類: logic
- やること: TumamuCore パッケージ（platforms は macOS 15 / visionOS 2）と、visionOS アプリを作る。アプリは起動すると `ImmersiveSpace(.mixed)` を開く。共通の型を Core に置く: `HandSample`（時刻、指先・親指先・手のひらの座標、トラッキング中かどうか）、`WallPlane`（原点と法線）、`MaterialKind`（wood / coal / marble）、`Parameters`（spec の定数表をそのまま定義する）。`project.yml`、`.gitignore`（`Local.xcconfig` と `DerivedData` を除外）、`Local.xcconfig.example`、Info.plist の2つの UsageDescription も用意する
- 触ってよいファイル: `tumamu/` の下（`docs/` と `PLAN.md` は除く）
- 完成の条件: `swift test` が通る（型ごとに最低1つテストを書く）。App のビルドコマンドが成功する
- 依存: なし
- 状態: pending

### T-002 接触判定とつまみ判定
- 種類: logic
- やること: `TouchDetector` を作る。指先と壁の距離が `TOUCH_DISTANCE` 未満なら接触とみなし、トラッキングが途切れても `TOUCH_GRACE` の間は接触を保つ。`PinchDetector` は `PINCH_DISTANCE` と `RELEASE_DISTANCE` のヒステリシスで判定する
- 触ってよいファイル: `TumamuCore/Sources/TumamuCore/Detection/{Touch,Pinch}*.swift` とそのテスト
- 完成の条件: `swift test` が通る。しきい値ちょうど・しきい値の前後・トラッキング途切れのテストを含める
- 依存: T-001
- 状態: pending

### T-003 層の決定と投げる速度
- 種類: logic
- やること: `LayerResolver` は、触れている時間から `MaterialKind` を返す。`ThrowVelocityEstimator` は、直近 `THROW_SAMPLE_FRAMES` 個のサンプルから平均速度を返す。サンプルが足りないときはゼロを返す
- 触ってよいファイル: `TumamuCore/Sources/TumamuCore/Detection/{Layer,Throw}*.swift` とそのテスト
- 完成の条件: `swift test` が通る。0秒・層の境界・サンプル数0と1のテストを含める
- 依存: T-001
- 状態: pending

### T-004 インタラクションの状態機械
- 種類: logic
- やること: `InteractionStateMachine` を作る。状態は idle → touching → pinching → pulling → holding → released で遷移し、`HandSample` と `WallPlane` を受け取ってイベント（`touchBegan` / `depthChanged` / `pieceSpawned` / `pieceDetached` / `thrown(velocity)` など）を出す。内部では T-002 と T-003 の部品を使う
- 触ってよいファイル: `TumamuCore/Sources/TumamuCore/Interaction/*` とそのテスト
- 完成の条件: `swift test` が通る。「触れる→つまむ→引き抜く→投げる」の一連の流れと、つまむ前に手を離したときの流れのテストを含める
- 依存: T-002, T-003
- 状態: pending

### T-005 シミュレータ用の入力と仮想の壁
- 種類: logic
- やること: App 側に `HandInputSource`、`WallProvider`、`CollisionProvider` の3つのプロトコルを定義する。シミュレータ用の実装として、`DragGesture` の `location3D` を `HandSample` に変換するもの（押さえている間はつまんでいる扱い）と、目の前1.2mに置いた仮想の壁と床の Entity を作る
- 触ってよいファイル: `TumamuApp/Input/Protocols/*`、`TumamuApp/Input/Simulator/*`
- 完成の条件: App のビルドが成功する。ドラッグを `HandSample` に変換する関数は純粋な関数にして Core に置き、`swift test` を通す（`TumamuCore/Sources/TumamuCore/Simulation/*` も触ってよい）
- 依存: T-001
- 状態: pending

### T-006 実機用の入力（ARKit）
- 種類: logic
- やること: `HandTrackingProvider` を使う `HandInputSource`、`PlaneDetectionProvider`（wall 分類）を使う `WallProvider`、`SceneReconstructionProvider` のメッシュから衝突形状を作る `CollisionProvider` を実装する。起動時に `isSupported` と許可の状態を見て、使えなければシミュレータ用の実装に切り替えるファクトリも作る
- 触ってよいファイル: `TumamuApp/Input/Device/*`、`TumamuApp/Input/InputFactory.swift`
- 完成の条件: App のビルドが成功する（動作は実機でしか確かめられないので、spec のチェックリストに回す）
- 依存: T-005（プロトコルを使うため）
- 状態: pending

### T-007 材質のかけらと物理
- 種類: logic
- やること: `MaterialPieceFactory` は、材質ごとに仮の見た目（生成したメッシュと SimpleMaterial）と物理パラメータ（質量・反発係数）を持つ Entity を作る。手の速度を受け取って投げる処理と、静止を検知したら kinematic に切り替えて `FLOAT_GAP` まで持ち上げる `FloatSystem` を作る。`MAX_PIECES` を超えたら古いものから消す。静止判定と、何個目から消すかの判定は Core の純粋な関数にする
- 触ってよいファイル: `TumamuApp/Pieces/*`、`TumamuCore/Sources/TumamuCore/Pieces/*` とそのテスト
- 完成の条件: `swift test` が通る。App のビルドが成功する
- 依存: T-001
- 状態: pending

### T-008 壁が応答する演出
- 種類: ui
- やること: 接触点から広がる波紋（コードで生成したリングで、深さに応じて色が変わる）、引き抜くときに伸びてちぎれる演出、壁に残る穴の痕を作る。ShaderGraph は使わず、コードだけで作る
- 触ってよいファイル: `TumamuApp/Effects/*`
- 完成の条件: App のビルドが成功する。開発用のプレビューシーンを用意して、シミュレータで演出を確かめられるようにする
- 見た目の条件: Apple Vision Pro シミュレータでのスクリーンショット（`xcrun simctl io booted screenshot`）
- 依存: T-001
- 状態: pending

### T-010 統合
- 種類: ui
- やること: `ImmersiveView` で、入力 → 状態機械 → 演出とかけら、の流れをつなぐ。シミュレータで「クリックして押さえる → 引っ張る → 離す」とすると材質が飛ぶところまで動かす
- 触ってよいファイル: `TumamuApp/ImmersiveView.swift`、`TumamuApp/App/*`
- 完成の条件: App のビルドが成功する。`swift test` が通る
- 見た目の条件: シミュレータで、3つの層の材質がそれぞれ投げられて浮いて静止していることをスクリーンショットで確認する
- 依存: T-004, T-005, T-006, T-007, T-008
- 状態: pending

## 保留（MVPの後）
- 音（素材が決まってから。T-010 の後に `SpatialAudioComponent` の差し込み口だけ作る）
