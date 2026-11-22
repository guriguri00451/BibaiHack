## 概要

Closes #1

ケイ（スマホ）側のAR土台。Android ChromeでWebXR ARセッションを起動し、hit-testによる平面検出とレティクル追従までを実装。

## 変更内容

- `seeker/`: Vite + three.js の雛形（`package.json`, `index.html`, `src/main.js`, `README.md`）
- ARを開始ボタン → `immersive-ar`（`hit-test` 必須）セッション開始
- hit-test結果にレティクルを追従、検出状態をステータス表示
- `navigator.xr` 未対応 / `immersive-ar` 非対応（iOSなど）は「非対応」表示（iPhone対応は別Issue）
- 参照空間は three のデフォルト `local-floor` だと実機で失敗するため `local` に変更
- 起動失敗時にセッションを終了し、原因表示を上書きしない
- `.gitignore` に `node_modules/` `dist/` を追加

## 動作確認（AI検証済み）

- [x] `npm run build` 成功（チャンクサイズ警告のみ）
- [x] Pixel 9a / Android Chrome（USB + `adb reverse`）で、AR起動→机上にレティクル表示・「平面を検出しました」表示をスクリーンショットで確認

## ⚠️ 目視確認が必要（人間がマージ前に実施）

> AIは実機ARを検証できていません。

- [ ] 端末を動かし続けてもレティクルが追従する（静止画でしか確認できていない）
- [ ] iOS Safari / デスクトップChromeで「非対応」と表示される
- 起動手順は `seeker/README.md`（`adb reverse tcp:5173 tcp:5173` → `http://localhost:5173`）

## 関連

- issue #1: https://github.com/guriguri00451/BibaiHack/issues/1
- `docs/whiteboard-brainstorm.md` §8

🤖 Generated with [Claude Code](https://claude.com/claude-code) / dev-flow skill
