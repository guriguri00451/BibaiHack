using System;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using Newtonsoft.Json;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>
    /// WS接続・auth送信・切断時の自動再接続。再接続のたびに新しいソケットで auth し直すので、
    /// サーバーから session.snapshot が再送され、状態は SyncedObjectStore 側で作り直される。
    /// </summary>
    public sealed class SeekerWsClient : IDisposable
    {
        private const float InitialBackoffSeconds = 1f;
        private const float MaxBackoffSeconds = 10f;

        private readonly string url;
        private readonly string accessToken;
        private readonly string sessionId;
        private WebSocket socket;
        private bool isDisposed;

        public event Action<string> MessageReceived;
        public event Action<bool> ConnectionChanged;

        public SeekerWsClient(string url, string accessToken, string sessionId)
        {
            this.url = url;
            this.accessToken = accessToken;
            this.sessionId = sessionId;
        }

        public async void Start()
        {
            var backoff = InitialBackoffSeconds;
            while (!isDisposed)
            {
                var hasOpened = false;
                socket = new WebSocket(url);
                socket.OnOpen += () =>
                {
                    hasOpened = true;
                    SendAuth();
                    ConnectionChanged?.Invoke(true);
                };
                socket.OnError += message => Debug.LogWarning($"[SeekerWs] error: {message}");
                socket.OnMessage += bytes => MessageReceived?.Invoke(Encoding.UTF8.GetString(bytes));

                try
                {
                    // Connect() は接続が閉じるまで戻らない
                    await socket.Connect();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[SeekerWs] connect failed: {exception.Message}");
                }

                if (hasOpened) ConnectionChanged?.Invoke(false);
                if (isDisposed) break;

                // 一度でも繋がったなら次は短い待ちから、繋がらなかったなら指数的に延ばす
                backoff = hasOpened ? InitialBackoffSeconds : Mathf.Min(backoff * 2f, MaxBackoffSeconds);
                await Task.Delay(TimeSpan.FromSeconds(backoff));
            }
        }

        /// <summary>
        /// 受信コールバックはここからメインスレッドで呼ばれる（NativeWebSocket が受信を内部キューに積み、
        /// このメソッドで吐き出す）。そのため GameObject 操作用の別キューは持たない。毎フレーム呼ぶこと。
        /// </summary>
        public void DispatchMessages()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            socket?.DispatchMessageQueue();
#endif
        }

        private void SendAuth()
        {
            var json = JsonConvert.SerializeObject(new
            {
                type = "auth",
                payload = new { accessToken, sessionId },
            });
            _ = socket.SendText(json);
        }

        public async void Dispose()
        {
            isDisposed = true;
            if (socket != null && socket.State == WebSocketState.Open) await socket.Close();
        }
    }
}
