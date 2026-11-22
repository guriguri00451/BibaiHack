# a-kun: 粘土でモデルを作って投稿する (めっちゃ美唄)

「めっちゃ美唄」でドロ (隠れる側) が使う、モデリングから投稿までの部分です。
立方体の粘土を手で押す・盛る・なでる・つまんで引っ張って形を作り、色を塗って、ゲームに投稿します。
Android XR の Chrome (WebXR + ハンドトラッキング) を想定しています。Mac やスマホの画面でもマウスやタッチで操作できます。

- 公開ページ: https://guriguri00451.github.io/BibaiHack/a-kun/ (GitHub Pages を有効にすると見られる)
- 仕様: [docs/whiteboard-brainstorm.md](../docs/whiteboard-brainstorm.md)
- もとのリポジトリ: [fukuda-A-HU/clay-xr](https://github.com/fukuda-A-HU/clay-xr) (471eec7 から移した)

## ファイル

| ファイル | 中身 |
|---|---|
| `index.html` | 画面の UI と three.js の読み込み (CDN, r170) |
| `src/sdf.js` | 粘土の距離場 (80³ ボクセル) と格子点ごとの色。押す・盛る・なでる・引っ張る・塗る、Surface Nets によるメッシュ化 (頂点カラー付き) |
| `src/main.js` | シーン、マウス操作、XR のハンド入力、移動・回転・拡大、Undo、GLB 書き出し |
| `src/panel.js` | XR 空間に浮かぶボタン・スライダー・色見本 |
| `src/post/uploader.js` | 投稿先 (このブラウザ内 / ゲームサーバー) |
| `src/post/ui.js` | 投稿のパネル (画面) と、XR の中の投稿ボタン |

## 動かす

リポジトリのいちばん上でサーバーを起動します (GitHub Pages と同じ `/a-kun/` のパスで開けます)。

```bash
python3 -m http.server 8080
```

ブラウザで http://localhost:8080/a-kun/ を開きます。

Android XR の実機 (Galaxy XR など) では、USB でつないでポートを転送し、ヘッドセットの Chrome で同じ URL を開きます。WebXR は HTTPS か localhost でしか動きません。

```bash
adb reverse tcp:8080 tcp:8080
```

## 使い方

1. 形を作る: 道具 (こねる・盛る・なでる・つまむ) で粘土を変形する
2. 色を付ける: 「塗る」を選び、色見本 (または「ほかの色」) の色で表面をなぞる。「盛る」も選んだ色の粘土を足す
3. 投稿する: 右の「投稿」パネルの「投稿する」を押す
   - 投稿したあとも粘土はそのまま残る。直してから「#n を更新して投稿」で新しい版を上げられる
   - 「新しく作る」で立方体に戻すと、次は別のモデルとして投稿される
   - 一覧の「作り直す」で、投稿したときの形と色を粘土に読み込める (このタブで投稿したものだけ)

画面での操作: 粘土の上を左ドラッグで造形、何もない所を左ドラッグで視点を回す、右ドラッグで平行移動、ホイールでズーム。<kbd>1</kbd>〜<kbd>5</kbd> で道具、<kbd>[</kbd> <kbd>]</kbd> でブラシの大きさ、<kbd>⌘Z</kbd> で戻す。

XR での操作:

- 手のひらで触れる: こねる / なでる (左のパネルで切り替え)。押し込むと見えている手は表面近くで止まり、押し続けると粘土がゆっくり沈む
- 盛る・塗る: 人差し指の先で。左のパネルの下の色見本で色を選ぶ
- 粘土の表面をつまむ: 引っ張る。何もない所をつまむ: 粘土を移動、両手でつまむと回転・拡大縮小
- 粘土の上 (奥) のボタン: 投稿する / 更新 / 新しく作る。XR では、粘土を置いた位置・向き・大きさがそのまま投稿の Transform になる
- 右のパネル「粘土の性質」: 柔らかさ・沈む速さなどを指でなぞって調整 (ブラウザに保存される)

## 投稿するもの

投稿は仕様 8 の「Sculpt確定 → Object Storage + PostgreSQL」にあたります。

- アセット: GLB (glTF バイナリ) 1 ファイル。メッシュ 1 つで、属性は `POSITION` / `NORMAL` / `COLOR_0` (頂点カラー、リニア)。単位はメートル、Y 上、原点は底面の中心。1 つ 200〜300KB くらい
- Transform: 仕様 3.5 の形 (`position` / `rotation` (クォータニオン x, y, z, w) / `scale`)。位置は粘土の底面の中心
- 版: 更新するたびに新しいアセットを上げ、新しい `asset_url` をオブジェクトに付け替える (前の版は上書きしない。仕様 3.5)

### 投稿先

- **このブラウザ内** (最初はこれ): サーバーがまだ無いので、投稿はこのページの中にだけ置かれます。読み込み直すと消えます。一覧の「GLB」で書き出せます
- **ゲームサーバー**: 「投稿」パネルの「投稿先」に名前・部屋のコード・WebSocket の URL を入れて接続します。URL に `?server=wss://…&room=ABCD` を付けて開くと入力済みになります

### サーバーと合わせること

サーバーはまだ無いので、クライアントからの送り方は次のように仮に決めています (`src/post/uploader.js`)。サーバーを作るときに、ここを合わせるか、こちらを直してください。

| やること | 送るもの | 返ってくるもの |
|---|---|---|
| 部屋に入る | WebSocket で `session.join { code, name }` | `ack { sessionId, playerId, token }` |
| GLB を上げる | `POST {サーバーの https://ホスト}/assets` (本文 = GLB, `Content-Type: model/gltf-binary`) | `{ assetUrl }` (期限付きでない永続的な参照) |
| 投稿 | `object.create { assetUrl, transform }` | `ack { objectId }` (サーバーが `object.created` を配る) |
| 更新 | `object.update { objectId, assetUrl }` と `object.transform { objectId, transform }` | `ack` |
| 取り消し | `object.delete { objectId }` | `ack` |

- メッセージは JSON の `{ type, payload, requestId }`。返事は `{ type: "ack" | "error", payload: { requestId, ... } }` (`error` は `code` と `message` を付ける)
- 投稿できるのが「現在のラウンドのドロだけ」などの判定はサーバー側でする想定 (仕様 3.5, 9)。断られたときは `error` の `message` をそのまま画面に出す

### まだやっていないこと

- スカルプト中の差分のリアルタイム同期 (仕様 4.5 の「Sculpt: 編集差分を送信」)。いまは投稿 (確定) のときだけ送る
- 配置・擬態・本番などのゲームの進行 (ほかの担当)
- 認証

## 調整しやすい値 (`src/main.js` の先頭)

- `PINCH_ON` / `PINCH_OFF`: ピンチと判定する親指と人差し指の距離 (2cm / 4cm)
- `PRE_PINCH`: これより指が閉じていると指先の道具を止める (5cm)
- `PALETTE`: 色見本の 12 色
- `grabR`: 引っ張るときにつかむ範囲 (ブラシ半径 × 2.2)
- `OneEuro3(minCutoff, beta)`: 手の震えを抑えるフィルタ
- `ClayField({ N: 80, size: 0.4 })`: 格子の細かさと造形できる範囲 (40cm 角)。色の細かさも格子と同じ (約 5mm)
- `LIMIT`: 押し込みの制限。見えている手がめり込める深さ (`depth`) と、押し込み過ぎた分で粘土が流れる速さ (`pressure`)
- `VISC`: 粘性。手の動きのうち粘土が付いてくる割合 (押す・なでる・離す向きごと) と、引きずる範囲
- `conserveBand` / `conserveMax` / `relax` (`src/sdf.js`): 押しのけた分を盛り上げる帯の幅、1回の上限、なじませる強さ
