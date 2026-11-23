# a-kun-unity: 粘土でモデルを作って投稿する (Android XR アプリ)

[a-kun](../a-kun/) (WebXR 版) を Unity で作り直したものです。中身 (粘土の計算、手の触り方、パネル、投稿) は Web 版と同じで、Android XR (Galaxy XR など) のアプリとして動きます。

- Unity **6000.3.22f1** (URP、OpenXR、Android XR)
- パッケージ: Unity OpenXR: Android XR 1.5.1、XR Hands、AR Foundation (パススルー)、Input System、glTFast 6.21.0 (マップの読み込み)
- 仕様: [docs/whiteboard-brainstorm.md](../docs/whiteboard-brainstorm.md)

## ファイル

| ファイル | 中身 |
|---|---|
| `Assets/AKun/Scripts/ClayField.cs` | 粘土の距離場 (80³) と色。押す・なでる・引っ張る・塗る、Surface Nets のメッシュ化 (`a-kun/src/sdf.js` の移植) |
| `Assets/AKun/Scripts/ClayApp.cs` | 本体。カメラとパススルー、手での造形、つまむ・移動、パネル、ログの板、投稿用の書き出し (`a-kun/src/main.js` の移植)。音と演出の呼び出しもここ |
| `Assets/AKun/Scripts/Sfx.cs` | 効果音と BGM。素材ファイルは使わず、起動時にコードで波形を作る |
| `Assets/AKun/Scripts/Fx.cs` | 演出: 粒 (はじける・きらきら)、広がる輪、触れている所の輪、動き (Tween) |
| `Assets/AKun/Scripts/Hud.cs` | 「今やること」の帯、残り時間の輪、通知 |
| `Assets/AKun/Scripts/WristMenu.cs` | 手首メニュー (左手のひらを上に向けると出る) |
| `Assets/AKun/Scripts/Tutorial.cs` | お手本の手 (なでる・こねる・つまむを見せる) |
| `Assets/AKun/Scripts/Hands.cs` | 手の関節・骨、震えを抑えるフィルタ、半透明の手の描画 |
| `Assets/AKun/Scripts/ButtonPanel.cs` | 空中のボタン・スライダー・色見本。指先で押す / レイで指してつまむ (`a-kun/src/panel.js` の移植) |
| `Assets/AKun/Scripts/Post.cs` / `Uploader.cs` | 投稿のパネルと投稿先 (この端末内 / ゲームサーバー) (`a-kun/src/post/` の移植) |
| `Assets/AKun/Scripts/Glb.cs` | GLB の書き出し |
| `Assets/AKun/Scripts/StagePicker.cs` | ステージを選ぶパネル。サーバーのステージ一覧から選び、そのモデルを取りにいく (隠す場所を選ぶ前の段階) |
| `Assets/AKun/Scripts/MapView.cs` | 隠す場所を選ぶマップの模型。マップ定義と GLB の読み込み、レイの当たり判定、隠す場所の Transform (seeker-unity の `MapService` / `MapDefinition` / `MapPresenter` をもとにした) |
| `Assets/StreamingAssets/mock/` | マップのモック (seeker-unity と同じ `bibai-museum`) |
| `Assets/AKun/Resources/Shaders/` | 粘土 (頂点カラー)、半透明の手、パネルの角丸、文字 |
| `Assets/AKun/Editor/ProjectSetup.cs` | プロジェクトの設定 (Player、URP、XR、シーン) と APK のビルド |
| `Assets/AKun/Editor/ClayTests.cs` | 粘土の計算の確認 |

シーン (`Assets/AKun/Scenes/Main.unity`) には `ClayApp` を 1 つ置くだけで、カメラ・パススルー・粘土・パネルは実行時にコードで組み立てます。

## ビルドして実機に入れる

`ProjectSetup` が設定をすべて行うので、エディタで手作業の設定は要りません。

1. Unity Hub で 6000.3.22f1 を Android Build Support (SDK・NDK・OpenJDK 付き) と一緒に入れる
2. このフォルダをプロジェクトとして開き、メニューの **AKun > Build APK** を選ぶ (コマンドラインなら下のとおり)
3. ヘッドセットを USB でつないで入れる

```bash
/Applications/Unity/Hub/Editor/6000.3.22f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath a-kun-unity -executeMethod AKun.EditorTools.ProjectSetup.BuildApk -logFile -
```

```bash
adb install -r a-kun-unity/Builds/a-kun-clay.apk
```

初めて起動したときに「手の追跡」の許可を求められるので、許可してください。

設定 (`ProjectSettings/`、`Assets/XR/`、`Assets/AKun/Settings/`、シーン) はコミット済みなので、そのままビルドできます。
`ProjectSetup.Setup` で入力方式 (Input System) を切り替えたときは、エディタを起動し直してからビルドしてください (同じ起動のままだと「script class layout is incompatible」で失敗する)。

`ProjectSetup.Setup` がすること:

- Player: Android、IL2CPP / ARM64、Vulkan、Linear、最小 API 29、GameActivity、Resizeable Activity、Run In Background、Input System
- URP: `Assets/AKun/Settings/` に URP の設定を作る (HDR なし、ポストエフェクトなし、MSAA 4x)
- XR: Android で OpenXR、機能は Android XR Support / Session / Camera (パススルー) / Hand Tracking / Hand Interaction Profile。描き方は Single Pass Instanced
- シーンを作ってビルド設定に入れる

## 使い方

起動すると、頭の高さで目の前 (約 45cm 先) に粘土の球 (直径 約 42cm) とパネルが出ます。操作は Web 版と同じです。

- さわる (手のひらで触れる): 押し込みの深さで自動で切り替わる。軽く触れる=なでる、深く押す=こねる (「こねる開始」の初期値 15mm)。
  触れている所に輪が出て、なでる間は水色、こねる間は橙色になる (手の色も同じ向きに変わる)
- 塗る: 人差し指の先で。左のパネルの色見本か、手首メニューで色を選ぶ
- 粘土の表面をつまむ: 引っ張る (体積は増えない)。粘土のそば (6cm 以内) では粘土ごとは動かない。離れた所をつまむと移動、両手で回転・拡大縮小
- ボタン: 手から伸びるレイで指してつまむとクリック (OS と同じ)。指先で直接押してもよい
- 左の道具のパネル: モード、ブラシの大きさ (スライダー。動かしている間はパネルの上に実物大の球が出る)、戻す・手元へ・使い方、色見本。
  「リセット」は下の段に離して置き、3 秒以内に 2 回押すと球に戻る
- 手首メニュー: 左手のひらを上 (自分の方) に向けると、手のひらの上に出る。モード・色・戻すと残り時間。右手で押す (エディタでは M キー)
- 使い方: 起動して粘土が目の前に来たら、白い半透明の「お手本の手」がなでる・こねる・つまむを 1 回ずつ見せる。「使い方」でもう一度見られる
- 粘土の上 (奥) のボタン: 投稿する / 更新 / 化ける / 新しく作る
- 作業台の上の帯「今やること」: 工程 (つくる → 隠す → 化ける → 待つ) と、今やることを 1 行で出す。サーバーにつないでいる間は、右に残り時間の輪が出る (30 秒を切ると黄色、10 秒を切ると赤で点滅して秒読みの音)
- 通知: 「隠しました」「化けました」などは、頭の前 (少し下) に数秒だけ出る
- 背中側のパネル「粘土の性質」: スタッフが調整する項目 (柔らかさ、効果音と BGM の音量など。端末に保存される)。その上の板に最近のログが出る

### 音と演出

音の素材ファイルは使わず、起動したときに `Sfx.cs` が波形を作ります。

- 手の音: なでる (すりすり)、こねる (むにゅ)、つまむ (むにっ)、伸ばす (みょーん。伸ばすほど高くなる)、離す (ぷにっ)、塗る (筆の音)。手の速さと押し込みの深さで音量が変わる
- パネル: 押す、指す、スライダーの目盛り
- 節目: 隠した (ぽんっ・どすん)、化けた (きらきら)、戻す、リセット (ぼよん)、準備開始・本番開始・終了のジングル、残り 10 秒の秒読み、見つかった (サイレン)
- BGM: 作る・待つ間はゆったり、本番は刻むベースで緊張感を出す
- 演出: 触れた所の波紋、こねたときの粘土くず、塗ったときのしぶき、「投稿する」で粘土がマップに吸い込まれてマップが膨らんで出る、「やめる」「ここに隠す」のあとで粘土がぽんと戻る

### 隠す場所を選ぶ

「投稿する」を押すと、粘土が縮みながらマップの方へ吸い込まれ、作業台の上にマップの模型 (最初は横幅 50cm) が膨らんで出ます。道具のパネルはしまわれます。

- レイでマップを指してつまむ: そこに隠す。粘土がマップと同じ縮尺で置かれる (底面を面に付け、上を面の向きに、正面を自分の方に向ける)。つまんだまま動かすと付いてくる
- 何もない所をつまむ: マップを動かす。両手でつまむと回す・大きさを変える (実物大まで)
- 「ここに隠す」: GLB を上げて、隠す場所の Transform でオブジェクトを作る (2 回目からは更新)。「やめる」で粘土に戻る
- 「マップを手元へ」: 頭の前に持ってきて、最初の大きさに戻す
- 2 回目からは、前に隠した場所を選んだ状態で始まる
- エディタで再生したときは、マウスでマップを押すとそこに隠す

隠す場所を選ぶ前に、**ステージ (マップ) を選んで、そのモデルを取ってきておく**段階があります (サーバーにつないでいるとき)。「ゲームサーバーにつなぐ」の 1 を見てください。
「投稿する」は、取ってあるモデルの中で隠す場所を選ぶ画面を出します (モデルをまだ取っている途中なら、終わるまで待つ。取れていなければ、ここでもう一度取りにいく)。

サーバーにつながないときは、起動時の設定の `map` (既定は `bibai-museum`) で選び、`Assets/StreamingAssets/mock/<map>.json` を読みます (サーバーにつないでいても、`GET /v1/maps/<map>` が使えなければこの mock を使う)。
マップの模型の置き方は `MapView.SetPose(位置, 回転, 倍率)` で外から決められます (マーカーの位置や、保存しておいた置き方など)。
マップの材質は粘土と同じシェーダーで、glTF の基本色だけを出します (テクスチャはまだ出さない)。

## 投稿するもの

Web 版と同じです ([a-kun/README.md](../a-kun/README.md#投稿するもの))。

- GLB 1 ファイル (`POSITION` / `NORMAL` / `COLOR_0`、メートル、Y 上、原点は底面の中心)。体積が 0.0027 m³ になるように大きさをそろえる
- Transform は `position` / `rotation` / `scale` ([1,1,1])。マップの上で選んだ隠す場所を、マップ座標 (右手系・Y 上・メートル、仕様 3.2) で送る。マップの模型をどう動かしても同じ値になる
- 投稿した GLB は端末にも保存される: `/sdcard/Android/data/com.bibaihack.akunclay/files/posts/clay-<n>-v<版>.glb`

```bash
adb pull /sdcard/Android/data/com.bibaihack.akunclay/files/posts/
```

### ゲームサーバーにつなぐ

起動するときに Intent の extra で渡すか、端末の `files/config.json` に置きます。

```bash
adb shell am start -n com.bibaihack.akunclay/com.unity3d.player.UnityPlayerGameActivity --es server https://bibai.t0waxx.com/a-kun-01 --es name a-kun
```

```json
{"server": "https://bibai.t0waxx.com/a-kun-01", "room": "", "name": "a-kun", "prepare": "120", "play": "180", "result": "20"}
```

| 項目 | 内容 |
|---|---|
| `server` | 接続先 ([`deploy/README.md`](../deploy/README.md) の環境の URL)。`wss://…/v1/ws` でもよい。無ければ投稿は「この端末内」 |
| `room` | 入るセッションの ID。空なら部屋を作ってホストになる (ID はログと `files/session.txt` に出る。ケイ側はこれで参加) |
| `prepare` / `play` | 準備・本番の秒数 (既定 120 / 180)。ホストのときだけ使う |
| `result` | 結果を見せる秒数 (既定 20)。過ぎるとサーバーが次のラウンドを同じ役割で始める。`0` ならホストが「次のラウンドへ」を押す |
| `name` / `map` | 名前 / 部屋を作るときのマップ (既定 `bibai-museum`)。あとでゲームのパネルで替えられる |

つなぐと右にゲームのパネルが出ます (ラウンド・フェーズ・残り時間・役割・参加人数)。流れは仕様書 4 章のとおりです。

1. ステージを選ぶ。右のゲームのパネルの「ステージ: … ▶ 選ぶ」を押すと、ステージの一覧のパネルが開く (開くたびに `GET /v1/maps` でサーバーから取り直すので、管理画面で足したステージもすぐ出る。4 件ずつのページ送り)。
   ホスト (部屋を作った端末) は、待機中とラウンドの終わった後に、一覧から選べる (`PUT /v1/sessions/{id}/map`。参加者全員には snapshot の `session.mapId` で知らされる)。
   選んだらすぐ、そのステージのモデル (`GET /v1/maps/{mapId}` → GLB) を取りにいく。部屋につないだとき・ほかの端末が替えたときも同じ。
   取れたかどうかは、パネルの欄 (取得中… / 取得済み / 取得できません) と「今やること」の帯に出る。取れなかったときは、そのステージを押すと取り直せる。
   ステージはサーバーの管理画面 (`<接続先>/admin`) で登録する
2. ホストが「準備をはじめる」。この端末がドロ、ほかの全員がケイになる
3. 準備中と本番の間、ドロは投稿・更新できる。ラウンドの最初の投稿に自動で化ける (「化ける」で切り替え)
4. 期限が来るとサーバーが次へ進める (「本番をはじめる」「ラウンドを終える」で早めることもできる)。全てのドロが見つかっても終わる
5. 終了したら、`result` 秒の後にサーバーが次のラウンドを始める (パネルに「次まで」の残り時間が出る)。その間もマップを替えられ、「次のラウンドへ」ですぐに始めることもできる。前のラウンドの投稿の一覧は空になる (粘土は残る)

使う API は `Assets/AKun/Scripts/Uploader.cs` の `ServerTarget` の先頭にまとめてあります。
サーバーにまだ無い API (404 `NOT_FOUND` / 405 / 501) は、ログに `[未実装]` と 1 回だけ出して、この端末の中で済ませます (フェーズもこの端末で進める)。
ゲームの流れは止まりません。WebSocket が切れたら自動でつなぎ直します。

確認 (エディタを閉じてから):

```bash
Unity -batchmode -quit -projectPath a-kun-unity -executeMethod AKun.EditorTools.GameTests.Run
Unity -batchmode -projectPath a-kun-unity -executeMethod AKun.EditorTools.GameTests.RunLive -akunServer https://bibai.t0waxx.com/a-kun-01
```

`RunLive` は本物のサーバーで、部屋を作る → もう 1 人が入る → ステージを替える (2 つ以上あるとき) → 選んだステージのモデルを取る → 準備 → 投稿・擬態 → 本番 → 終了 → サーバーが次のラウンドを始める、を一通り回します。

## 調整しやすい値 (`ClayApp.cs` の先頭)

- `CUBE_SCALE` / `START_R`: 最初の球の大きさ (半径 8.65cm を 2.45 倍)
- `PINCH_ON` / `PINCH_OFF` / `PRE_PINCH`: つまむ判定の指の距離
- `PALETTE`: 色見本の 12 色
- `NO_MOVE_NEAR`: 粘土のそばでつまんでも粘土ごと動かさない距離
- `RECENTER_FORWARD` / `RECENTER_DROP`: 起動したとき・「手元へ」で置く位置
- `limit*` / `visc*`: 押し込みの制限と粘性 (右のパネルのスライダーと同じもの)

## Web 版とのちがい

- 画面 (マウス) での造形はない。エディタで再生すると、粘土とパネルを眺めてマウスでボタンを押せるだけ
- 投稿の一覧 (作り直す・取消・GLB の保存) と、サーバーの接続フォームはない。サーバーと部屋は起動時の設定で渡す
- 手のレイは OpenXR の Hand Interaction Profile の aim pose を使う。取れないときは、肩のあたりから指の付け根を通るレイにする
