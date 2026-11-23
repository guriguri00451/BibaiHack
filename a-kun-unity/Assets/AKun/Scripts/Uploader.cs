using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AKun
{
    // 粘土のモデルを投稿する部分 (Web 版 a-kun/src/post/uploader.js の移植)。
    // 仕様 (docs/whiteboard-brainstorm.md) の「Sculpt確定 → Object Storage + PostgreSQL」と
    // REST の /assets/uploads と /sessions/{id}/objects に合わせる。
    //
    //   await target.UploadAsset(glb)                       → assetKey (版ごとに別のキー)
    //   await target.CreateObject(assetKey, transform)      → objectId
    //   await target.UpdateObject(objectId, assetKey, transform)
    //   await target.DeleteObject(objectId)
    //   await target.Disguise(objectId)                     → このオブジェクトに化ける (前の擬態先は外れる)
    public interface IPostTarget
    {
        string Kind { get; }
        string Label { get; }
        Task<string> UploadAsset(byte[] glb);
        Task<string> CreateObject(string assetUrl, Dictionary<string, object> transform);
        Task UpdateObject(string objectId, string assetUrl, Dictionary<string, object> transform);
        Task DeleteObject(string objectId);
        Task Disguise(string objectId);
        void Close();
    }

    // サーバーがまだ無いときの代わり。このアプリの中に置くだけ (終了すると消える。GLB はファイルにも保存する)
    public class LocalTarget : IPostTarget
    {
        readonly Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        readonly Dictionary<string, (string assetUrl, Dictionary<string, object> transform, int version)> objects
            = new Dictionary<string, (string, Dictionary<string, object>, int)>();

        public string Kind => "local";
        public string Label => "この端末内";

        public Task<string> UploadAsset(byte[] glb)
        {
            string url = $"local-asset://{Guid.NewGuid()}.glb";
            assets[url] = glb;
            return Task.FromResult(url);
        }

        public Task<string> CreateObject(string assetUrl, Dictionary<string, object> transform)
        {
            string id = Guid.NewGuid().ToString();
            objects[id] = (assetUrl, transform, 1);
            return Task.FromResult(id);
        }

        public Task UpdateObject(string objectId, string assetUrl, Dictionary<string, object> transform)
        {
            if (!objects.TryGetValue(objectId, out var o)) throw new Exception("投稿が見つかりません");
            if (assetUrl != null) { o.assetUrl = assetUrl; o.version += 1; }
            if (transform != null) o.transform = transform;
            objects[objectId] = o;
            return Task.CompletedTask;
        }

        public Task DeleteObject(string objectId)
        {
            objects.Remove(objectId);
            return Task.CompletedTask;
        }

        public Task Disguise(string objectId) => Task.CompletedTask;

        public void Close() { }
    }


    // ゲームサーバー用 (仕様書 docs/whiteboard-brainstorm.md の 5 章・6 章)。
    //   参加: POST /v1/guests → POST /v1/sessions (部屋を作る) か POST /v1/sessions/{id}/join → GET /v1/sessions/{id}
    //         → WebSocket /v1/ws に auth → session.snapshot
    //   フェーズ (ホストだけ): POST /v1/sessions/{id}/rounds/start · play · finish
    //   投稿: POST /v1/assets/uploads → 署名 URL へ PUT → POST /v1/sessions/{id}/objects → (最初の 1 つは) …/disguise
    //         直すとき PATCH /v1/sessions/{id}/objects/{objectId}
    // サーバーにまだ無い API (404 NOT_FOUND / 405 / 501) は、ログで知らせて、この端末の中で済ませる (ゲームの流れは止めない)。
    public class ServerTarget : IPostTarget
    {
        public struct ApiResult
        {
            public long status;
            public string body, error;
            public Dictionary<string, object> json;
            public bool notImplemented;
            public bool Ok => status >= 200 && status < 300;
        }

        public readonly GameState game = new GameState();
        public readonly string apiBase, wsUrl;
        public string Token { get; private set; }
        public bool PhaseBusy { get; private set; }
        public bool Connected => ws != null && ws.State == WebSocketState.Open && authed;
        public double preparingSeconds = 120, playingSeconds = 180;
        // 結果を見せる秒数。0 より大きければ、ラウンドが終わってこの秒数でサーバーが次のラウンドを始める (0 ならホストが「次のラウンドへ」)
        public double resultSeconds = 20;

        // 選べるマップ (GET /v1/maps)。ホストは待機中とラウンドの終わった後に替えられる
        public readonly List<MapEntry> maps = new List<MapEntry>();
        public bool MapBusy { get; private set; }
        public struct MapEntry { public string id, name; }

        readonly Action<string> log;
        readonly HashSet<string> warned = new HashSet<string>();
        readonly HashSet<string> localIds = new HashSet<string>(); // サーバーに登録できず、この端末内だけにある投稿
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        ClientWebSocket ws;
        bool authed, closed, reconnecting;
        TaskCompletionSource<bool> firstSnapshot;
        DateTime? expiredSince; // ホスト: 期限が来てから (サーバーが自分で進めるのを少し待つ)

        public string Kind => "server";
        public string Label => game.sessionId == null ? "サーバー" : $"セッション {Short(game.sessionId)}";
        public static string Short(string id) => id != null && id.Length > 8 ? id.Substring(0, 8) : id;
        string Sid => game.sessionId;

        public ServerTarget(string server, Action<string> log)
        {
            (apiBase, wsUrl) = GameUrls.Endpoints(server);
            this.log = log;
        }

        // ---------- 参加 ----------

        // sessionId が空なら部屋を作ってホストになる。あれば参加する
        public async Task Connect(string sessionId, string name, string mapId)
        {
            game.mapId = mapId;
            bool reused = UseSavedGuest();
            if (!reused) await NewGuest(name);
            var r = await EnterSession(sessionId, mapId);
            if (r.status == 401 && reused)
            {
                // 保存していたトークンが使えない (DB を作り直した、など)。ゲストを作り直す
                await NewGuest(name);
                r = await EnterSession(sessionId, mapId);
            }
            if (!r.Ok) throw new Exception(string.IsNullOrEmpty(sessionId) ? $"部屋を作れません ({r.error})" : $"部屋に入れません ({r.error})");
            await RefreshSession();
            SaveSessionId();
            await OpenSocket();
        }

        async Task<ApiResult> EnterSession(string sessionId, string mapId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                var r = await Api("POST", "/sessions", new Dictionary<string, object> { ["mapId"] = mapId });
                if (r.Ok)
                {
                    game.sessionId = r.json.Str("id");
                    game.myPlayerId = r.json.Str("playerId");
                    game.hostUserId = game.myUserId; // 作った人がホスト
                    game.mapId = r.json.Str("mapId") ?? mapId;
                }
                return r;
            }
            else
            {
                var r = await Api("POST", $"/sessions/{sessionId}/join", null);
                if (r.Ok)
                {
                    game.sessionId = r.json.Str("sessionId") ?? sessionId;
                    game.myPlayerId = r.json.Str("playerId");
                }
                return r;
            }
        }

        // 同じ端末なら同じゲストで入り直す (サーバーは同じユーザーの再参加に同じ playerId を返す)
        string PrefKey => "akun.guest." + apiBase;

        bool UseSavedGuest()
        {
            string saved = PlayerPrefs.GetString(PrefKey, "");
            int bar = saved.IndexOf('|');
            if (bar <= 0) return false;
            game.myUserId = saved.Substring(0, bar);
            Token = saved.Substring(bar + 1);
            return true;
        }

        async Task NewGuest(string name)
        {
            Token = null;
            var r = await Api("POST", "/guests", new Dictionary<string, object> { ["name"] = name });
            if (!r.Ok) throw new Exception($"ゲストを作れません ({r.error})");
            Token = r.json.Str("accessToken");
            game.myUserId = (r.json.TryGetValue("user", out var u) ? u as Dictionary<string, object> : null).Str("id");
            PlayerPrefs.SetString(PrefKey, $"{game.myUserId}|{Token}");
            PlayerPrefs.Save();
        }

        public async Task RefreshSession()
        {
            if (Sid == null) return;
            var r = await Api("GET", $"/sessions/{Sid}", null);
            if (r.Ok) game.ApplySessionDetail(r.json);
            else Debug.LogWarning($"[game] 状態を取り直せません: {r.error}");
        }

        // スタッフが ID を取り出せるように残す (adb pull …/files/session.txt)。ケイ側はこの ID で参加する
        void SaveSessionId()
        {
            Debug.Log($"[game] session id: {Sid} (host={game.IsHost}) api={apiBase}");
            try { File.WriteAllText(Path.Combine(Application.persistentDataPath, "session.txt"), Sid + "\n"); }
            catch (Exception e) { Debug.LogWarning($"[game] session.txt を書けません: {e.Message}"); }
        }

        // ---------- WebSocket ----------

        async Task OpenSocket()
        {
            var sock = new ClientWebSocket();
            ws = sock;
            authed = false;
            var first = firstSnapshot = new TaskCompletionSource<bool>();
            try
            {
                using var timeout = new CancellationTokenSource(8000);
                await sock.ConnectAsync(new Uri(wsUrl), timeout.Token);
            }
            catch (Exception e) { throw new Exception($"WebSocket につながりません ({e.Message})"); }
            _ = ReceiveLoop(sock);
            await SendJson(sock, new Dictionary<string, object>
            {
                ["type"] = "auth",
                ["payload"] = new Dictionary<string, object> { ["accessToken"] = Token, ["sessionId"] = Sid },
            });
            if (await Task.WhenAny(first.Task, Task.Delay(8000)) != first.Task) throw new Exception("session.snapshot が届きません");
            await first.Task; // 閉じられたときはここで例外になる
        }

        Task SendJson(ClientWebSocket sock, Dictionary<string, object> msg)
            => sock.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(MiniJson.Write(msg))), WebSocketMessageType.Text, true, cts.Token);

        async Task ReceiveLoop(ClientWebSocket sock)
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            try
            {
                while (sock.State == WebSocketState.Open)
                {
                    var r = await sock.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    string text = sb.ToString();
                    sb.Clear();
                    OnMessage(text);
                }
            }
            catch (Exception) { /* 切れた */ }
            if (closed || sock != ws) return;
            authed = false;
            int code = sock.CloseStatus.HasValue ? (int)sock.CloseStatus.Value : 0;
            string why = CloseReason(code, sock.CloseStatusDescription);
            firstSnapshot?.TrySetException(new Exception(why));
            if (code == 4001 || code == 4003 || code == 4004 || code == 4009)
            {
                log($"サーバーから切られました: {why}");
                return;
            }
            log($"サーバーとの接続が切れました ({why})。つなぎ直します");
            Reconnect();
        }

        static string CloseReason(int code, string desc) => code switch
        {
            4001 => "認証できません",
            4003 => "この部屋の参加者ではありません",
            4004 => "部屋がありません",
            4009 => "別の接続で入り直しました",
            0 => "接続が切れました",
            _ => $"{code} {desc}",
        };

        async void Reconnect()
        {
            if (reconnecting) return;
            reconnecting = true;
            int delay = 1000;
            while (!closed)
            {
                await Task.Delay(delay);
                if (closed) break;
                try
                {
                    await OpenSocket();
                    await RefreshSession();
                    log("サーバーにつなぎ直しました");
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[game] つなぎ直せません: {e.Message}");
                    delay = Math.Min(delay * 2, 10000);
                }
            }
            reconnecting = false;
        }

        void OnMessage(string text)
        {
            Dictionary<string, object> msg;
            try { msg = MiniJson.Parse(text) as Dictionary<string, object>; } catch { return; }
            if (msg == null) return;
            string type = msg.Str("type");
            if (type == "error")
            {
                var p = msg.TryGetValue("payload", out var pv) ? pv as Dictionary<string, object> : null;
                log($"サーバー: {p.Str("code")} {p.Str("message")}");
                return;
            }
            string note = game.ApplyMessage(msg);
            if (type == "session.snapshot")
            {
                authed = true;
                firstSnapshot?.TrySetResult(true);
            }
            if (note != null) log(note);
        }

        // ---------- フェーズ (ホスト) ----------

        public Task StartRound()
        {
            var body = new Dictionary<string, object>
            {
                ["preparingSeconds"] = (int)preparingSeconds,
                ["playingSeconds"] = (int)playingSeconds,
                ["roles"] = Roles(),
            };
            // サーバーが 終了 → 次の準備 も進める (同じ役割で。後から入った人はケイ)
            if (resultSeconds > 0) body["resultSeconds"] = Math.Max(1, (int)resultSeconds);
            return Phase("start", body, () => game.LocalStart(preparingSeconds, game.myPlayerId, DateTime.UtcNow), "準備を始めました");
        }

        // ---------- マップ ----------

        // GET /v1/maps。無ければ (古いサーバー) 今のマップだけ
        public async Task LoadMaps()
        {
            var r = await Api("GET", "/maps", null);
            maps.Clear();
            if (r.Ok && r.json != null && r.json.TryGetValue("maps", out var mv) && mv is List<object> list)
            {
                foreach (var o in list)
                {
                    if (!(o is Dictionary<string, object> m) || m.Str("mapId") == null) continue;
                    maps.Add(new MapEntry { id = m.Str("mapId"), name = m.Str("name") ?? m.Str("mapId") });
                }
            }
            else if (r.notImplemented) Warn("GET /maps", "GET /maps が無いので、マップは選べません");
            else Debug.LogWarning($"[game] マップの一覧を取れません: {r.error}");
        }

        public string MapName(string id)
        {
            foreach (var m in maps) if (m.id == id) return m.name;
            return id ?? "";
        }

        public bool CanChangeMap => game.IsHost && !MapBusy && !PhaseBusy && Sid != null
            && (game.status == GameState.WAITING || game.status == GameState.FINISHED) && maps.Count > 1;

        // 一覧の次のマップにする (ホスト)。PUT /v1/sessions/{id}/map。ほかの端末には snapshot で届く
        public async Task NextMap()
        {
            if (!CanChangeMap) return;
            int i = maps.FindIndex(m => m.id == game.mapId);
            var next = maps[(i + 1) % maps.Count];
            MapBusy = true;
            try
            {
                var r = await Api("PUT", $"/sessions/{Sid}/map", new Dictionary<string, object> { ["mapId"] = next.id });
                if (r.Ok) { game.ApplySessionDetail(r.json); log($"マップを「{next.name}」にしました"); }
                else if (r.notImplemented) Warn("PUT /sessions/{id}/map", "PUT /sessions/{id}/map が無いので、マップは替えられません");
                else log($"[エラー] マップを替えられません ({r.error})");
            }
            finally { MapBusy = false; }
        }

        public Task Play() => Phase("play", new Dictionary<string, object> { ["playingSeconds"] = (int)playingSeconds },
            () => game.LocalPlay(playingSeconds, DateTime.UtcNow), "本番を始めました");

        public Task Finish() => Phase("finish", new Dictionary<string, object>(), game.LocalFinish, "ラウンドを終えました");

        // この端末 (ホスト) がドロ、ほかの全員がケイ。a-kun-unity は隠す側の端末なので
        List<object> Roles()
        {
            var list = new List<object>();
            foreach (var p in game.players.Values)
                list.Add(new Dictionary<string, object> { ["playerId"] = p.id, ["role"] = p.id == game.myPlayerId ? "hider" : "seeker" });
            return list;
        }

        async Task Phase(string what, Dictionary<string, object> body, Action local, string done)
        {
            if (PhaseBusy || Sid == null) return;
            PhaseBusy = true;
            expiredSince = null;
            string route = $"POST /sessions/{{id}}/rounds/{what}";
            try
            {
                var r = await Api("POST", $"/sessions/{Sid}/rounds/{what}", body);
                if (r.Ok)
                {
                    game.localPhase = false;
                    if (r.json != null && r.json.ContainsKey("status")) game.ApplySessionDetail(r.json);
                    await RefreshSession();
                    log($"ラウンド {game.round}: {done}");
                }
                else if (r.notImplemented || game.localPhase)
                {
                    // API が無い、またはすでにこの端末でフェーズを進めている (サーバーのフェーズと合わない) ので、この端末で進める
                    if (r.notImplemented) Warn(route, $"{route} が無いので、この端末でフェーズを進めます");
                    else Debug.LogWarning($"[game] {route}: {r.error} (この端末でフェーズを進めます)");
                    local();
                    log($"ラウンド {game.round}: {done}");
                }
                else if (r.status == 409)
                {
                    log("ほかの操作と重なりました。状態を取り直します");
                    await RefreshSession();
                }
                else log($"[エラー] {route}: {r.error}");
            }
            finally { PhaseBusy = false; }
        }

        // 毎フレーム呼ぶ。ホストは期限が来たら次のフェーズへ進める (サーバーが自分で進めたら何もしない)
        public void Tick()
        {
            if (!game.IsHost || PhaseBusy || Sid == null) return;
            var rem = game.Remaining(DateTime.UtcNow);
            bool due = (game.status == GameState.PREPARING && rem != null && rem.Value <= 0)
                || (game.status == GameState.PLAYING && ((rem != null && rem.Value <= 0) || game.AllHidersCaught()));
            if (!due) { expiredSince = null; return; }
            expiredSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - expiredSince.Value).TotalSeconds < 1.5) return;
            if (game.status == GameState.PREPARING) _ = Play();
            else _ = Finish();
        }

        // ---------- 投稿 ----------

        public async Task<string> UploadAsset(byte[] glb)
        {
            const string route = "POST /assets/uploads";
            var r = await Api("POST", "/assets/uploads", new Dictionary<string, object>
            {
                ["sessionId"] = Sid, ["contentType"] = "model/gltf-binary", ["sizeBytes"] = glb.Length,
            });
            if (r.Ok)
            {
                string key = r.json.Str("assetKey"), up = r.json.Str("uploadUrl");
                if (key != null && up != null)
                {
                    string err = await Put(up, glb);
                    if (err == null) return key;
                    log($"[エラー] GLB を送れません ({err})。この端末内で続けます");
                }
                else log("[エラー] アップロード先が返りません。この端末内で続けます");
            }
            else Fallback(r, route, "GLB のアップロード");
            return $"local-asset://{Guid.NewGuid()}.glb";
        }

        public async Task<string> CreateObject(string assetKey, Dictionary<string, object> transform)
        {
            const string route = "POST /sessions/{id}/objects";
            var r = await Api("POST", $"/sessions/{Sid}/objects", new Dictionary<string, object> { ["assetKey"] = assetKey, ["transform"] = transform });
            string id = r.Ok ? r.json.Str("id") : null;
            if (id != null) return id;
            if (r.Ok) log("[エラー] オブジェクトの id が返りません。この端末内で続けます");
            else Fallback(r, route, "オブジェクトの登録");
            id = Guid.NewGuid().ToString();
            localIds.Add(id);
            return id;
        }

        public async Task UpdateObject(string objectId, string assetKey, Dictionary<string, object> transform)
        {
            if (localIds.Contains(objectId)) return;
            var body = new Dictionary<string, object>();
            if (assetKey != null) body["assetKey"] = assetKey;
            if (transform != null) body["transform"] = transform;
            var r = await Api("PATCH", $"/sessions/{Sid}/objects/{objectId}", body);
            if (!r.Ok) Fallback(r, "PATCH /sessions/{id}/objects/{objectId}", "オブジェクトの更新");
        }

        public async Task DeleteObject(string objectId)
        {
            if (localIds.Remove(objectId)) return;
            var r = await Api("DELETE", $"/sessions/{Sid}/objects/{objectId}", null);
            if (!r.Ok) Fallback(r, "DELETE /sessions/{id}/objects/{objectId}", "オブジェクトの削除");
        }

        public async Task Disguise(string objectId)
        {
            if (localIds.Contains(objectId)) return;
            var r = await Api("POST", $"/sessions/{Sid}/objects/{objectId}/disguise", null);
            if (!r.Ok) Fallback(r, "POST /sessions/{id}/objects/{objectId}/disguise", "擬態の切り替え");
        }

        // 失敗しても流れは止めない。未実装なら 1 回だけ知らせる
        void Fallback(ApiResult r, string route, string what)
        {
            if (r.notImplemented) Warn(route, $"{route} が無いので、{what}はこの端末内で済ませます");
            else log($"[エラー] {what}ができません ({r.error})。この端末内で続けます");
        }

        void Warn(string key, string msg)
        {
            Debug.LogWarning("[game] 未実装: " + msg);
            if (warned.Add(key)) log("[未実装] " + msg);
        }

        // ---------- HTTP ----------

        public async Task<ApiResult> Api(string method, string path, Dictionary<string, object> body)
        {
            using var req = new UnityWebRequest(apiBase + path, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 10 };
            if (body != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MiniJson.Write(body)));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            if (!string.IsNullOrEmpty(Token)) req.SetRequestHeader("Authorization", $"Bearer {Token}");
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            var r = new ApiResult { status = req.responseCode, body = req.downloadHandler?.text };
            try { r.json = string.IsNullOrEmpty(r.body) ? null : MiniJson.Parse(r.body) as Dictionary<string, object>; } catch { /* JSON でない */ }
            r.notImplemented = r.status > 0 && GameUrls.IsNotImplemented(r.status, r.body);
            if (!r.Ok)
            {
                var err = r.json != null && r.json.TryGetValue("error", out var e) ? e as Dictionary<string, object> : null;
                r.error = r.status == 0 ? req.error : $"{r.status} {err.Str("code") ?? req.error}";
                Debug.Log($"[game] {method} {path} → {r.status} {r.body}");
            }
            return r;
        }

        async Task<string> Put(string url, byte[] bytes)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out _)) url = new Uri(new Uri(apiBase + "/"), url).ToString();
            using var req = new UnityWebRequest(url, "PUT")
            {
                uploadHandler = new UploadHandlerRaw(bytes) { contentType = "model/gltf-binary" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 30,
            };
            req.SetRequestHeader("Content-Type", "model/gltf-binary");
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return req.result == UnityWebRequest.Result.Success ? null : $"{req.responseCode} {req.error}";
        }

        public void Close()
        {
            closed = true;
            try
            {
                cts.Cancel();
                ws?.Abort();
                ws?.Dispose();
            }
            catch { /* 無視 */ }
        }
    }
}
