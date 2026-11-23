# BibaiHack

サブプロジェクト: `server/`（Bun + TypeScript）、`seeker-unity/`（ケイ側 AR クライアント、Unity）、`a-kun-unity/`（ドロ側）、`mock-server/`、`deploy/`。
`seeker/`（three.js + WebXR）は旧実装で、現行は `seeker-unity/`。

## seeker-unity

Unity 6000.3.22f1。コードは `Assets/Scripts/{Ar,Sync,App}`、テストは `Assets/Tests/EditMode`。
`Ar` / `Sync` は asmdef で分かれている（`SeekerAr` / `SeekerSync`）。UI や MonoBehaviour に依存しない判定・変換は純 C# にしてテストを書く。

### セットアップ

不要（初回の batchmode 実行時に Library が作られる）。**同じ `-projectPath` を2つの Unity で同時に開けない**ので、worktree ごとに1プロセスだけ。

### テスト・ビルド（`<wt>` はリポジトリルート）

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.3.22f1/Unity.app/Contents/MacOS/Unity

# EditMode テスト。結果は xml の total / failed を見る
$UNITY -batchmode -nographics -projectPath <wt>/seeker-unity -runTests -testPlatform EditMode \
  -testResults <wt>/seeker-unity/Logs/editmode.xml -logFile -

# Android 開発ビルド（コンパイルエラーの確認も兼ねる）
$UNITY -batchmode -quit -nographics -projectPath <wt>/seeker-unity \
  -executeMethod Seeker.Editor.AndroidBuilder.Build -logFile -
```

### 規約

- 接続情報は `SyncConnection` で受け取る。URL・トークンをコードやビルドに埋め込まない
- サーバーの payload の形は `server/src/contracts/ws.ts` が正。仕様書に無い形はクラスの summary に想定を書く
- 実機（Pixel 9a）の確認は人間がやる。ワーカーは EditMode テストとビルドまで
