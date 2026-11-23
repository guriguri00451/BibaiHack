using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AKun
{
    // 投稿 (Web 版 a-kun/src/post/ui.js の XR 部分の移植)。
    // 流れ: 粘土で形を作る → 「塗る」で色を付ける → 「投稿する」→ マップの模型の上で隠す場所を選ぶ
    //   → 「ここに隠す」(GLB を上げて、隠す場所をマップ座標の Transform にしてオブジェクトとして登録)
    //   投稿したあとも粘土はそのまま残り、直してから「更新して投稿」で新しい版を上げられる。
    //   「新しく作る」で球に戻すと、次は別のオブジェクトとして投稿される。
    // 投稿先: 最初は「この端末内」。起動時の設定 (Intent の extra か config.json) にサーバーがあれば接続する。
    // サーバーにつないでいるときはゲームの流れに従う:
    //   ドロ (hider) で、準備中 (preparing) か本番 (playing) で、まだ見つかっていない間だけ投稿・更新できる。
    //   ラウンドの最初の投稿に自動で化ける (「化ける」で別の投稿に切り替え)。ラウンドが変わると投稿の一覧は空にする。
    //   ホストは右のゲームのパネルでフェーズを進める (期限が来たら自動でも進む)
    public class Post
    {
        public class Item
        {
            public string id; public int n, version; public byte[] bytes; public ClayField.Snapshot snapshot;
            public Vector3 spotPos; public Quaternion spotRot; // 隠した場所 (マップの root から見た位置。Unity の座標)
            public int round;
        }

        readonly ClayApp app;
        IPostTarget target = new LocalTarget();
        public readonly Dictionary<string, Item> posted = new Dictionary<string, Item>();
        int counter;
        string editingId, disguisedId;
        bool busy;
        int lastRound = -1;
        string lastStatus;
        bool lastCaught, lastConnected = true;
        readonly ButtonPanel xrPanel, hidePanel, gamePanel;
        ClayApp.ExportResult? pending; // 隠す場所を選んでいる間の、投稿する形

        public IPostTarget Target => target;
        public ButtonPanel Panel => xrPanel;
        public ButtonPanel HidePanel => hidePanel;
        public ButtonPanel GamePanel => gamePanel;
        public bool Choosing => pending != null;
        public ServerTarget Server => target as ServerTarget;

        // 投稿できない理由 (できるときは null)。この端末内に投稿しているときは、いつでもできる
        string Blocked => Server?.game.HideBlockReason();
        Item Editing => editingId != null && posted.TryGetValue(editingId, out var it) ? it : null;

        public Post(ClayApp app, Transform workspace, float panelZ)
        {
            this.app = app;
            const float bw = 0.075f, bh = 0.036f, gap = 0.006f;
            xrPanel = new ButtonPanel(workspace, "post-panel", new List<ButtonDef>
            {
                new ButtonDef { id = "status", label = XRStatus, x = 0, y = 0, w = 3 * bw + 2 * gap, h = 0.03f },
                new ButtonDef
                {
                    id = "post", x = -(bw + gap), y = -0.04f, w = bw, h = bh, color = "#81c784",
                    label = () => busy ? "送信中…" : Blocked != null ? "投稿できません" : Editing != null ? $"#{Editing.n} を更新" : "投稿する",
                    onPress = DoPost, isEnabled = () => !busy && Blocked == null,
                },
                new ButtonDef
                {
                    id = "disguise", x = 0, y = -0.04f, w = bw, h = bh, color = "#ffb74d",
                    label = () => Editing == null ? "化ける" : Editing.id == disguisedId ? $"#{Editing.n} に化けている" : $"#{Editing.n} に化ける",
                    onPress = DoDisguise,
                    isActive = () => Editing != null && Editing.id == disguisedId,
                    isEnabled = () => !busy && Server != null && Blocked == null && Editing != null && Editing.id != disguisedId,
                },
                new ButtonDef { id = "new", label = () => "新しく作る", x = bw + gap, y = -0.04f, w = bw, h = bh, onPress = NewClay, isEnabled = () => !busy },
            });
            xrPanel.group.localPosition = new Vector3(0, 0.2f, -panelZ);

            // 隠す場所を選んでいる間のパネル (投稿のパネルと入れ替える)
            const float hw = 0.07f;
            hidePanel = new ButtonPanel(workspace, "hide-panel", new List<ButtonDef>
            {
                new ButtonDef { id = "hideStatus", label = HideStatus, x = 0, y = 0, w = 3 * hw + 2 * gap, h = 0.03f },
                new ButtonDef
                {
                    id = "hideHere", x = -(hw + gap), y = -0.04f, w = hw, h = bh, color = "#81c784",
                    label = () => busy ? "送信中…" : "ここに隠す",
                    onPress = ConfirmHide, isEnabled = () => !busy && app.map.HasSpot,
                },
                new ButtonDef { id = "mapReset", label = () => "マップを手元へ", x = 0, y = -0.04f, w = hw, h = bh, onPress = app.RecenterMap, isEnabled = () => !busy },
                new ButtonDef { id = "hideCancel", label = () => "やめる", x = hw + gap, y = -0.04f, w = hw, h = bh, onPress = CancelHide, isEnabled = () => !busy },
            }, new[] { "レイで指してつまむ: 隠す場所", "何もない所をつまむ: マップを動かす", "両手でつまむ: 回す・大きさを変える" });
            // 選んでいる間は粘土を隠すので、少し下げる (上のログの板と重ならないように)
            hidePanel.group.localPosition = new Vector3(0, 0.16f, -panelZ);
            hidePanel.Visible = false;

            // ゲームのパネル (サーバーにつないでいる間だけ)。道具のパネルと左右対称に、右に置く
            const float gw = 0.2f, gh = 0.028f;
            gamePanel = new ButtonPanel(workspace, "game-panel", new List<ButtonDef>
            {
                new ButtonDef { id = "phase", label = PhaseLine, x = 0, y = 0, w = gw, h = gh },
                new ButtonDef { id = "me", label = MeLine, x = 0, y = -(gh + gap), w = gw, h = gh },
                new ButtonDef { id = "people", label = PeopleLine, x = 0, y = -2 * (gh + gap), w = gw, h = gh },
                new ButtonDef
                {
                    id = "host", x = 0, y = -3 * (gh + gap) - 0.006f, w = gw, h = 0.04f, color = "#4fc3f7",
                    label = HostLabel, onPress = HostAction,
                    isEnabled = () => Server != null && Server.game.IsHost && !Server.PhaseBusy && !busy && !Choosing,
                },
            }, new[] { "ゲーム" });
            gamePanel.group.localPosition = new Vector3(0.3f, 0.02f, -panelZ);
            gamePanel.group.localRotation = Quaternion.Euler(0.3f * Mathf.Rad2Deg, 0.6f * Mathf.Rad2Deg, 0);
            gamePanel.Visible = false;
        }

        // ---------- ゲームのパネル ----------
        string PhaseLine()
        {
            var s = Server;
            if (s == null) return "";
            var g = s.game;
            var rem = g.Remaining(DateTime.UtcNow);
            string left = rem != null ? $" · 残り {(int)rem.Value / 60}:{(int)rem.Value % 60:00}" : "";
            string conn = s.Connected ? "" : " · 切断中";
            return $"ラウンド {g.round} · {GameState.PhaseName(g.status)}{left}{conn}";
        }

        string MeLine()
        {
            var g = Server?.game;
            if (g == null) return "";
            string caught = g.IsCaught ? " · 見つかった" : "";
            return $"あなた: {GameState.RoleName(g.myRole)}{caught} · {g.Me?.score ?? 0} 点";
        }

        string PeopleLine()
        {
            var s = Server;
            if (s == null) return "";
            var g = s.game;
            return $"参加 {g.players.Count} 人 (ケイ {g.Count("seeker")}) · 部屋 {ServerTarget.Short(g.sessionId)}";
        }

        string HostLabel()
        {
            var s = Server;
            if (s == null) return "";
            if (!s.game.IsHost) return "ホストが進めます";
            if (s.PhaseBusy) return "送信中…";
            return s.game.status switch
            {
                GameState.WAITING => "準備をはじめる",
                GameState.PREPARING => "本番をはじめる",
                GameState.PLAYING => "ラウンドを終える",
                _ => "次のラウンドへ",
            };
        }

        void HostAction()
        {
            var s = Server;
            if (s == null || !s.game.IsHost) return;
            switch (s.game.status)
            {
                case GameState.PREPARING: _ = s.Play(); break;
                case GameState.PLAYING: _ = s.Finish(); break;
                default: _ = s.StartRound(); break; // waiting / finished
            }
        }

        string HideStatus()
        {
            string what = editingId != null && posted.TryGetValue(editingId, out var it) ? $"#{it.n} の" : "";
            return app.map.HasSpot ? $"{what}隠す場所: {app.map.Name}" : $"{what}隠す場所をマップの上で選んでください";
        }

        string XRStatus()
        {
            string b = Blocked;
            if (b != null) return $"{b} · {target.Label}";
            string e = Editing != null ? $" · #{Editing.n} を編集中" : "";
            return $"投稿 {posted.Count} 件 · {target.Label}{e}";
        }

        // ---------- 「今やること」の帯 ----------
        public struct GuideLine { public string main, sub; public string[] steps; public int step; }
        static readonly string[] STEPS_GAME = { "つくる", "隠す", "化ける", "待つ" };
        static readonly string[] STEPS_LOCAL = { "つくる", "隠す", "できた" };

        // 今の状態から、体験者にいちばん大事な一言を選ぶ
        public GuideLine Guide(string mode, bool hiding, bool hasSpot)
        {
            var s = Server;
            var g = s?.game;
            string[] steps = s != null ? STEPS_GAME : STEPS_LOCAL;
            GuideLine L(string main, string sub, int step) => new GuideLine { main = main, sub = sub, steps = steps, step = step };

            if (s != null)
            {
                if (!s.Connected) return L("サーバーとの接続が切れています", "つながるまで、粘土はそのまま作れます", -1);
                if (g.IsCaught) return L("見つかった！", "このラウンドはもう動かせません。次のラウンドを待とう", -1);
                switch (g.status)
                {
                    case GameState.WAITING:
                        return L("もうすぐ始まります", g.IsHost ? "ケイが入ったら、右のパネルの「準備をはじめる」" : "ホストが始めるまで、粘土で練習できます", 0);
                    case GameState.FINISHED:
                        return L("ラウンド終了", g.IsHost ? "右のパネルの「次のラウンドへ」で続ける" : "次のラウンドを待とう", -1);
                }
                if (g.myRole == "seeker") return L("このラウンドはケイ (探す側) です", "スマホで探す人を応援しよう", -1);
            }
            if (busy) return L("送信中…", "少し待ってください", hiding ? 1 : 0);
            if (hiding)
                return hasSpot
                    ? L("ここでよければ「ここに隠す」", "つまんだまま動かすと場所を変えられる。何もない所をつまむとマップが動く", 1)
                    : L("マップを指して、つまんで隠す場所を選ぼう", "何もない所をつまむとマップが動く。両手で回す・大きさを変える", 1);

            if (posted.Count == 0 || Editing == null)
            {
                string sub = mode == "paint" ? "人差し指の先で塗る。色は左のパネルか、左手のひらを上に向けて選ぶ" : "軽く触れる=なでる、深く押す=こねる、つまむ=伸ばす";
                string main = mode == "paint" ? "好きな色を塗ろう。できたら「投稿する」" : "粘土をこねて形を作ろう。できたら「投稿する」";
                return L(main, sub, 0);
            }
            if (s != null && disguisedId == null)
                return L($"#{Editing.n} に化けよう", "上の「化ける」を押すと、その形に化けてケイから隠れる", 2);
            if (s != null)
            {
                string sub = $"形を直したら「#{Editing.n} を更新」で隠し直せる";
                return g.status == GameState.PLAYING
                    ? L("ケイが探しています。見つからないように！", sub, 3)
                    : L("隠れました。本番が始まるのを待とう", sub, 3);
            }
            return L($"#{Editing.n} を隠しました", $"直したら「#{Editing.n} を更新」、別の形は「新しく作る」", 2);
        }

        // 体積をそろえるために大きさを直した倍率 (ほぼ 1 のときは出さない)
        static string SizeNote(ClayApp.ExportResult ex) => Mathf.Abs(ex.factor - 1) > 0.01f ? $"、大きさを ×{ex.factor:0.00} にそろえた" : "";
        static string Kb(int n) => $"{Mathf.Max(1, Mathf.RoundToInt(n / 1024f))}KB";

        // 「投稿する」: 今の形を書き出して、マップの上で隠す場所を選ぶ
        async void DoPost()
        {
            if (busy) return;
            if (Blocked != null) { Notify($"今は投稿できません: {Blocked}", Hud.WARN, "error"); return; }
            busy = true;
            try
            {
                var ex = app.ExportClay();
                Item editing = editingId != null && posted.TryGetValue(editingId, out var it) ? it : null;
                // 前に隠した場所があれば、そこを最初から選んでおく (そのまま「ここに隠す」で形だけ更新できる)
                await app.BeginHide(ex.mesh, editing != null ? (editing.spotPos, editing.spotRot) : ((Vector3, Quaternion)?)null);
                pending = ex;
                xrPanel.Visible = false;
                hidePanel.Visible = true;
                app.Log(editing != null ? $"#{editing.n} を隠す場所を選んでください (前の場所を選んであります)" : "隠す場所をマップの上で選んでください");
            }
            catch (Exception err)
            {
                Notify($"投稿できませんでした: {err.Message}", Hud.ERROR, "error");
            }
            finally
            {
                busy = false;
            }
        }

        // 「ここに隠す」: GLB を上げて、隠す場所 (マップ座標) で登録する
        async void ConfirmHide()
        {
            if (busy || pending == null || !app.map.HasSpot) return;
            if (Blocked != null) { Notify($"今は投稿できません: {Blocked}", Hud.WARN, "error"); EndChoosing(); return; }
            busy = true;
            var ex = pending.Value;
            int round = Server?.game.round ?? 0;
            try
            {
                string assetUrl = await target.UploadAsset(ex.bytes);
                var transform = app.map.SpotTransform();
                if (editingId != null && posted.TryGetValue(editingId, out var item))
                {
                    await target.UpdateObject(item.id, assetUrl, transform);
                    item.version++; item.bytes = ex.bytes; item.snapshot = app.field.TakeSnapshot();
                    item.spotPos = app.map.SpotPosition; item.spotRot = app.map.SpotRotation;
                    string path = Save(item);
                    app.Log($"#{item.n} を更新して隠しました ({Kb(ex.bytes.Length)}{SizeNote(ex)})");
                    app.hud.Toast($"#{item.n} を更新して隠しました", Hud.OK);
                    app.FxHidden();
                    Debug.Log($"[clay] 保存: {path} transform={MiniJson.Write(transform)}");
                }
                else
                {
                    string objectId = await target.CreateObject(assetUrl, transform);
                    if ((Server?.game.round ?? 0) != round) throw new Exception("送っている間にラウンドが変わりました");
                    var it = new Item
                    {
                        id = objectId, n = ++counter, version = 1, bytes = ex.bytes, snapshot = app.field.TakeSnapshot(),
                        spotPos = app.map.SpotPosition, spotRot = app.map.SpotRotation, round = round,
                    };
                    posted[objectId] = it;
                    editingId = objectId;
                    string path = Save(it);
                    app.Log($"#{counter} を投稿して隠しました ({Kb(ex.bytes.Length)}{SizeNote(ex)})");
                    app.hud.Toast($"#{counter} を隠しました", Hud.OK);
                    app.FxHidden();
                    Debug.Log($"[clay] 保存: {path} transform={MiniJson.Write(transform)}");
                    // ラウンドの最初の投稿には、そのまま化ける (ケイに見つかるとドロが捕まるのはこのオブジェクト)
                    if (Server != null && disguisedId == null)
                    {
                        await target.Disguise(objectId);
                        disguisedId = objectId;
                        Notify($"#{it.n} に化けました", Hud.WARN);
                        app.FxDisguised();
                    }
                }
                EndChoosing();
            }
            catch (Exception err)
            {
                Notify($"投稿できませんでした: {err.Message}", Hud.ERROR, "error");
            }
            finally
            {
                busy = false;
            }
        }

        // 「化ける」: 編集中の投稿に化ける (前に化けていた投稿は普通のオブジェクトに戻る)
        async void DoDisguise()
        {
            var it = Editing;
            if (busy || it == null || Server == null || Blocked != null || it.id == disguisedId) return;
            busy = true;
            try
            {
                await target.Disguise(it.id);
                disguisedId = it.id;
                Notify($"#{it.n} に化けました", Hud.WARN);
                app.FxDisguised();
            }
            catch (Exception err) { Notify($"化けられませんでした: {err.Message}", Hud.ERROR, "error"); }
            finally { busy = false; }
        }

        void CancelHide()
        {
            if (busy) return;
            EndChoosing();
            Notify("投稿をやめました (粘土はそのまま)", Hud.INFO);
        }

        void EndChoosing()
        {
            pending = null;
            hidePanel.Visible = false;
            xrPanel.Visible = true;
            app.EndHide();
        }

        // 投稿した GLB を端末にも保存する (adb pull で取り出せる)
        static string Save(Item it)
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "posts");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"clay-{it.n}-v{it.version}.glb");
                File.WriteAllBytes(path, it.bytes);
                return path;
            }
            catch (Exception e) { return $"(保存できません: {e.Message})"; }
        }

        void NewClay()
        {
            editingId = null;
            app.ResetClay();
        }

        // ---------- サーバー ----------
        // room: 入るセッションの ID。空なら部屋を作ってホストになる (ID はログと files/session.txt に出す)
        public async void Connect(string server, string room, string name, string mapId, double preparingSeconds, double playingSeconds)
        {
            busy = true;
            ServerTarget t = null;
            try
            {
                t = new ServerTarget(server.Trim(), app.Log) { preparingSeconds = preparingSeconds, playingSeconds = playingSeconds };
                await t.Connect(room?.Trim(), name, mapId);
                SwitchTarget(t);
                var g = t.game;
                app.Log(g.IsHost
                    ? $"部屋を作りました ({ServerTarget.Short(g.sessionId)}…)。ケイが入ったら「準備をはじめる」"
                    : $"部屋に入りました ({ServerTarget.Short(g.sessionId)}…)。ホストが始めるのを待ちます");
                app.hud.Toast(g.IsHost ? "部屋を作りました" : "部屋に入りました", Hud.INFO);
                Sfx.Play("chime");
                lastStatus = g.status; lastCaught = g.IsCaught;
            }
            catch (Exception err)
            {
                t?.Close();
                Notify($"サーバーに接続できません: {err.Message}", Hud.ERROR, "error");
            }
            finally { busy = false; }
        }

        // 投稿先を替えると、それまでの投稿の一覧は空にする (別の場所の話になるので)
        void SwitchTarget(IPostTarget t)
        {
            target.Close();
            target = t;
            ClearPosts();
            lastRound = Server?.game.round ?? -1;
            gamePanel.Visible = Server != null;
        }

        void ClearPosts()
        {
            posted.Clear();
            editingId = null;
            disguisedId = null;
            counter = 0;
        }

        public void Close() => target.Close();

        // ログに書き、体験者にも通知で見せる (音つき)
        void Notify(string msg, Color accent, string sound = null, bool log = true)
        {
            if (log) app.Log(msg);
            app.hud?.Toast(msg, accent);
            if (sound != null) Sfx.Play(sound);
        }

        // フェーズが変わった: ジングルと通知
        void OnPhase(GameState g)
        {
            switch (g.status)
            {
                case GameState.PREPARING:
                    Notify(g.IsHider ? $"ラウンド {g.round} 準備開始！ 形を作って隠そう" : $"ラウンド {g.round} 準備開始！", Hud.OK, "jinglePrepare", false);
                    break;
                case GameState.PLAYING:
                    Notify("本番開始！ ケイが探し始めた", Hud.WARN, "jinglePlay", false);
                    break;
                case GameState.FINISHED:
                    Notify("ラウンド終了", Hud.INFO, "jingleFinish", false);
                    break;
            }
        }

        public void Update()
        {
            var s = Server;
            if (s != null)
            {
                s.Tick();
                // 新しいラウンド: 前のラウンドのオブジェクトはもう使われない (仕様書 3.1)。粘土はそのまま残す
                if (s.game.round != lastRound)
                {
                    lastRound = s.game.round;
                    if (posted.Count > 0) app.Log($"ラウンド {lastRound}: 前のラウンドの投稿は片付けました (粘土はそのまま)");
                    ClearPosts();
                }
                // フェーズが変わった・見つかった・接続が切れた / つながった
                var g = s.game;
                if (lastStatus != null && g.status != lastStatus) OnPhase(g);
                lastStatus = g.status;
                if (g.IsCaught && !lastCaught) Notify("見つかった！ このラウンドはもう動かせません", Hud.ERROR, "alarm", false);
                lastCaught = g.IsCaught;
                if (s.Connected != lastConnected)
                {
                    lastConnected = s.Connected;
                    if (s.Connected) Notify("サーバーにつながりました", Hud.OK, "chime", false);
                    else Notify("サーバーとの接続が切れました", Hud.ERROR, "error", false);
                }
                // 隠す場所を選んでいる間に投稿できなくなった (見つかった、ラウンドが終わった)
                if (Choosing && !busy && Blocked != null)
                {
                    Notify($"投稿をやめました: {Blocked}", Hud.WARN);
                    EndChoosing();
                }
            }
            if (xrPanel.Visible) xrPanel.Refresh();
            if (hidePanel.Visible) hidePanel.Refresh();
            if (gamePanel.Visible) gamePanel.Refresh();
        }
    }
}
