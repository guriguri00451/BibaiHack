using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AKun.EditorTools
{
    // ゲームの状態 (Game.cs) の確認。
    //   Unity -batchmode -quit -projectPath a-kun-unity -executeMethod AKun.EditorTools.GameTests.Run
    // 本物のサーバーにつないで、ゲームの流れ (参加 → 準備 → 投稿・擬態 → 本番 → 終了 → 次のラウンド) を一通り回す。
    // 未実装の API はログで知らせて、この端末の中で進むことも確かめる:
    //   Unity -batchmode -projectPath a-kun-unity -executeMethod AKun.EditorTools.GameTests.RunLive -akunServer https://bibai.t0waxx.com/a-kun-01
    public static class GameTests
    {
        static int fails;
        static void Check(bool ok, string msg) { Debug.Log($"[test] {(ok ? "OK  " : "FAIL")} {msg}"); if (!ok) fails++; }

        static Dictionary<string, object> J(string json) => (Dictionary<string, object>)MiniJson.Parse(json);

        [MenuItem("AKun/Run Game Tests")]
        public static void Run()
        {
            fails = 0;

            // ---- URL ----
            var (api, ws) = GameUrls.Endpoints("https://bibai.t0waxx.com/a-kun-01");
            Check(api == "https://bibai.t0waxx.com/a-kun-01/v1" && ws == "wss://bibai.t0waxx.com/a-kun-01/v1/ws", $"接続先から URL を作る: {api} {ws}");
            (api, ws) = GameUrls.Endpoints("wss://bibai.t0waxx.com/v1/ws");
            Check(api == "https://bibai.t0waxx.com/v1" && ws == "wss://bibai.t0waxx.com/v1/ws", $"WebSocket の URL からも作れる: {api} {ws}");
            (api, ws) = GameUrls.Endpoints("http://192.168.0.5:8080/");
            Check(api == "http://192.168.0.5:8080/v1" && ws == "ws://192.168.0.5:8080/v1/ws", $"http は ws になる: {api} {ws}");

            // ---- 未実装の見分け ----
            Check(GameUrls.IsNotImplemented(404, "{\"error\":{\"code\":\"NOT_FOUND\",\"message\":\"Not found.\"}}"), "404 NOT_FOUND は未実装");
            Check(!GameUrls.IsNotImplemented(404, "{\"error\":{\"code\":\"SESSION_NOT_FOUND\",\"message\":\"x\"}}"), "404 SESSION_NOT_FOUND は未実装ではない");
            Check(GameUrls.IsNotImplemented(404, "404 Not Found"), "JSON でない 404 は未実装");
            Check(GameUrls.IsNotImplemented(405, ""), "405 は未実装");
            Check(!GameUrls.IsNotImplemented(409, "{}") && !GameUrls.IsNotImplemented(0, null), "409 と通信失敗は未実装ではない");

            // ---- 状態 ----
            var g = new GameState { myUserId = "u1", myPlayerId = "p1" };
            g.ApplySessionDetail(J(@"{""id"":""s"",""hostUserId"":""u1"",""status"":""waiting"",""round"":1,""mapId"":""m"",""phaseEndsAt"":null,
                ""players"":[{""id"":""p1"",""userId"":""u1"",""name"":""a"",""role"":null,""score"":0,""isCaught"":false},
                             {""id"":""p2"",""userId"":""u2"",""name"":""b"",""role"":null,""score"":3,""isCaught"":false}]}"));
            Check(g.IsHost && g.players.Count == 2 && g.status == "waiting" && g.players["p2"].score == 3, "GET /sessions/{id} を当てる");
            Check(!g.CanHide && g.HideBlockReason() != null, "待機中は投稿できない");

            var now = DateTime.UtcNow;
            g.LocalStart(60, "p1", now);
            Check(g.status == "preparing" && g.round == 1 && g.IsHider && g.players["p2"].role == "seeker" && g.localPhase, "この端末で準備を始める (自分がドロ)");
            Check(g.CanHide, "準備中のドロは投稿できる");
            var rem = g.Remaining(now);
            Check(rem != null && Math.Abs(rem.Value - 60) < 1, $"残り時間 {rem}");

            // 未実装のサーバーは waiting のままの snapshot を送ってくる。この端末のフェーズは上書きしない
            g.ApplyMessage(J(@"{""type"":""session.snapshot"",""round"":1,""seq"":0,""timestamp"":0,""payload"":{""session"":{""id"":""s"",""status"":""waiting"",""round"":1,""phaseEndsAt"":null},
                ""you"":{""playerId"":""p1"",""role"":null},""players"":[{""playerId"":""p1"",""name"":""a"",""role"":null,""isCaught"":false,""connected"":true,""transform"":null}]}}"));
            Check(g.status == "preparing" && g.IsHider, "この端末で進めている間は snapshot のフェーズで上書きしない");
            g.clockOffsetMs = 0;

            g.LocalPlay(30, now);
            Check(g.status == "playing" && g.CanHide, "本番もドロは投稿できる");
            string note = g.ApplyMessage(J(@"{""type"":""player.caught"",""round"":1,""seq"":5,""timestamp"":0,""payload"":{""playerId"":""p1"",""score"":0}}"));
            g.clockOffsetMs = 0;
            Check(g.IsCaught && !g.CanHide && note != null && g.AllHidersCaught(), $"見つかったら投稿できない ({note})");

            g.LocalFinish();
            Check(g.status == "finished" && g.phaseEndsAt == null, "終了");
            g.LocalStart(60, "p1", now);
            Check(g.round == 2 && !g.IsCaught && g.CanHide, "次のラウンドは round++ で見つかった印が消える");

            // サーバーが phase_changed を送ってきたら、サーバーに従う
            note = g.ApplyMessage(J(@"{""type"":""session.phase_changed"",""round"":2,""seq"":9,""timestamp"":0,""payload"":{""status"":""playing"",""round"":2,""phaseEndsAt"":""2030-01-01T00:00:00.000Z""}}"));
            g.clockOffsetMs = 0;
            Check(!g.localPhase && g.status == "playing" && g.phaseEndsAt == new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), $"phase_changed でサーバーに従う ({note})");
            g.ApplyMessage(J(@"{""type"":""session.phase_changed"",""round"":1,""seq"":1,""timestamp"":0,""payload"":{""status"":""waiting""}}"));
            Check(g.status == "playing", "古いラウンドの phase_changed は無視");
            g.ApplyMessage(J(@"{""type"":""session.snapshot"",""round"":3,""seq"":0,""timestamp"":0,""payload"":{""session"":{""id"":""s"",""status"":""preparing"",""round"":3,""phaseEndsAt"":null},
                ""you"":{""playerId"":""p1"",""role"":""seeker""},""players"":[]}}"));
            Check(g.round == 3 && g.status == "preparing" && g.myRole == "seeker" && g.HideBlockReason() == "このラウンドはケイです", "ケイの番は投稿できない");

            Debug.Log(fails == 0 ? "[test] ALL OK" : $"[test] {fails} FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(fails == 0 ? 0 : 1);
        }

        // ---------- 本物のサーバーで一通り ----------
        public static void RunLive()
        {
            string server = "https://bibai.t0waxx.com/a-kun-01";
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-akunServer") server = args[i + 1];
            fails = 0;
            var task = Live(server);
            double started = EditorApplication.timeSinceStartup;
            void Poll()
            {
                if (!task.IsCompleted && EditorApplication.timeSinceStartup - started < 120) return;
                EditorApplication.update -= Poll;
                if (!task.IsCompleted) Check(false, "時間切れ");
                else if (task.IsFaulted) Check(false, $"例外: {task.Exception?.GetBaseException()}");
                Debug.Log(fails == 0 ? "[test] LIVE ALL OK" : $"[test] LIVE {fails} FAILED");
                if (Application.isBatchMode) EditorApplication.Exit(fails == 0 ? 0 : 1);
            }
            EditorApplication.update += Poll;
        }

        static async Task Live(string server)
        {
            void Log(string m) => Debug.Log($"[test] log: {m}");
            PlayerPrefs.DeleteKey("akun.guest." + GameUrls.Endpoints(server).api); // 毎回新しいゲストで

            var host = new ServerTarget(server, Log) { preparingSeconds = 3, playingSeconds = 3 };
            await host.Connect("", "test-hider", "bibai-museum");
            var g = host.game;
            Check(g.sessionId != null && g.IsHost && host.Connected, $"部屋を作ってホストになる ({g.sessionId})");

            // ケイ役 (seeker-unity の代わり) が同じ部屋に入る
            var seeker = new ServerTarget(server, Log);
            PlayerPrefs.DeleteKey("akun.guest." + seeker.apiBase);
            await seeker.Connect(g.sessionId, "test-seeker", "bibai-museum");
            Check(seeker.Connected && !seeker.game.IsHost, "もう 1 人が参加する");
            await host.RefreshSession();
            Check(g.players.Count == 2, $"参加者 {g.players.Count} 人");

            await host.StartRound();
            Check(g.status == GameState.PREPARING && g.IsHider && g.CanHide, $"準備中・ドロ (localPhase={g.localPhase})");

            byte[] glb = { 0x67, 0x6c, 0x54, 0x46, 2, 0, 0, 0, 12, 0, 0, 0 };
            string key = await host.UploadAsset(glb);
            var tf = new Dictionary<string, object>
            {
                ["position"] = new List<object> { 1f, 0f, 2f }, ["rotation"] = new List<object> { 0f, 0f, 0f, 1f }, ["scale"] = new List<object> { 1f, 1f, 1f },
            };
            string id = await host.CreateObject(key, tf);
            await host.Disguise(id);
            await host.UpdateObject(id, key, tf);
            Check(!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(id), $"投稿・擬態・更新が流れを止めない ({key}, {id})");

            // 期限が来たらホストが自動で進める (Tick を毎フレーム呼ぶ代わり)
            async Task WaitFor(string status, double seconds)
            {
                var until = DateTime.UtcNow.AddSeconds(seconds);
                while (g.status != status && DateTime.UtcNow < until) { host.Tick(); await Task.Delay(100); }
            }
            await WaitFor(GameState.PLAYING, 10);
            Check(g.status == GameState.PLAYING && g.CanHide, "準備の期限で本番へ");
            await WaitFor(GameState.FINISHED, 10);
            Check(g.status == GameState.FINISHED && !g.CanHide, "本番の期限で終了");

            await host.StartRound();
            Check(g.round == 2 && g.status == GameState.PREPARING, $"次のラウンド (round={g.round})");
            await host.Finish();
            Check(g.status == GameState.FINISHED, "ホストがラウンドを終える");

            host.Close();
            seeker.Close();
        }
    }
}
