## 概要

Refs #2（Unity 版の AR 設定調整は別ブランチで続行するため、issue はクローズしない）

マーカー(QR)で共有マップの原点を合わせ、端末ローカル⇔マップ座標を変換する Adapter を追加。
検出方式を WebXR `image-tracking`（Chrome フラグ必須・標準化停滞）から Unity AR Foundation（ARCore/ARKit）へ切り替える方針に変えたため、両方を入れている。

## 変更内容

- `seeker/`（three.js + WebXR）: 座標 Adapter と `image-tracking` による原点合わせ。vitest 10件。検証用として残す
- `seeker-unity/`（Unity 6000.3.22f1）: AR Foundation 6.7.1 + ARCore。
  - `CoordinateAdapter`: マップ(右手系)⇔Unity(左手系)を Z 反転で変換。EditMode テスト 9件
  - `SeekerArController`: `ARTrackedImageManager` でマーカーを読み、原点に 10cm キューブ。再スキャンボタン付き
  - `Editor/ProjectSetup.cs`: XR 設定・参照画像ライブラリ・URP の `ARBackgroundRendererFeature`・シーンを再現可能に生成

## 動作確認（AI検証済み）

- [x] `seeker`: `npm test` 10 passed / `npm run build` 成功
- [x] `seeker-unity`: EditMode テスト 9 passed
- [x] Android ビルド成功、Pixel 9a で起動し ARCore が参照画像1枚を読み込むことをログで確認

## ⚠️ 未解決・目視確認が必要

- [ ] **Unity 版で QR マーカーが検出されない**（実機で確認済み）。AR 側の設定が原因と見て別ブランチで対応する。QR は特徴点が少なく ARCore の画像認識に不向きな可能性が高い
- [ ] 検出できた後に、2台で同じ場所にキューブが見えること
- [ ] `ARTrackedImage` の +Y が床置きマーカーの法線（上向き）という仮定が実機で正しいか

## 関連

- issue #2: https://github.com/guriguri00451/BibaiHack/issues/2
