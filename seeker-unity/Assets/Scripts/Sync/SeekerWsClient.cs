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
    /// サーバーが意図して閉じた場合（認証失敗・参加者でない・別の接続に置き換え）は再接続しない（Rejected）。
    /// </summary>
    public sealed class SeekerWsClient : IDisposable
    {
        private const float InitialBackoffSeconds = 1f;
        private const float MaxBackoffSeconds = 10f;

        private readonly SyncConnection connection;
        private WebSocket socket;
        private bool isDisposed;

        public event Action<string> MessageReceived;
        public event Action<bool> ConnectionChanged;
        /// <summary>サーバーに拒否されて接続を諦めたときに1度だけ呼ばれる。</summary>
        public event Action Rejected;

        public SeekerWsClient(SyncConnection connection)
        {
            this.connection = connection;
        }

        public async void Start()
        {
            var backoff = InitialBackoffSeconds;
            while (!isDisposed)
            {
                var hasOpened = false;
                var closeCode = WebSocketCloseCode.Abnormal;
                socket = new WebSocket(connection.WsUrl);
                socket.OnOpen += () =>
                {
                    hasOpened = true;
                    SendAuth();
                    ConnectionChanged?.Invoke(true);
                };
                socket.OnClose += code => closeCode = code;
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

                if (hasOpened && IsServerRejection(closeCode))
                {
                    Debug.LogError("[SeekerWs] サーバーに接続を拒否された（認証失敗・参加者でない・セッション無し・別接続に置換）。再接続しない");
                    Rejected?.Invoke();
                    break;
                }

                // 一度でも繋がったなら次は短い待ちから、繋がらなかったなら指数的に延ばす
                backoff = hasOpened ? InitialBackoffSeconds : Mathf.Min(backoff * 2f, MaxBackoffSeconds);
                await Task.Delay(TimeSpan.FromSeconds(backoff));
            }
        }

        /// <summary>
        /// サーバー独自の close code（4001/4003/4004/4009）は NativeWebSocket が値を保持せず Undefined(1004) に丸める。
        /// 区別できないので、接続できたあとの Undefined をまとめて「再接続しても直らない拒否」として扱う。
        /// ネットワーク断は Abnormal(1006) になり、こちらは再接続の対象。
        /// </summary>
        private static bool IsServerRejection(WebSocketCloseCode closeCode) => closeCode == WebSocketCloseCode.Undefined;

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

        /// <summary>Open のときだけ送る。未接続・切断中は黙って捨てる（Pose は次の値で置き換わるため再送しない）。</summary>
        public void SendText(string json)
        {
            if (socket == null || socket.State != WebSocketState.Open) return;
            _ = socket.SendText(json);
        }

        private void SendAuth()
        {
            var json = JsonConvert.SerializeObject(new
            {
                type = "auth",
                payload = new { accessToken = connection.AccessToken, sessionId = connection.SessionId },
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
