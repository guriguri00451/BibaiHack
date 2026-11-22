using System;
using System.Collections.Generic;
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
    // WebSocket の object.created / object.updated / object.deleted に合わせる。
    //
    //   await target.UploadAsset(glb)                       → asset_url (版ごとに別のキー)
    //   await target.CreateObject(assetUrl, transform)      → objectId
    //   await target.UpdateObject(objectId, assetUrl, transform)
    //   await target.DeleteObject(objectId)
    public interface IPostTarget
    {
        string Kind { get; }
        string Label { get; }
        Task<string> UploadAsset(byte[] glb);
        Task<string> CreateObject(string assetUrl, Dictionary<string, object> transform);
        Task UpdateObject(string objectId, string assetUrl, Dictionary<string, object> transform);
        Task DeleteObject(string objectId);
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

        public void Close() { }
    }

    // ゲームサーバー用。サーバー側とまだ決めていない部分は次のように仮定している (README 参照):
    //   - メッセージは JSON の { type, payload, requestId }。返事は { type: 'ack' | 'error', payload: { requestId, ... } }
    //   - session.join { code, name } → ack { sessionId, playerId, token }
    //   - GLB は POST {httpBase}/assets (body = GLB) → { assetUrl }
    //   - object.create { assetUrl, transform } → ack { objectId }   (サーバーが object.created を配る)
    //   - object.update { objectId, assetUrl } / object.transform { objectId, transform } / object.delete { objectId }
    public class ServerTarget : IPostTarget
    {
        public readonly string url;
        readonly string httpBase;
        ClientWebSocket ws;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending
            = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        int seq;
        public string code, sessionId, playerId, token;
        public Action onClose;

        public ServerTarget(string url)
        {
            this.url = url;
            var u = new Uri(url);
            if (u.Scheme != "ws" && u.Scheme != "wss") throw new Exception("サーバーの URL は ws:// か wss:// で始めてください");
            httpBase = $"{(u.Scheme == "wss" ? "https" : "http")}://{u.Authority}";
        }

        public string Kind => "ws";
        public string Label => code != null ? $"部屋 {code}" : "サーバー";

        public async Task Join(string code, string name)
        {
            ws = new ClientWebSocket();
            var timeout = new CancellationTokenSource(6000);
            try { await ws.ConnectAsync(new Uri(url), timeout.Token); }
            catch (Exception) { throw new Exception("サーバーに接続できません"); }
            _ = ReceiveLoop();
            var res = await Request("session.join", new Dictionary<string, object> { ["code"] = code, ["name"] = name });
            sessionId = res.Str("sessionId");
            playerId = res.Str("playerId");
            token = res.Str("token");
            this.code = res.Str("code") ?? code;
        }

        async Task ReceiveLoop()
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    string text = sb.ToString();
                    sb.Clear();
                    OnMessage(text);
                }
            }
            catch (Exception) { /* 切れた */ }
            foreach (var p in pending.Values) p.TrySetException(new Exception("接続が切れました"));
            pending.Clear();
            onClose?.Invoke();
        }

        void OnMessage(string text)
        {
            Dictionary<string, object> msg;
            try { msg = MiniJson.Parse(text) as Dictionary<string, object>; } catch { return; }
            string type = msg.Str("type");
            if (type != "ack" && type != "error") return; // ほかのイベント (game.snapshot など) はここでは使わない
            var payload = msg.TryGetValue("payload", out var pl) ? pl as Dictionary<string, object> : null;
            if (payload == null || !int.TryParse(payload.Str("requestId"), out int id)) return;
            if (!pending.TryGetValue(id, out var tcs)) return;
            pending.Remove(id);
            if (type == "ack") tcs.TrySetResult(payload);
            else tcs.TrySetException(new Exception(payload.Str("message") ?? "サーバーでエラーになりました"));
        }

        public async Task<Dictionary<string, object>> Request(string type, Dictionary<string, object> payload, int timeoutMs = 8000)
        {
            if (ws == null || ws.State != WebSocketState.Open) throw new Exception("サーバーにつながっていません");
            int id = ++seq;
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            pending[id] = tcs;
            string json = MiniJson.Write(new Dictionary<string, object> { ["type"] = type, ["payload"] = payload, ["requestId"] = id });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, cts.Token);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (done != tcs.Task)
            {
                pending.Remove(id);
                throw new Exception("サーバーから応答がありません");
            }
            return await tcs.Task;
        }

        public async Task<string> UploadAsset(byte[] glb)
        {
            using var req = new UnityWebRequest($"{httpBase}/assets", "POST");
            req.uploadHandler = new UploadHandlerRaw(glb) { contentType = "model/gltf-binary" };
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "model/gltf-binary");
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success) throw new Exception($"アップロードに失敗しました ({req.responseCode})");
            var res = MiniJson.Parse(req.downloadHandler.text) as Dictionary<string, object>;
            string assetUrl = res.Str("assetUrl");
            if (string.IsNullOrEmpty(assetUrl)) throw new Exception("アップロード先から asset_url が返りませんでした");
            return assetUrl;
        }

        public async Task<string> CreateObject(string assetUrl, Dictionary<string, object> transform)
        {
            var res = await Request("object.create", new Dictionary<string, object> { ["assetUrl"] = assetUrl, ["transform"] = transform });
            return res.Str("objectId");
        }

        public async Task UpdateObject(string objectId, string assetUrl, Dictionary<string, object> transform)
        {
            await Request("object.update", new Dictionary<string, object> { ["objectId"] = objectId, ["assetUrl"] = assetUrl });
            if (transform != null) await Request("object.transform", new Dictionary<string, object> { ["objectId"] = objectId, ["transform"] = transform });
        }

        public Task DeleteObject(string objectId)
            => Request("object.delete", new Dictionary<string, object> { ["objectId"] = objectId });

        public void Close()
        {
            onClose = null;
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
